using System.Text;
using System.Text.Json.Nodes;

namespace WordCraft.ObjectModel.Model;

/// <summary>A run of explicit character formatting inside one paragraph, in UTF-16 offsets.</summary>
internal readonly record struct RunInfo(int Start, int End, JsonObject Props);

/// <summary>One paragraph of the main story as Word's object model sees it.</summary>
internal sealed class ParaEntry
{
    public required int[] Path { get; init; }
    /// <summary>WordCraft's raw paragraph text: '\n' is a line break, U+FFFC an inline object.</summary>
    public required string Text { get; init; }
    public required string StyleId { get; init; }
    public JsonObject? Props { get; init; }
    public List<RunInfo> Runs { get; init; } = [];
    /// <summary>Word position of the first character.</summary>
    public int Start { get; set; }
    public int Length => Text.Length;
    /// <summary>Position of this paragraph's mark (Chr(13)).</summary>
    public int Mark => Start + Length;
    /// <summary>A table row ends after this paragraph: Word gives the end-of-row mark one position.</summary>
    public bool RowEndAfter { get; set; }
    public bool InTable => Path.Length > 1;
}

/// <summary>
/// Maps Word's character positions onto WordCraft's document positions.
///
/// Word addresses a story as one string: each paragraph's text followed by its paragraph mark
/// (one position, Chr(13)), counted in UTF-16 code units. WordCraft addresses
/// <c>{"story": "body", "path": [block, row, cell, block…], "off": utf8ByteOffset}</c>.
/// The map is rebuilt from <c>document.inspect</c> whenever the text changes.
/// </summary>
internal sealed class StoryMap
{
    public const char Mark = '\r';
    public const char WordLineBreak = '\v'; // Chr(11): Word's manual line break
    const char WcLineBreak = '\n';

    public List<ParaEntry> Paras { get; } = [];
    public int Length { get; private set; }
    public int SelAnchor { get; set; }
    public int SelFocus { get; set; }
    string? _text;

    public int SelStart => Math.Min(SelAnchor, SelFocus);
    public int SelEnd => Math.Max(SelAnchor, SelFocus);

    /// <summary>The whole story as Word's Range.Text would return it.</summary>
    public string Text => _text ??= BuildText();

    public static StoryMap Build(JsonNode inspect, Func<int[], string?> rawText)
    {
        var map = new StoryMap();
        foreach (var b in inspect["blocks"]?.AsArray() ?? [])
        {
            if (b is null) continue;
            int index = b["index"]?.GetValue<int>() ?? map.Paras.Count;
            if (b["type"]?.GetValue<string>() == "table")
                AddTable(map, b, index);
            else
                map.Paras.Add(ParagraphEntry(b, index, rawText));
        }
        if (map.Paras.Count == 0)
            map.Paras.Add(new ParaEntry { Path = [0], Text = "", StyleId = "Normal" });

        int pos = 0;
        foreach (var p in map.Paras)
        {
            p.Start = pos;
            pos += p.Length + 1 + (p.RowEndAfter ? 1 : 0);
        }
        map.Length = pos;

        var sel = inspect["selection"];
        map.SelAnchor = map.TryFromPos(sel?["anchor"]) ?? 0;
        map.SelFocus = map.TryFromPos(sel?["focus"]) ?? map.SelAnchor;
        return map;
    }

    static ParaEntry ParagraphEntry(JsonNode b, int index, Func<int[], string?> rawText)
    {
        int[] path = [index];
        var text = b["text"]?.GetValue<string>() ?? "";
        int byteLen = b["len"]?.GetValue<int>() ?? Encoding.UTF8.GetByteCount(text);
        // `text` is plain text (fields show their result). When it doesn't match the raw byte
        // length the paragraph holds objects, so ask for the raw text the positions refer to.
        if (Encoding.UTF8.GetByteCount(text) != byteLen && rawText(path) is string raw)
            text = raw;
        var runs = new List<RunInfo>();
        foreach (var r in b["runs"]?.AsArray() ?? [])
        {
            if (r?["props"] is not JsonObject props) continue;
            int s = BytesToChars(text, r["start"]?.GetValue<int>() ?? 0);
            int e = BytesToChars(text, r["end"]?.GetValue<int>() ?? 0);
            if (e > s) runs.Add(new RunInfo(s, e, props));
        }
        return new ParaEntry
        {
            Path = path,
            Text = text,
            StyleId = b["style"]?.GetValue<string>() ?? "Normal",
            Props = b["props"] as JsonObject,
            Runs = runs,
        };
    }

    static void AddTable(StoryMap map, JsonNode b, int index)
    {
        var rows = b["cells"]?.AsArray() ?? [];
        for (int r = 0; r < rows.Count; r++)
        {
            var cells = rows[r]?.AsArray() ?? [];
            ParaEntry? last = null;
            for (int c = 0; c < cells.Count; c++)
            {
                var paras = (cells[c]?.GetValue<string>() ?? "").Split('\n');
                for (int k = 0; k < paras.Length; k++)
                {
                    last = new ParaEntry { Path = [index, r, c, k], Text = paras[k], StyleId = "Normal" };
                    map.Paras.Add(last);
                }
            }
            if (last is not null) last.RowEndAfter = true;
        }
    }

    string BuildText()
    {
        var sb = new StringBuilder(Length);
        foreach (var p in Paras)
        {
            sb.Append(p.Text.Replace(WcLineBreak, WordLineBreak)).Append(Mark);
            if (p.RowEndAfter) sb.Append(Mark);
        }
        return sb.ToString();
    }

    public string Slice(int start, int end)
    {
        start = Clamp(start);
        end = Clamp(end);
        return end <= start ? "" : Text[start..end];
    }

    public int Clamp(int c) => Math.Clamp(c, 0, Length);

    /// <summary>The paragraph that owns position c (its text or its mark).</summary>
    public ParaEntry ParaAt(int c)
    {
        c = Clamp(c);
        foreach (var p in Paras)
            if (c <= p.Mark) return p; // a row-end mark belongs to the next row's first paragraph
        return Paras[^1];
    }

    /// <summary>Paragraphs that overlap [start, end); a collapsed range gives the paragraph it sits in.</summary>
    public IEnumerable<ParaEntry> ParasIn(int start, int end)
    {
        if (end <= start)
        {
            yield return ParaAt(start);
            yield break;
        }
        foreach (var p in Paras)
            if (p.Start < end && p.Mark + 1 > start) yield return p;
    }

    /// <summary>A WordCraft position for Word position c.</summary>
    public JsonObject ToPos(int c)
    {
        c = Clamp(c);
        var target = Paras[^1];
        int off = target.Length;
        for (int i = 0; i < Paras.Count; i++)
        {
            var p = Paras[i];
            if (c < p.Start) // a row-end mark: the start of the next row
            {
                target = p;
                off = 0;
                break;
            }
            if (c <= p.Mark)
            {
                target = p;
                off = c - p.Start;
                break;
            }
        }
        return new JsonObject
        {
            ["story"] = "body",
            ["path"] = new JsonArray(target.Path.Select(x => (JsonNode)x).ToArray()),
            ["off"] = CharsToBytes(target.Text, off),
        };
    }

    public int FromPos(JsonNode? pos) =>
        TryFromPos(pos) ?? throw new NotSupportedException($"Position {pos?.ToJsonString()} is outside the main story; headers, footers and notes aren't mapped yet.");

    public int? TryFromPos(JsonNode? pos)
    {
        if (pos is null) return null;
        if (pos["story"] is JsonNode story && story.GetValueKind() != System.Text.Json.JsonValueKind.String) return null;
        if (pos["story"]?.GetValue<string>() is string s && s != "body") return null;
        var path = pos["path"]?.AsArray().Select(x => x?.GetValue<int>() ?? 0).ToArray() ?? [0];
        int off = pos["off"]?.GetValue<int>() ?? 0;
        foreach (var p in Paras)
            if (p.Path.SequenceEqual(path))
                return p.Start + BytesToChars(p.Text, off);
        return null;
    }

    public static int CharsToBytes(string text, int chars)
    {
        chars = Math.Clamp(chars, 0, text.Length);
        if (chars > 0 && chars < text.Length && char.IsHighSurrogate(text[chars - 1])) chars--; // never split a pair
        return Encoding.UTF8.GetByteCount(text.AsSpan(0, chars));
    }

    public static int BytesToChars(string text, int bytes)
    {
        int seen = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (seen >= bytes) return i;
            int width = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 4 : Encoding.UTF8.GetByteCount(text.AsSpan(i, 1));
            if (width == 4) i++;
            seen += width;
        }
        return text.Length;
    }
}
