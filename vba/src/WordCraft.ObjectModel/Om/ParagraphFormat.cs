using System.Globalization;
using System.Text.Json.Nodes;

namespace WordCraft.ObjectModel;

/// <summary>Word's ParagraphFormat over a range or the selection, mapped to WordCraft's <c>para.set</c> ParaProps.</summary>
public sealed class ParagraphFormat
{
    readonly Document _doc;
    readonly Func<(int Start, int End)> _span;
    readonly bool _selection;

    internal ParagraphFormat(Document doc, Func<(int, int)> span, bool selection)
    {
        _doc = doc;
        _span = span;
        _selection = selection;
    }

    static readonly string[] Alignments = ["left", "center", "right", "justify", "distribute"];

    /// <summary>wdAlignParagraphLeft 0, Center 1, Right 2, Justify 3, Distribute 4.</summary>
    public object Alignment
    {
        get => Get("align", v => Math.Max(0, Array.IndexOf(Alignments, v?.GetValue<string>() ?? "left")));
        set
        {
            int i = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (i < 0 || i >= Alignments.Length) throw new NotSupportedException($"Alignment {i} isn't mapped yet.");
            Set("align", Alignments[i]);
        }
    }

    public object LeftIndent { get => Num("indentLeft"); set => SetNum("indentLeft", value); }
    public object RightIndent { get => Num("indentRight"); set => SetNum("indentRight", value); }
    public object FirstLineIndent { get => Num("indentFirst"); set => SetNum("indentFirst", value); }
    public object SpaceBefore { get => Num("spaceBefore"); set => SetNum("spaceBefore", value); }
    public object SpaceAfter { get => Num("spaceAfter"); set => SetNum("spaceAfter", value); }
    public object KeepWithNext { get => Flag("keepNext"); set => SetFlag("keepNext", value); }
    public object KeepTogether { get => Flag("keepLines"); set => SetFlag("keepLines", value); }
    public object PageBreakBefore { get => Flag("pageBreakBefore"); set => SetFlag("pageBreakBefore", value); }
    public object WidowControl { get => Flag("widowControl"); set => SetFlag("widowControl", value); }

    int _rule = WdConstants.wdLineSpaceSingle;

    /// <summary>wdLineSpaceSingle 0, 1pt5 1, Double 2, AtLeast 3, Exactly 4, Multiple 5.</summary>
    public object LineSpacingRule
    {
        get => _rule;
        set
        {
            _rule = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            switch (_rule)
            {
                case WdConstants.wdLineSpaceSingle: SetSpacing("multiple", 1); break;
                case WdConstants.wdLineSpace1pt5: SetSpacing("multiple", 1.5); break;
                case WdConstants.wdLineSpaceDouble: SetSpacing("multiple", 2); break;
            }
        }
    }

    /// <summary>Points; for wdLineSpaceMultiple, 12 points is one line.</summary>
    public object LineSpacing
    {
        get => Get("lineSpacing", v => v?["rule"]?.GetValue<string>() == "multiple" ? v["value"]!.GetValue<double>() * 12 : v?["value"]?.GetValue<double>() ?? 12.0);
        set
        {
            double pt = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            switch (_rule)
            {
                case WdConstants.wdLineSpaceAtLeast: SetSpacing("atLeast", pt); break;
                case WdConstants.wdLineSpaceExactly: SetSpacing("exactly", pt); break;
                default: SetSpacing("multiple", pt / 12); break;
            }
        }
    }

    void SetSpacing(string rule, double value) => Set("lineSpacing", new JsonObject { ["rule"] = rule, ["value"] = value });

    object Num(string key) => Get(key, v => v?.GetValue<double>() ?? 0.0);
    void SetNum(string key, object value) => Set(key, Convert.ToDouble(value, CultureInfo.InvariantCulture));
    object Flag(string key) => Get(key, v => Vba.FromBool(v?.GetValue<bool>() == true));
    void SetFlag(string key, object value) => Set(key, Vba.ToBool(value));

    void Set(string key, JsonNode? value)
    {
        var p = new JsonObject { ["props"] = new JsonObject { [key] = value } };
        if (_selection) _doc.Run("para.set", p, Effect.Props);
        else
        {
            var (s, e) = _span();
            _doc.ApplyOver(s, e, "para.set", p);
        }
    }

    object Get(string key, Func<JsonNode?, object> convert)
    {
        var map = _doc.FreshMap;
        var (s, e) = _selection ? (map.SelStart, map.SelEnd) : _span();
        string? seen = null;
        bool first = true;
        foreach (var p in map.ParasIn(s, e))
        {
            var j = p.Props?[key]?.ToJsonString();
            if (first) { seen = j; first = false; }
            else if (j != seen) return WdConstants.wdUndefined;
        }
        return convert(seen is null ? null : JsonNode.Parse(seen));
    }
}
