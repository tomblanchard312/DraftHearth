using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using System.Text.RegularExpressions;

namespace WordCraft.Vba.Ole;

/// <summary>
/// Reads the VBA project ([MS-OVBA]) out of a Word file: .docm/.dotm (word/vbaProject.bin inside
/// the package), legacy .doc/.dot (the Macros storage) or a bare vbaProject.bin.
/// </summary>
internal static class CodePages
{
    /// <summary>VBA stores source in the project's ANSI code page (1252, 932, 1251…).</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
}

public static class VbaProjectReader
{
    public static IReadOnlyList<VbaModule> ReadFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (CompoundFileReader.IsCompoundFile(bytes)) return Read(new CompoundFileReader(bytes));
        if (bytes.Length > 4 && bytes[0] == 'P' && bytes[1] == 'K')
        {
            var bin = ExtractFromPackage(bytes) ?? throw new CompoundFileException($"{Path.GetFileName(path)} has no VBA project (it isn't macro-enabled).");
            return Read(new CompoundFileReader(bin));
        }
        throw new CompoundFileException($"{Path.GetFileName(path)} is neither an Office package nor a compound file.");
    }

    /// <summary>The vbaProject.bin part, found through the main document's relationships.</summary>
    public static byte[]? ExtractFromPackage(byte[] package)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        string? target = null;
        var rels = zip.GetEntry("word/_rels/document.xml.rels");
        if (rels is not null)
        {
            using var s = rels.Open();
            var rel = XDocument.Load(s).Root?.Elements()
                .FirstOrDefault(e => (string?)e.Attribute("Type") == VbaPackage.RelationshipType);
            if (rel?.Attribute("Target")?.Value is string t) target = "word/" + t.TrimStart('/');
        }
        var entry = (target is null ? null : zip.GetEntry(target))
            ?? zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        if (entry.Length > 256L * 1024 * 1024) throw new CompoundFileException("vbaProject.bin is too large.");
        using var es = entry.Open();
        using var ms = new MemoryStream();
        es.CopyTo(ms);
        return ms.ToArray();
    }

    public static IReadOnlyList<VbaModule> Read(CompoundFileReader cfb)
    {
        // vbaProject.bin keeps VBA/ at the root; a .doc keeps it under Macros/.
        string prefix = cfb.Find("VBA/dir") is not null ? "" : cfb.Find("Macros/VBA/dir") is not null ? "Macros/" : throw new CompoundFileException("No VBA project in this file.");
        var dir = OvbaCompression.Decompress(cfb.Read(prefix + "VBA/dir"));
        var (codePage, modules) = ParseDir(dir);
        var encoding = Encoding.GetEncoding(codePage);
        var kinds = ProjectKinds(cfb.Find(prefix + "PROJECT") is { } p ? encoding.GetString(cfb.Read(p)) : "");

        var result = new List<VbaModule>();
        foreach (var m in modules)
        {
            var stream = cfb.Find($"{prefix}VBA/{m.StreamName}") ?? throw new CompoundFileException($"Module stream {m.StreamName} is missing.");
            var raw = cfb.Read(stream);
            if (m.Offset > raw.Length) throw new CompoundFileException($"Module {m.Name}: source offset past the stream.");
            var source = encoding.GetString(OvbaCompression.Decompress(raw.AsSpan((int)m.Offset)));
            var kind = kinds.TryGetValue(m.Name, out var k) ? k : m.Procedural ? VbaModuleKind.Standard : VbaModuleKind.Class;
            result.Add(new VbaModule(m.Name, kind, source));
        }
        return result;
    }

    internal sealed record ModuleRecord(string Name, string StreamName, uint Offset, bool Procedural, int OffsetField);

    /// <summary>The dir stream: a flat list of (id, size, data) records, [MS-OVBA] 2.3.4.2.</summary>
    internal static (int CodePage, List<ModuleRecord> Modules) ParseDir(byte[] dir)
    {
        int codePage = 1252;
        var modules = new List<ModuleRecord>();
        string? name = null, nameUnicode = null, stream = null, streamUnicode = null;
        uint offset = 0;
        int offsetField = -1;
        bool procedural = true;
        var latin1 = Encoding.Latin1;

        int pos = 0;
        while (pos + 6 <= dir.Length)
        {
            int id = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(pos));
            int size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(pos + 2)), (uint)(dir.Length - pos - 6));
            if (id == 0x0009) size = 6; // PROJECTVERSION's "size" is a reserved 4; its data is 6 bytes
            var data = dir.AsSpan(pos + 6, Math.Min(size, dir.Length - pos - 6));
            pos += 6 + size;
            switch (id)
            {
                case 0x0003 when data.Length >= 2: codePage = BinaryPrimitives.ReadUInt16LittleEndian(data); break;
                case 0x0019: name = latin1.GetString(data); nameUnicode = null; stream = streamUnicode = null; offset = 0; offsetField = -1; procedural = true; break;
                case 0x0047: nameUnicode = Encoding.Unicode.GetString(data); break;
                case 0x001A: stream = latin1.GetString(data); break;
                case 0x0032: streamUnicode = Encoding.Unicode.GetString(data); break;
                case 0x0031 when data.Length >= 4: offset = BinaryPrimitives.ReadUInt32LittleEndian(data); offsetField = pos - size; break;
                case 0x0021: procedural = true; break;
                case 0x0022: procedural = false; break;
                case 0x002B when name is not null:
                    modules.Add(new ModuleRecord(nameUnicode ?? name, streamUnicode ?? stream ?? name, offset, procedural, offsetField));
                    name = null;
                    break;
                case 0x0010: return (codePage, modules);
            }
        }
        return (codePage, modules);
    }

    /// <summary>Module kinds from the PROJECT stream: Module=, Class=, Document=, BaseClass= (a UserForm).</summary>
    static Dictionary<string, VbaModuleKind> ProjectKinds(string project)
    {
        var kinds = new Dictionary<string, VbaModuleKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in project.Split('\n').Select(l => l.Trim()))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq];
            var value = line[(eq + 1)..].Split('/')[0];
            var kind = key switch
            {
                "Module" => VbaModuleKind.Standard,
                "Class" => VbaModuleKind.Class,
                "Document" => VbaModuleKind.Document,
                "BaseClass" => VbaModuleKind.Form,
                _ => (VbaModuleKind?)null,
            };
            if (kind is not null) kinds[value] = kind.Value;
            if (line.StartsWith('[')) break; // [Host Extender Info] and [Workspace] follow
        }
        return kinds;
    }
}

/// <summary>
/// Writes a vbaProject.bin from module sources and puts it into a .docx to make a .docm.
/// The project has no compiled p-code (_VBA_PROJECT version 0xFFFF), which tells the host to
/// compile from source. It carries no protection records (CMG/DPB/GC), so it hasn't been checked
/// against Word yet: it is used for test fixtures and is the start of writing macros back.
/// </summary>
public static class VbaPackage
{
    public const string RelationshipType = "http://schemas.microsoft.com/office/2006/relationships/vbaProject";
    const string MacroMain = "application/vnd.ms-word.document.macroEnabled.main+xml";
    const string DocxMain = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";

    public static byte[] BuildProject(IEnumerable<VbaModule> modules, string projectName = "Project", int codePage = 1252)
    {
        var list = modules.ToList();
        var encoding = Encoding.GetEncoding(codePage);
        var cfb = new CompoundFileWriter();
        cfb.AddStream("VBA/_VBA_PROJECT", [0xCC, 0x61, 0xFF, 0xFF, 0x00, 0x00, 0x00]);
        cfb.AddStream("VBA/dir", OvbaCompression.Compress(Dir(list, projectName, codePage, encoding)));
        foreach (var m in list)
            cfb.AddStream("VBA/" + m.Name, OvbaCompression.Compress(encoding.GetBytes(WithName(m))));

        var project = new StringBuilder();
        project.Append("ID=\"{00000000-0000-0000-0000-000000000000}\"\r\n");
        foreach (var m in list)
            project.Append(m.Kind switch
            {
                VbaModuleKind.Document => $"Document={m.Name}/&H00000000\r\n",
                VbaModuleKind.Class => $"Class={m.Name}\r\n",
                VbaModuleKind.Form => $"BaseClass={m.Name}\r\n",
                _ => $"Module={m.Name}\r\n",
            });
        project.Append($"Name=\"{projectName}\"\r\nHelpContextID=\"0\"\r\nVersionCompatible32=\"393222000\"\r\n\r\n");
        project.Append("[Host Extender Info]\r\n&H00000001={3832D640-CF90-11CF-8E43-00A0C911005A};VBE;&H00000000\r\n\r\n[Workspace]\r\n");
        foreach (var m in list) project.Append($"{m.Name}=0, 0, 0, 0, C\r\n");
        cfb.AddStream("PROJECT", encoding.GetBytes(project.ToString()));
        cfb.AddStream("PROJECTwm", [.. list.SelectMany(m => encoding.GetBytes(m.Name).Append((byte)0).Concat(Encoding.Unicode.GetBytes(m.Name)).Concat(new byte[2])), 0, 0]);
        return cfb.ToArray();
    }

    internal static string WithName(VbaModule m)
    {
        string source = m.Source.TrimStart('\uFEFF');
        var attribute = Regex.Match(source, @"^Attribute\s+VB_Name\s*=\s*""([^""]+)""", RegexOptions.Multiline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        if (m.Kind == VbaModuleKind.Class && source.TrimStart().StartsWith("VERSION ", StringComparison.OrdinalIgnoreCase))
        {
            if (!attribute.Success) throw new CompoundFileException("An exported class module needs Attribute VB_Name.");
            source = source[attribute.Index..]; // Export-only VERSION/BEGIN properties are not module source.
        }
        if (attribute.Success)
        {
            if (!attribute.Groups[1].Value.Equals(m.Name, StringComparison.OrdinalIgnoreCase))
                throw new CompoundFileException("The module name and Attribute VB_Name disagree.");
            return source;
        }
        return $"Attribute VB_Name = \"{m.Name}\"\r\n{source}";
    }

    internal static byte[] Dir(List<VbaModule> modules, string projectName, int codePage, Encoding encoding)
    {
        var ms = new MemoryStream();
        void Rec(int id, byte[] data) { Span<byte> h = stackalloc byte[6]; BinaryPrimitives.WriteUInt16LittleEndian(h, (ushort)id); BinaryPrimitives.WriteUInt32LittleEndian(h[2..], (uint)data.Length); ms.Write(h); ms.Write(data); }
        byte[] U16(int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); return b; }
        byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }

        Rec(0x0001, U32(1));                 // PROJECTSYSKIND: Win32
        Rec(0x0002, U32(0x409));             // PROJECTLCID
        Rec(0x0014, U32(0x409));             // PROJECTLCIDINVOKE
        Rec(0x0003, U16(codePage));          // PROJECTCODEPAGE
        Rec(0x0004, encoding.GetBytes(projectName));
        Rec(0x0005, []); Rec(0x0040, []);    // PROJECTDOCSTRING (+ Unicode)
        Rec(0x0006, []); Rec(0x003D, []);    // PROJECTHELPFILEPATH (+ second path)
        Rec(0x0007, U32(0));                 // PROJECTHELPCONTEXT
        Rec(0x0008, U32(0));                 // PROJECTLIBFLAGS
        ms.Write([0x09, 0x00, 0x04, 0x00, 0x00, 0x00]); ms.Write(U32(1)); ms.Write(U16(0)); // PROJECTVERSION
        Rec(0x000C, []); Rec(0x003C, []);    // PROJECTCONSTANTS (+ Unicode)
        Rec(0x000F, U16(modules.Count));     // PROJECTMODULES
        Rec(0x0013, U16(0xFFFF));            // PROJECTCOOKIE
        foreach (var m in modules)
        {
            Rec(0x0019, encoding.GetBytes(m.Name));
            Rec(0x0047, Encoding.Unicode.GetBytes(m.Name));
            Rec(0x001A, encoding.GetBytes(m.Name));
            Rec(0x0032, Encoding.Unicode.GetBytes(m.Name));
            Rec(0x001C, []); Rec(0x0048, []); // MODULEDOCSTRING (+ Unicode)
            Rec(0x0031, U32(0));              // MODULEOFFSET: source starts the stream (no p-code)
            Rec(0x001E, U32(0));              // MODULEHELPCONTEXT
            Rec(0x002C, U16(0xFFFF));         // MODULECOOKIE
            Rec(m.Kind == VbaModuleKind.Standard ? 0x0021 : 0x0022, []);
            Rec(0x002B, []);                  // module terminator
        }
        Rec(0x0010, []);                      // dir terminator
        return ms.ToArray();
    }

    /// <summary>Copies a .docx to a .docm that carries this VBA project.</summary>
    public static void MakeMacroEnabled(string docxPath, byte[] vbaProject, string docmPath)
    {
        File.Copy(docxPath, docmPath, overwrite: true);
        using var zip = ZipFile.Open(docmPath, ZipArchiveMode.Update);

        var types = Load(zip, "[Content_Types].xml");
        XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
        foreach (var o in types.Root!.Elements(ct + "Override").Where(o => (string?)o.Attribute("ContentType") == DocxMain))
            o.SetAttributeValue("ContentType", MacroMain);
        if (!types.Root.Elements(ct + "Default").Any(d => (string?)d.Attribute("Extension") == "bin"))
            types.Root.AddFirst(new XElement(ct + "Default", new XAttribute("Extension", "bin"), new XAttribute("ContentType", "application/vnd.ms-office.vbaProject")));
        Save(zip, "[Content_Types].xml", types);

        var rels = Load(zip, "word/_rels/document.xml.rels");
        XNamespace pr = "http://schemas.openxmlformats.org/package/2006/relationships";
        rels.Root!.Elements(pr + "Relationship").Where(r => (string?)r.Attribute("Type") == RelationshipType).Remove();
        rels.Root.Add(new XElement(pr + "Relationship", new XAttribute("Id", "rIdVbaProject"),
            new XAttribute("Type", RelationshipType), new XAttribute("Target", "vbaProject.bin")));
        Save(zip, "word/_rels/document.xml.rels", rels);

        zip.GetEntry("word/vbaProject.bin")?.Delete();
        using var s = zip.CreateEntry("word/vbaProject.bin").Open();
        s.Write(vbaProject);
    }

    static XDocument Load(ZipArchive zip, string name)
    {
        using var s = (zip.GetEntry(name) ?? throw new CompoundFileException($"The package has no {name}.")).Open();
        return XDocument.Load(s);
    }

    static void Save(ZipArchive zip, string name, XDocument doc)
    {
        zip.GetEntry(name)?.Delete();
        using var s = zip.CreateEntry(name).Open();
        doc.Save(s);
    }
}
