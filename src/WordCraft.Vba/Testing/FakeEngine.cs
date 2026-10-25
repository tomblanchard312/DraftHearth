using System.Text;
using System.Text.Json.Nodes;
using WordCraft.ObjectModel.Transport;

namespace WordCraft.Vba.Testing;

/// <summary>
/// An in-memory stand-in for WordCraft's engine that implements the commands the object model
/// sends, with the same JSON shapes (byte-offset positions, inspect blocks, CharProps runs).
/// It lets the object model and macros be tested without a WordCraft build; the real engine
/// remains the reference.
/// </summary>
public sealed class FakeEngine : IEngine
{
    sealed class Para
    {
        public StringBuilder Text = new();
        public List<string?> Chars = []; // CharProps JSON per UTF-16 unit, null = none
        public JsonObject Props = [];
        public string Style = "Normal";
    }

    readonly List<Para> _paras = [new()];
    (int P, int C) _anchor, _focus;
    bool _track;

    public List<string> Log { get; } = [];
    public string? SavedPath { get; private set; }

    public FakeEngine(params string[] paragraphs)
    {
        if (paragraphs.Length > 0) Load(paragraphs);
    }

    void Load(IEnumerable<string> paragraphs)
    {
        _paras.Clear();
        foreach (var t in paragraphs)
        {
            var p = new Para();
            p.Text.Append(t);
            p.Chars.AddRange(Enumerable.Repeat<string?>(null, t.Length));
            _paras.Add(p);
        }
        if (_paras.Count == 0) _paras.Add(new Para());
        _anchor = _focus = (0, 0);
    }

    /// <summary>Paragraph texts, for assertions.</summary>
    public IReadOnlyList<string> Paragraphs => _paras.Select(p => p.Text.ToString()).ToList();
    public string StyleOf(int paragraph) => _paras[paragraph].Style;
    public JsonObject ParaProps(int paragraph) => _paras[paragraph].Props;

    /// <summary>The CharProps at one character, for assertions.</summary>
    public JsonObject CharProps(int paragraph, int ch) =>
        _paras[paragraph].Chars[ch] is string j ? JsonNode.Parse(j)!.AsObject() : [];

    public JsonNode? Execute(string command, JsonObject? p = null)
    {
        Log.Add(command);
        p ??= [];
        switch (command)
        {
            case "document.inspect": return Inspect();
            case "document.paragraph": return new JsonObject { ["paragraph"] = new JsonObject { ["text"] = _paras[Path(p)].Text.ToString() } };
            case "select.range": _anchor = FromPos(p["anchor"]); _focus = FromPos(p["focus"]); return Sel();
            case "caret.set": Move(FromPos(p["pos"]), p); return Sel();
            case "select.all": _anchor = (0, 0); _focus = (_paras.Count - 1, _paras[^1].Text.Length); return Sel();
            case "select.collapse":
            {
                var (a, b) = Ordered();
                _anchor = _focus = p["end"]?.GetValue<bool>() == true ? b : a;
                return Sel();
            }
            case "caret.docStart": Move((0, 0), p); return Sel();
            case "caret.docEnd": Move((_paras.Count - 1, _paras[^1].Text.Length), p); return Sel();
            case "caret.home": Move((_focus.P, 0), p); return Sel();
            case "caret.end": Move((_focus.P, _paras[_focus.P].Text.Length), p); return Sel();
            case "caret.right": Move(Step(_focus, 1), p); return Sel();
            case "caret.left": Move(Step(_focus, -1), p); return Sel();
            case "caret.down" or "caret.paraDown": Move(_focus.P + 1 < _paras.Count ? (_focus.P + 1, 0) : (_focus.P, _paras[_focus.P].Text.Length), p); return Sel();
            case "caret.up" or "caret.paraUp": Move(_focus.C > 0 ? (_focus.P, 0) : (Math.Max(0, _focus.P - 1), 0), p); return Sel();
            case "caret.wordRight": Move(WordStep(_focus, 1), p); return Sel();
            case "caret.wordLeft": Move(WordStep(_focus, -1), p); return Sel();
            case "text.insert": DeleteSelection(); Insert(p["text"]?.GetValue<string>() ?? ""); return Sel();
            case "text.lineBreak": DeleteSelection(); Insert("\n"); return Sel();
            case "text.pageBreak": DeleteSelection(); Insert("\f"); return Sel();
            case "text.newParagraph": DeleteSelection(); Split(); return Sel();
            case "text.delete":
                if (_anchor != _focus) DeleteSelection();
                else { var next = Step(_focus, 1); if (next != _focus) DeleteRange(_focus, next); }
                return Sel();
            case "text.backspace":
                if (_anchor != _focus) DeleteSelection();
                else { var prev = Step(_focus, -1); if (prev != _focus) { DeleteRange(prev, _focus); _anchor = _focus = prev; } }
                return Sel();
            case "format.set": FormatSelection(p["props"]?.AsObject() ?? []); return Sel();
            case "para.set":
                foreach (var para in SelectedParas())
                    foreach (var (k, v) in p["props"]?.AsObject() ?? []) para.Props[k] = v?.DeepClone();
                return Sel();
            case "para.style":
                foreach (var para in SelectedParas()) para.Style = (p["style"]?.GetValue<string>() ?? "Normal").Replace(" ", "");
                return Sel();
            case "edit.replaceAll": return ReplaceAll(p);
            case "review.trackChanges": _track = p["value"]?.GetValue<bool>() ?? !_track; return new JsonObject { ["trackChanges"] = _track };
            case "file.new": Load([""]); SavedPath = null; return new JsonObject();
            case "file.open":
            {
                var path = p["path"]!.GetValue<string>();
                Load(File.ReadAllText(path).Replace("\r\n", "\n").Split('\n'));
                SavedPath = path;
                return new JsonObject();
            }
            case "file.save" or "file.saveAs":
            {
                var path = p["path"]?.GetValue<string>() ?? SavedPath ?? throw new EngineException(command, "the document has no file yet");
                File.WriteAllText(path, string.Join("\n", Paragraphs));
                SavedPath = path;
                return new JsonObject { ["path"] = path };
            }
            case "file.setAuthor" or "file.close": return new JsonObject();
            default: throw new EngineException(command, "not implemented by FakeEngine");
        }
    }

    // ---- JSON shapes ----------------------------------------------------------------------------

    JsonObject Inspect()
    {
        var blocks = new JsonArray();
        for (int i = 0; i < _paras.Count; i++)
        {
            var para = _paras[i];
            var text = para.Text.ToString();
            var block = new JsonObject
            {
                ["index"] = i, ["type"] = "paragraph", ["style"] = para.Style,
                ["len"] = Encoding.UTF8.GetByteCount(text), ["text"] = text,
            };
            var runs = new JsonArray();
            int c = 0;
            while (c < para.Chars.Count)
            {
                int e = c;
                while (e < para.Chars.Count && para.Chars[e] == para.Chars[c]) e++;
                if (para.Chars[c] is string j)
                    runs.Add(new JsonObject { ["start"] = Bytes(text, c), ["end"] = Bytes(text, e), ["props"] = JsonNode.Parse(j) });
                c = e;
            }
            if (runs.Count > 0) block["runs"] = runs;
            if (para.Props.Count > 0) block["props"] = para.Props.DeepClone();
            blocks.Add(block);
        }
        return new JsonObject
        {
            ["blocks"] = blocks,
            ["selection"] = new JsonObject { ["anchor"] = ToPos(_anchor), ["focus"] = ToPos(_focus), ["text"] = SelectedText() },
            ["trackChanges"] = _track,
        };
    }

    JsonObject Sel() => new() { ["anchor"] = ToPos(_anchor), ["focus"] = ToPos(_focus) };

    JsonObject ToPos((int P, int C) at) => new()
    {
        ["story"] = "body",
        ["path"] = new JsonArray(at.P),
        ["off"] = Bytes(_paras[at.P].Text.ToString(), at.C),
    };

    (int, int) FromPos(JsonNode? pos)
    {
        int p = Math.Clamp(pos?["path"]?[0]?.GetValue<int>() ?? 0, 0, _paras.Count - 1);
        var text = _paras[p].Text.ToString();
        int off = pos?["off"]?.GetValue<int>() ?? 0;
        int c = 0, seen = 0;
        while (c < text.Length && seen < off) { seen += Encoding.UTF8.GetByteCount(text.AsSpan(c, char.IsHighSurrogate(text[c]) ? 2 : 1)); c += char.IsHighSurrogate(text[c]) ? 2 : 1; }
        return (p, c);
    }

    static int Path(JsonObject p) => p["path"]?[0]?.GetValue<int>() ?? 0;
    static int Bytes(string text, int chars) => Encoding.UTF8.GetByteCount(text.AsSpan(0, Math.Min(chars, text.Length)));

    // ---- editing ----------------------------------------------------------------------------------

    void Move((int, int) to, JsonObject p)
    {
        _focus = to;
        if (p["extend"]?.GetValue<bool>() != true) _anchor = to;
    }

    ((int P, int C) A, (int P, int C) B) Ordered() => _anchor.CompareTo(_focus) <= 0 ? (_anchor, _focus) : (_focus, _anchor);

    (int, int) Step((int P, int C) at, int dir)
    {
        if (dir > 0) return at.C < _paras[at.P].Text.Length ? (at.P, at.C + 1) : at.P + 1 < _paras.Count ? (at.P + 1, 0) : at;
        return at.C > 0 ? (at.P, at.C - 1) : at.P > 0 ? (at.P - 1, _paras[at.P - 1].Text.Length) : at;
    }

    (int, int) WordStep((int P, int C) at, int dir)
    {
        var t = _paras[at.P].Text.ToString();
        int c = at.C;
        if (dir > 0)
        {
            if (c >= t.Length) return Step(at, 1);
            while (c < t.Length && char.IsLetterOrDigit(t[c])) c++;
            while (c < t.Length && t[c] == ' ') c++;
            return (at.P, c == at.C ? c + 1 : c);
        }
        if (c == 0) return Step(at, -1);
        while (c > 0 && t[c - 1] == ' ') c--;
        while (c > 0 && char.IsLetterOrDigit(t[c - 1])) c--;
        return (at.P, c);
    }

    string SelectedText()
    {
        var (a, b) = Ordered();
        var sb = new StringBuilder();
        for (int p = a.P; p <= b.P; p++)
        {
            var t = _paras[p].Text.ToString();
            int s = p == a.P ? a.C : 0, e = p == b.P ? b.C : t.Length;
            sb.Append(t, s, e - s);
            if (p < b.P) sb.Append('\r');
        }
        return sb.ToString();
    }

    IEnumerable<Para> SelectedParas()
    {
        var (a, b) = Ordered();
        int last = b.C == 0 && b.P > a.P ? b.P - 1 : b.P; // a selection ending at a paragraph start doesn't include it
        for (int p = a.P; p <= last; p++) yield return _paras[p];
    }

    void DeleteSelection()
    {
        if (_anchor == _focus) return;
        var (a, b) = Ordered();
        DeleteRange(a, b);
        _anchor = _focus = a;
    }

    void DeleteRange((int P, int C) a, (int P, int C) b)
    {
        if (a.P == b.P)
        {
            _paras[a.P].Text.Remove(a.C, b.C - a.C);
            _paras[a.P].Chars.RemoveRange(a.C, b.C - a.C);
            return;
        }
        var first = _paras[a.P];
        var last = _paras[b.P];
        first.Text.Remove(a.C, first.Text.Length - a.C);
        first.Chars.RemoveRange(a.C, first.Chars.Count - a.C);
        first.Text.Append(last.Text.ToString(b.C, last.Text.Length - b.C));
        first.Chars.AddRange(last.Chars.Skip(b.C));
        _paras.RemoveRange(a.P + 1, b.P - a.P);
    }

    void Insert(string text)
    {
        var para = _paras[_focus.P];
        // Typed text takes the formatting of the character before the caret, as in a word processor.
        string? props = _focus.C > 0 ? para.Chars[_focus.C - 1] : para.Chars.Count > 0 ? para.Chars[0] : null;
        para.Text.Insert(_focus.C, text);
        para.Chars.InsertRange(_focus.C, Enumerable.Repeat(props, text.Length));
        _focus = _anchor = (_focus.P, _focus.C + text.Length);
    }

    void Split()
    {
        var para = _paras[_focus.P];
        var next = new Para { Props = para.Props.DeepClone().AsObject(), Style = para.Style };
        next.Text.Append(para.Text.ToString(_focus.C, para.Text.Length - _focus.C));
        next.Chars.AddRange(para.Chars.Skip(_focus.C));
        para.Text.Remove(_focus.C, para.Text.Length - _focus.C);
        para.Chars.RemoveRange(_focus.C, para.Chars.Count - _focus.C);
        _paras.Insert(_focus.P + 1, next);
        _focus = _anchor = (_focus.P + 1, 0);
    }

    void FormatSelection(JsonObject props)
    {
        var (a, b) = Ordered();
        for (int p = a.P; p <= b.P; p++)
        {
            var para = _paras[p];
            int s = p == a.P ? a.C : 0, e = p == b.P ? b.C : para.Chars.Count;
            for (int c = s; c < e; c++)
            {
                var merged = para.Chars[c] is string j ? JsonNode.Parse(j)!.AsObject() : [];
                foreach (var (k, v) in props) merged[k] = v?.DeepClone();
                para.Chars[c] = merged.ToJsonString();
            }
        }
    }

    JsonObject ReplaceAll(JsonObject p)
    {
        if (p["wildcards"]?.GetValue<bool>() == true) throw new EngineException("edit.replaceAll", "FakeEngine has no wildcard support");
        var find = p["text"]?.GetValue<string>() ?? "";
        var with = (p["with"]?.GetValue<string>() ?? "").Replace("^^", "\u0001").Replace("^t", "\t").Replace("\u0001", "^");
        if (find.Length == 0 || find.Contains('^')) throw new EngineException("edit.replaceAll", "FakeEngine only replaces plain text");
        var cmp = p["matchCase"]?.GetValue<bool>() == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        bool whole = p["wholeWord"]?.GetValue<bool>() == true;
        int n = 0;
        foreach (var para in _paras)
        {
            int at = 0;
            while ((at = para.Text.ToString().IndexOf(find, at, cmp)) >= 0)
            {
                var t = para.Text.ToString();
                if (whole && ((at > 0 && char.IsLetterOrDigit(t[at - 1])) || (at + find.Length < t.Length && char.IsLetterOrDigit(t[at + find.Length]))))
                {
                    at += find.Length;
                    continue;
                }
                string? props = para.Chars[at];
                para.Text.Remove(at, find.Length).Insert(at, with);
                para.Chars.RemoveRange(at, find.Length);
                para.Chars.InsertRange(at, Enumerable.Repeat(props, with.Length));
                at += with.Length;
                n++;
            }
        }
        _anchor = (Math.Min(_anchor.P, _paras.Count - 1), Math.Min(_anchor.C, _paras[Math.Min(_anchor.P, _paras.Count - 1)].Text.Length));
        _focus = _anchor;
        return new JsonObject { ["replaced"] = n };
    }

    public void Dispose() { }
}
