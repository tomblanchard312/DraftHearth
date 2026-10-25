using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WordCraft.ObjectModel;

/// <summary>
/// Word's Range: a span [Start, End) of the main story in Word character positions.
/// Ranges stay live: edits made through any range shift the others the way Word does.
/// </summary>
public sealed class Range : IFindTarget
{
    readonly Document _doc;
    int _start, _end;
    Find? _find;

    internal Range(Document doc, int start, int end)
    {
        _doc = doc;
        var map = doc.Map;
        _start = map.Clamp(Math.Min(start, end));
        _end = map.Clamp(Math.Max(start, end));
        doc.Register(this);
    }

    public Document Document => _doc;
    public Application Application => _doc.Application;

    public int Start
    {
        get => _start;
        set { _start = _doc.Map.Clamp(value); if (_end < _start) _end = _start; }
    }

    public int End
    {
        get => _end;
        set { _end = _doc.Map.Clamp(value); if (_start > _end) _start = _end; }
    }

    public string Text
    {
        get => _doc.Map.Slice(_start, _end);
        set => (_start, _end) = _doc.Replace(_start, _end, value ?? "", atSelection: false);
    }

    public Font Font => new(_doc, () => (_start, _end), selection: false);
    public ParagraphFormat ParagraphFormat => new(_doc, () => (_start, _end), selection: false);
    public Find Find => _find ??= new Find(_doc, this);
    public Paragraphs Paragraphs => new(_doc, _start, _end);
    public Range Duplicate => new(_doc, _start, _end);

    public object Bold { get => Font.Bold; set => Font.Bold = value; }
    public object Italic { get => Font.Italic; set => Font.Italic = value; }

    /// <summary>Paragraph style of the first paragraph; setting applies a style to every paragraph in the range.</summary>
    public object Style
    {
        get => Styles.DisplayName(_doc.FreshMap.ParaAt(_start).StyleId);
        set => _doc.ApplyOver(_start, _end, "para.style", new JsonObject { ["style"] = Styles.Resolve(value) });
    }

    public void InsertAfter(string Text)
    {
        int s = _start;
        var (_, e) = _doc.Replace(_end, _end, Text, atSelection: false);
        (_start, _end) = (s, e);
    }

    public void InsertBefore(string Text)
    {
        int len = Document.WordLength(Text);
        int oldEnd = _end;
        var (s, _) = _doc.Replace(_start, _start, Text, atSelection: false);
        (_start, _end) = (s, oldEnd + len);
    }

    public void InsertParagraphAfter() => InsertAfter("\r");
    public void InsertParagraphBefore() => InsertBefore("\r");
    public void InsertParagraph() => Text = "\r";

    public object Delete(object? Unit = null, object? Count = null)
    {
        if (_end == _start)
        {
            int n = Count is null ? 1 : Convert.ToInt32(Count);
            _end = _doc.Map.Clamp(_start + Math.Max(1, n));
        }
        int removed = _end - _start;
        (_start, _end) = _doc.Replace(_start, _end, "", atSelection: false);
        return removed;
    }

    public void Collapse(object? Direction = null)
    {
        int d = Direction is null ? WdConstants.wdCollapseStart : Convert.ToInt32(Direction);
        if (d == WdConstants.wdCollapseEnd) _start = _end; else _end = _start;
    }

    public void SetRange(int Start, int End)
    {
        var map = _doc.Map;
        _start = map.Clamp(Math.Min(Start, End));
        _end = map.Clamp(Math.Max(Start, End));
    }

    public void Select() => _doc.Select(_start, _end);

    public bool InRange(Range Range) => _start >= Range.Start && _end <= Range.End;
    public bool IsEqual(Range Range) => _start == Range.Start && _end == Range.End;

    public int Expand(object? Unit = null)
    {
        int unit = Unit is null ? WdConstants.wdWord : Convert.ToInt32(Unit);
        int oldLen = _end - _start;
        (_start, _end) = Units.Expand(_doc.Map, unit, _start, _end);
        return _end - _start - oldLen;
    }

    public int MoveStart(object? Unit = null, object? Count = null)
    {
        int old = _start;
        _start = Units.Move(_doc.Map, Unit, Count, _start);
        if (_start > _end) _end = _start;
        return _start - old;
    }

    public int MoveEnd(object? Unit = null, object? Count = null)
    {
        int old = _end;
        _end = Units.Move(_doc.Map, Unit, Count, _end);
        if (_end < _start) _start = _end;
        return _end - old;
    }

    public int Move(object? Unit = null, object? Count = null)
    {
        int n = Count is null ? 1 : Convert.ToInt32(Count);
        Collapse(n >= 0 ? WdConstants.wdCollapseEnd : WdConstants.wdCollapseStart);
        int old = _start;
        _start = _end = Units.Move(_doc.Map, Unit, n, _start);
        return _start - old;
    }

    public Characters Characters => new(_doc, _start, _end);
    public Words Words => new(_doc, _start, _end);

    // ---- live-range bookkeeping ----------------------------------------------------------------

    internal void Shift(int start, int end, int delta)
    {
        _start = Document.Shift(_start, start, end, delta);
        _end = Math.Max(_start, Document.Shift(_end, start, end, delta));
    }

    (int Start, int End) IFindTarget.Span => (_start, _end);
    void IFindTarget.SetSpan(int start, int end) => (_start, _end) = (start, end);
    bool IFindTarget.IsSelection => false;

    public override string ToString() => Text;
}

/// <summary>Range.Characters: one Range per character.</summary>
public sealed class Characters(Document doc, int start, int end) : System.Collections.IEnumerable
{
    public int Count => end - start;
    public Range this[int index] => index < 1 || index > Count ? throw new ArgumentOutOfRangeException(nameof(index)) : new(doc, start + index - 1, start + index);
    public Range First => this[1];
    public Range Last => this[Count];
    public System.Collections.IEnumerator GetEnumerator() { for (int i = 1; i <= Count; i++) yield return this[i]; }
}

/// <summary>Range.Words: Word counts a word with its trailing spaces, and punctuation and marks as words of their own.</summary>
public sealed partial class Words : System.Collections.IEnumerable
{
    readonly Document _doc;
    readonly List<(int S, int E)> _spans = [];

    internal Words(Document doc, int start, int end)
    {
        _doc = doc;
        var text = doc.Map.Slice(start, end);
        foreach (Match m in WordToken().Matches(text))
            _spans.Add((start + m.Index, start + m.Index + m.Length));
    }

    [GeneratedRegex(@"[\p{L}\p{N}_'’]+ *|[^\p{L}\p{N}_ ] *| +")]
    private static partial Regex WordToken();

    public int Count => _spans.Count;
    public Range this[int index] => index < 1 || index > Count ? throw new ArgumentOutOfRangeException(nameof(index)) : new(_doc, _spans[index - 1].S, _spans[index - 1].E);
    public Range First => this[1];
    public Range Last => this[Count];
    public System.Collections.IEnumerator GetEnumerator() { for (int i = 1; i <= Count; i++) yield return this[i]; }
}
