using System.Buffers.Binary;
using System.Text;
using WordCraft.Vba;
using WordCraft.Vba.Ole;

static class ProjectEditorTests
{
    static void Equal(byte[] a, byte[] b, string what) { if (!a.SequenceEqual(b)) throw new Exception(what); }
    static VbaModule Module(string name, string source) => new(name, VbaModuleKind.Standard, $"Attribute VB_Name = \"{name}\"\r\n{source}");

    public static void Updating_source_preserves_references_designers_and_directory_metadata()
    {
        var originalModule = Module("Module1", "Sub Main()\r\nEnd Sub\r\n");
        var original = new CompoundFileReader(VbaPackage.BuildProject([originalModule]));
        var metadata = new CfbEntry { Name = "Form1", IsStorage = true, Metadata = Enumerable.Range(0, 36).Select(n => (byte)n).ToArray() };
        var writer = new CompoundFileWriter(metadata);
        foreach (var child in original.Root.Children)
        {
            if (child.IsStorage)
            {
                foreach (var stream in child.Children)
                {
                    var bytes = original.Read(stream);
                    if (stream.Name == "dir")
                    {
                        var dir = OvbaCompression.Decompress(bytes);
                        int at = dir.AsSpan().IndexOf(new byte[] { 0x0F, 0, 2, 0, 0, 0, 1, 0 });
                        if (at < 0) throw new Exception("No module count");
                        // Opaque reference payload: its bytes must survive source replacement.
                        dir = [.. dir.AsSpan(0, at), 0x0D, 0, 3, 0, 0, 0, 11, 22, 33, .. dir.AsSpan(at)];
                        bytes = OvbaCompression.Compress(dir);
                    }
                    writer.AddStream(child.Name + "/" + stream.Name, bytes);
                }
            }
            else writer.AddStream(child.Name, original.Read(child));
        }
        writer.AddStorage("Form1", metadata);
        writer.AddStorage("EmptyDesigner", metadata);
        writer.AddStream("Form1/o", [8, 7, 6], metadata);
        writer.AddStream("VBA/__SRP_0", [99]);
        writer.AddStream("ReferenceExtension", [42, 43]);
        var input = writer.ToArray();
        var edited = Module("Module1", "Sub Main()\r\n    Debug.Print \"é\"\r\nEnd Sub\r\n");
        var updated = VbaProjectEditor.Update(input, [edited]);
        var before = new CompoundFileReader(input);
        var after = new CompoundFileReader(updated);
        Equal(before.Root.Metadata, after.Root.Metadata, "root metadata changed");
        Equal(before.Find("Form1")!.Metadata, after.Find("Form1")!.Metadata, "designer CLSID/state changed");
        Equal(before.Find("Form1/o")!.Metadata, after.Find("Form1/o")!.Metadata, "stream metadata changed");
        Equal(before.Read("Form1/o"), after.Read("Form1/o"), "designer data changed");
        Equal(before.Read("ReferenceExtension"), after.Read("ReferenceExtension"), "opaque stream changed");
        Equal(before.Read("PROJECT"), after.Read("PROJECT"), "project references/settings changed");
        Equal(OvbaCompression.Decompress(before.Read("VBA/dir")), OvbaCompression.Decompress(after.Read("VBA/dir")), "directory/reference records changed");
        if (after.Find("EmptyDesigner") is null || after.Find("VBA/__SRP_0") is not null) throw new Exception("empty storage or cache handling");
        Equal([0xCC, 0x61, 0xFF, 0xFF, 0, 0, 0], after.Read("VBA/_VBA_PROJECT"), "compiled cache retained");
        if (VbaProjectReader.Read(after).Single().Source != edited.Source) throw new Exception("source mismatch");
    }

    public static void No_op_preserves_project_bytes_and_comment_text_is_not_an_attribute()
    {
        var module = new VbaModule("Original", VbaModuleKind.Standard, "' Attribute VB_Name appears in a comment\r\nSub Main()\r\nEnd Sub\r\n");
        var bytes = VbaPackage.BuildProject([module]);
        var source = VbaProjectReader.Read(new CompoundFileReader(bytes)).Single();
        if (!source.Source.StartsWith("Attribute VB_Name = \"Original\"\r\n")) throw new Exception("missing real module attribute");
        Equal(bytes, VbaProjectEditor.Update(bytes, [source]), "no-op save changed the project");
        var bom = source with { Source = "\uFEFF" + source.Source };
        var roundTrip = VbaProjectReader.Read(new CompoundFileReader(VbaPackage.BuildProject([bom]))).Single();
        if (roundTrip.Source != source.Source) throw new Exception("UTF-8 BOM was stored as source or duplicated the attribute");
    }

    public static void Exported_class_headers_are_not_stored_as_executable_source()
    {
        var source = "VERSION 1.0 CLASS\r\nBEGIN\r\n    MultiUse = -1\r\nEND\r\nAttribute VB_Name = \"ClassOne\"\r\nAttribute VB_PredeclaredId = False\r\nOption Explicit\r\n";
        var module = new VbaModule("ClassOne", VbaModuleKind.Class, source);
        var bytes = VbaProjectEditor.Update([], [module]);
        var saved = VbaProjectReader.Read(new CompoundFileReader(bytes)).Single();
        if (saved.Kind != VbaModuleKind.Class || !saved.Source.StartsWith("Attribute VB_Name") || !saved.Source.Contains("Attribute VB_PredeclaredId"))
            throw new Exception("export wrapper was retained or class attributes lost");
    }

    public static void Adding_modules_preserves_existing_source_and_rejects_lossy_encoding()
    {
        var a = Module("Module1", "Sub Main()\r\nEnd Sub\r\n");
        var b = Module("Added", "Sub Extra()\r\nEnd Sub\r\n");
        var original = VbaPackage.BuildProject([a]);
        var updated = VbaProjectEditor.Update(original, [b]);
        var parsed = VbaProjectReader.Read(new CompoundFileReader(updated));
        if (parsed.Count != 2 || parsed.Single(m => m.Name == a.Name).Source != a.Source || parsed.Single(m => m.Name == b.Name).Source != b.Source)
            throw new Exception("module addition did not round-trip");
        try { VbaProjectEditor.Update(original, [Module("Module1", "' 日本語")]); throw new Exception("unrepresentable source was silently changed"); }
        catch (EncoderFallbackException) { }
        var bad = original.ToArray(); bad[0] = 0;
        try { VbaProjectEditor.Update(bad, [a]); throw new Exception("bad compound file accepted"); }
        catch (CompoundFileException) { }
    }
}
