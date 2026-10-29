using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace WordCraft.Vba.Ole;

/// <summary>Replace module source while preserving project records, references and designer streams.</summary>
public static class VbaProjectEditor
{
    readonly record struct Record(int Id, int Start, int Data, int Length);

    // Unknown records stay opaque. A format we cannot walk is refused, never rebuilt approximately.
    static List<Record> Records(byte[] bytes)
    {
        var records = new List<Record>();
        int pos = 0;
        while (pos < bytes.Length)
        {
            if (bytes.Length - pos < 6) throw new CompoundFileException("Truncated VBA directory record.");
            int id = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pos));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 2));
            if (id == 0x0009) length = 6; // PROJECTVERSION has a reserved size of four, then six data bytes.
            if (length > bytes.Length - pos - 6) throw new CompoundFileException("Unsupported or truncated VBA directory record.");
            records.Add(new Record(id, pos, pos + 6, (int)length));
            pos += 6 + (int)length;
        }
        return records;
    }

    public static byte[] Update(byte[] original, IReadOnlyList<VbaModule> edits)
    {
        if (edits.Count > 256 || edits.Sum(m => (long)m.Source.Length) > 2 * 1024 * 1024)
            throw new CompoundFileException("VBA source exceeds the editor limit.");
        if (edits.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != edits.Count ||
            edits.Any(m => m.Name.Length is 0 or > 128 || m.Name.Any(c => char.IsControl(c) || c is '/' or '\\')))
            throw new CompoundFileException("Use unique VBA module names of at most 31 letters, digits or underscores.");
        if (original.Length == 0)
        {
            // Refuse replacement characters before the original writer encodes source.
            var encoding = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            foreach (var edit in edits)
            {
                if (edit.Name.Length > 31 || !char.IsLetter(edit.Name[0]) || edit.Name.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
                    throw new CompoundFileException("New modules need valid VBA names of at most 31 characters.");
                _ = encoding.GetBytes(VbaPackage.WithName(edit));
            }
            return VbaPackage.BuildProject(edits);
        }
        var cfb = new CompoundFileReader(original);
        var dir = OvbaCompression.Decompress(cfb.Read("VBA/dir"));
        var records = Records(dir);
        var (codePage, existing) = VbaProjectReader.ParseDir(dir);
        if (existing.Any(m => m.OffsetField < 0)) throw new CompoundFileException("A VBA module has no source offset.");
        var encodingStrict = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var sources = VbaProjectReader.Read(cfb).ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        if (edits.All(m => sources.TryGetValue(m.Name, out var old) && old.Kind == m.Kind && old.Source == VbaPackage.WithName(m)))
            return original;
        var changes = edits.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        var replacements = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in existing)
        {
            // All modules lose compiled caches together, so source and p-code can never disagree.
            var source = changes.TryGetValue(module.Name, out var edit) ? edit : sources[module.Name];
            if (source.Kind != sources[module.Name].Kind) throw new CompoundFileException("Changing an existing module's kind is unsupported.");
            var normalized = VbaPackage.WithName(source);
            var raw = cfb.Read("VBA/" + module.StreamName);
            replacements["VBA/" + module.StreamName] = normalized == sources[module.Name].Source
                ? raw.AsSpan(checked((int)module.Offset)).ToArray()
                : OvbaCompression.Compress(encodingStrict.GetBytes(normalized));
            BinaryPrimitives.WriteUInt32LittleEndian(dir.AsSpan(module.OffsetField), 0);
        }
        var additions = edits.Where(e => !sources.ContainsKey(e.Name)).ToList();
        if (additions.Any(m => m.Name.Length > 31 || !char.IsLetter(m.Name[0]) || m.Name.Any(c => !char.IsLetterOrDigit(c) && c != '_')))
            throw new CompoundFileException("New modules need valid VBA names of at most 31 characters.");
        if (additions.Any(m => m.Kind is not (VbaModuleKind.Standard or VbaModuleKind.Class)))
            throw new CompoundFileException("New document modules and form designers require host integration.");
        if (existing.Count + additions.Count > 256) throw new CompoundFileException("Too many VBA modules.");
        foreach (var module in additions)
        {
            if (cfb.Find("VBA/" + module.Name) is not null) throw new CompoundFileException("The module stream name already exists.");
            replacements["VBA/" + module.Name] = OvbaCompression.Compress(encodingStrict.GetBytes(VbaPackage.WithName(module)));
        }
        if (additions.Count > 0)
        {
            var counts = records.Where(r => r.Id == 0x000F && r.Length == 2).ToList();
            var ends = records.Where(r => r.Id == 0x0010 && r.Length == 0).ToList();
            if (counts.Count != 1 || ends.Count != 1 || ends[0].Start != dir.Length - 6)
                throw new CompoundFileException("Unsupported VBA module directory layout.");
            BinaryPrimitives.WriteUInt16LittleEndian(dir.AsSpan(counts[0].Data), checked((ushort)(existing.Count + additions.Count)));
            var added = VbaPackage.Dir(additions, "Project", codePage, encodingStrict);
            var addedRecords = Records(added);
            int start = addedRecords.First(r => r.Id == 0x0019).Start;
            int end = addedRecords.Last(r => r.Id == 0x0010).Start;
            dir = [.. dir.AsSpan(0, ends[0].Start), .. added.AsSpan(start, end - start), .. dir.AsSpan(ends[0].Start)];
            var project = encodingStrict.GetString(cfb.Read("PROJECT"));
            var section = Regex.Match(project, @"^\[", RegexOptions.Multiline, TimeSpan.FromSeconds(1));
            int insert = section.Success ? section.Index : project.Length;
            var lines = string.Concat(additions.Select(m => $"{(m.Kind == VbaModuleKind.Class ? "Class" : "Module")}={m.Name}\r\n"));
            if (insert > 0 && project[insert - 1] != '\n') lines = "\r\n" + lines;
            replacements["PROJECT"] = encodingStrict.GetBytes(project.Insert(insert, lines));
            var wm = cfb.Find("PROJECTwm") is { } names ? cfb.Read(names) : [0, 0];
            if (wm.Length < 2 || wm[^1] != 0 || wm[^2] != 0) throw new CompoundFileException("Unsupported PROJECTwm name map.");
            var extra = additions.SelectMany(m => encodingStrict.GetBytes(m.Name).Append((byte)0).Concat(Encoding.Unicode.GetBytes(m.Name)).Concat(new byte[2]));
            replacements["PROJECTwm"] = [.. wm.AsSpan(0, wm.Length - 2), .. extra, 0, 0];
        }
        replacements["VBA/dir"] = OvbaCompression.Compress(dir);
        // [MS-OVBA] 2.3.4.1: source-only output, version FFFF and no performance cache.
        replacements["VBA/_VBA_PROJECT"] = [0xCC, 0x61, 0xFF, 0xFF, 0, 0, 0];
        var output = new CompoundFileWriter(cfb.Root);
        void Copy(CfbEntry parent, string prefix, int depth)
        {
            if (depth > 64) throw new CompoundFileException("VBA storage nesting exceeds the limit.");
            foreach (var entry in parent.Children)
            {
                string path = prefix + entry.Name;
                if (entry.IsStorage) { output.AddStorage(path, entry); Copy(entry, path + "/", depth + 1); }
                else if (!path.StartsWith("VBA/__SRP_", StringComparison.OrdinalIgnoreCase))
                {
                    output.AddStream(path, replacements.Remove(path, out var changed) ? changed : cfb.Read(entry), entry);
                }
            }
        }
        Copy(cfb.Root, "", 0);
        foreach (var (path, bytes) in replacements) output.AddStream(path, bytes);
        var result = output.ToArray();
        var roundTrip = VbaProjectReader.Read(new CompoundFileReader(result));
        foreach (var edit in edits)
            if (roundTrip.FirstOrDefault(m => m.Name.Equals(edit.Name, StringComparison.OrdinalIgnoreCase))?.Source != VbaPackage.WithName(edit))
                throw new CompoundFileException("The saved VBA source did not round-trip exactly.");
        return result;
    }
}
