using System.Globalization;
using System.Text.Json.Nodes;

namespace WordCraft.ObjectModel;

/// <summary>Word's Selection: WordCraft's own selection, moved and edited with its keyboard commands.</summary>
public sealed class Selection : IFindTarget
{
    readonly Application _app;
    Find? _find;
    Document? _findDoc;

    internal Selection(Application app) => _app = app;

    Document Doc => _app.ActiveDocument;

    public int Start
    {
        get => Doc.Map.SelStart;
        set => Doc.Select(value, Math.Max(value, End));
    }

    public int End
    {
        get => Doc.Map.SelEnd;
        set => Doc.Select(Math.Min(Start, value), value);
    }

    public string Text
    {
        get => Doc.Map.Slice(Start, End);
        set
        {
            var (s, e) = Doc.Replace(Start, End, value ?? "", atSelection: true);
            Doc.Select(s, e); // Word leaves the new text selected
        }
    }

    public Range Range => new(Doc, Start, End);
    public Font Font => new(Doc, () => (Start, End), selection: true);
    public ParagraphFormat ParagraphFormat => new(Doc, () => (Start, End), selection: true);
    public Paragraphs Paragraphs => new(Doc, Start, End);
    public Characters Characters => new(Doc, Start, End);
    public Words Words => new(Doc, Start, End);
    public Document Document => Doc;

    /// <summary>One Find per selection, so property settings persist across Selection.Find calls.</summary>
    public Find Find
    {
        get
        {
            if (_find is null || _findDoc != Doc) { _find = new Find(Doc, this); _findDoc = Doc; }
            return _find;
        }
    }

    public object Style
    {
        get => Range.Style;
        set => Doc.Run("para.style", new JsonObject { ["style"] = Styles.Resolve(value) }, Effect.Props);
    }

    /// <summary>Types like the keyboard, so AutoCorrect and AutoFormat-as-you-type apply, as in Word.</summary>
    public void TypeText(string Text) => Doc.InsertAtCaret(Text ?? "", raw: false);
    public void TypeParagraph() => Doc.Run("text.newParagraph", null, Effect.Text);
    public void TypeBackspace() => Doc.Run("text.backspace", null, Effect.Text);
    public void InsertParagraphAfter() => InsertAfter("\r");
    public void InsertBreak(object? Type = null) => Doc.Run("text.pageBreak", null, Effect.Text);

    public void InsertAfter(string Text)
    {
        int s = Start;
        var (_, e) = Doc.Replace(End, End, Text, atSelection: true);
        Doc.Select(s, e);
    }

    public void InsertBefore(string Text)
    {
        int e = End + Document.WordLength(Text);
        var (s, _) = Doc.Replace(Start, Start, Text, atSelection: true);
        Doc.Select(s, e);
    }

    public object Delete(object? Unit = null, object? Count = null)
    {
        int n = Count is null ? 1 : Convert.ToInt32(Count, CultureInfo.InvariantCulture);
        for (int i = 0; i < Math.Max(1, n); i++) Doc.Run("text.delete", null, Effect.Text);
        return n;
    }

    public void Collapse(object? Direction = null)
    {
        bool toEnd = Direction is not null && Convert.ToInt32(Direction, CultureInfo.InvariantCulture) == WdConstants.wdCollapseEnd;
        Doc.Run("select.collapse", new JsonObject { ["end"] = toEnd }, Effect.None);
        Doc.Map.SelAnchor = Doc.Map.SelFocus = toEnd ? End : Start;
    }

    public void WholeStory() => Doc.Select(0, Doc.Map.Length);
    public void SetRange(int Start, int End) => Doc.Select(Start, End);
    public void Select() { }

    public int HomeKey(object? Unit = null, object? Extend = null) => Key(Unit, Extend, WdConstants.wdLine, "caret.docStart", "caret.home");
    public int EndKey(object? Unit = null, object? Extend = null) => Key(Unit, Extend, WdConstants.wdLine, "caret.docEnd", "caret.end");

    public int MoveRight(object? Unit = null, object? Count = null, object? Extend = null) =>
        Step(Unit, Count, Extend, WdConstants.wdCharacter, new() { [WdConstants.wdCharacter] = "caret.right", [WdConstants.wdWord] = "caret.wordRight" });

    public int MoveLeft(object? Unit = null, object? Count = null, object? Extend = null) =>
        Step(Unit, Count, Extend, WdConstants.wdCharacter, new() { [WdConstants.wdCharacter] = "caret.left", [WdConstants.wdWord] = "caret.wordLeft" });

    public int MoveDown(object? Unit = null, object? Count = null, object? Extend = null) =>
        Step(Unit, Count, Extend, WdConstants.wdLine, new() { [WdConstants.wdLine] = "caret.down", [WdConstants.wdParagraph] = "caret.paraDown" });

    public int MoveUp(object? Unit = null, object? Count = null, object? Extend = null) =>
        Step(Unit, Count, Extend, WdConstants.wdLine, new() { [WdConstants.wdLine] = "caret.up", [WdConstants.wdParagraph] = "caret.paraUp" });

    public int Expand(object? Unit = null)
    {
        var r = Range;
        int n = r.Expand(Unit);
        Doc.Select(r.Start, r.End);
        return n;
    }

    int Key(object? unit, object? extend, int defaultUnit, string storyCmd, string lineCmd)
    {
        int u = unit is null ? defaultUnit : Convert.ToInt32(unit, CultureInfo.InvariantCulture);
        string cmd = u == WdConstants.wdStory ? storyCmd : u == WdConstants.wdLine ? lineCmd
            : throw new NotSupportedException($"HomeKey/EndKey unit {u} isn't supported yet.");
        return Move(cmd, extend);
    }

    int Step(object? unit, object? count, object? extend, int defaultUnit, Dictionary<int, string> commands)
    {
        int u = unit is null ? defaultUnit : Convert.ToInt32(unit, CultureInfo.InvariantCulture);
        if (!commands.TryGetValue(u, out var cmd)) throw new NotSupportedException($"Move unit {u} isn't supported yet.");
        int n = count is null ? 1 : Convert.ToInt32(count, CultureInfo.InvariantCulture);
        int moved = 0;
        for (int i = 0; i < n; i++) moved += Move(cmd, extend) != 0 ? 1 : 0;
        return moved;
    }

    int Move(string command, object? extend)
    {
        bool ext = extend is not null && Convert.ToInt32(extend, CultureInfo.InvariantCulture) == WdConstants.wdExtend;
        var before = Doc.Map.SelFocus;
        var result = Doc.Run(command, new JsonObject { ["extend"] = ext }, Effect.None);
        var map = Doc.Map;
        if (result?["focus"] is JsonNode f)
        {
            map.SelFocus = map.FromPos(f);
            map.SelAnchor = result["anchor"] is JsonNode a ? map.FromPos(a) : map.SelFocus;
        }
        else
        {
            Doc.Invalidate();
            map = Doc.Map;
        }
        return map.SelFocus - before;
    }

    (int Start, int End) IFindTarget.Span => (Start, End);
    void IFindTarget.SetSpan(int start, int end) { }
    bool IFindTarget.IsSelection => true;
}
