using System.Collections;
using System.Text.Json.Nodes;

namespace WordCraft.ObjectModel;

/// <summary>The paragraphs of a range, numbered from 1 as in Word.</summary>
public sealed class Paragraphs : IEnumerable
{
    readonly Document _doc;
    readonly List<(int Start, int End)> _spans;

    internal Paragraphs(Document doc, int start, int end)
    {
        _doc = doc;
        var map = doc.Map;
        _spans = map.ParasIn(start, end).Select(p => (p.Start, Math.Min(map.Length, p.Mark + 1))).ToList();
    }

    public int Count => _spans.Count;

    public Paragraph this[int index] =>
        index < 1 || index > _spans.Count
            ? throw new ArgumentOutOfRangeException(nameof(index), $"The collection has {_spans.Count} paragraphs.")
            : new Paragraph(new Range(_doc, _spans[index - 1].Start, _spans[index - 1].End));

    public Paragraph First => this[1];
    public Paragraph Last => this[Count];

    public Paragraph Add(object? Range = null)
    {
        var at = Range as Range ?? new Range(_doc, _spans[^1].End, _spans[^1].End);
        at.InsertParagraphAfter();
        return new Paragraph(new Range(_doc, at.End, at.End));
    }

    public IEnumerator GetEnumerator()
    {
        for (int i = 1; i <= Count; i++) yield return this[i];
    }
}

/// <summary>One paragraph. Its Range includes the paragraph mark.</summary>
public sealed class Paragraph
{
    internal Paragraph(Range range) => Range = range;

    public Range Range { get; }
    public ParagraphFormat Format => Range.ParagraphFormat;
    public object Style { get => Range.Style; set => Range.Style = value; }
    public object Alignment { get => Format.Alignment; set => Format.Alignment = value; }
    public object SpaceBefore { get => Format.SpaceBefore; set => Format.SpaceBefore = value; }
    public object SpaceAfter { get => Format.SpaceAfter; set => Format.SpaceAfter = value; }
    public object LeftIndent { get => Format.LeftIndent; set => Format.LeftIndent = value; }
    public object FirstLineIndent { get => Format.FirstLineIndent; set => Format.FirstLineIndent = value; }

    public Paragraph? Next(object? Count = null)
    {
        int n = Count is null ? 1 : Convert.ToInt32(Count);
        var all = Range.Document.Paragraphs;
        int i = IndexIn(all);
        return i + n >= 1 && i + n <= all.Count ? all[i + n] : null;
    }

    public Paragraph? Previous(object? Count = null) => Next(-(Count is null ? 1 : Convert.ToInt32(Count)));

    int IndexIn(Paragraphs all)
    {
        for (int i = 1; i <= all.Count; i++)
            if (all[i].Range.Start == Range.Start) return i;
        return 0;
    }

    public void OutlinePromote() => Range.Document.ApplyOver(Range.Start, Range.End, "outline.promote", new JsonObject());
    public void OutlineDemote() => Range.Document.ApplyOver(Range.Start, Range.End, "outline.demote", new JsonObject());
}

/// <summary>A style as returned by Document.Styles(name); Word's default property is NameLocal.</summary>
public sealed class Style(string name)
{
    public string NameLocal { get; } = name;
    public override string ToString() => NameLocal;
}
