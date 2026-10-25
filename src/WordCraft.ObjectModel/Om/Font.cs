using System.Globalization;
using System.Text.Json.Nodes;
using WordCraft.ObjectModel.Model;

namespace WordCraft.ObjectModel;

/// <summary>
/// Word's Font over a range or the selection. Setters map to WordCraft's <c>format.set</c> CharProps;
/// getters read the explicit run formatting and return wdUndefined when it is mixed.
/// </summary>
public sealed class Font
{
    readonly Document _doc;
    readonly Func<(int Start, int End)> _span;
    readonly bool _selection;

    internal Font(Document doc, Func<(int, int)> span, bool selection)
    {
        _doc = doc;
        _span = span;
        _selection = selection;
    }

    public object Bold { get => GetBool("bold"); set => SetBool("bold", value); }
    public object Italic { get => GetBool("italic"); set => SetBool("italic", value); }
    public object StrikeThrough { get => GetBool("strike"); set => SetBool("strike", value); }
    public object DoubleStrikeThrough { get => GetBool("doubleStrike"); set => SetBool("doubleStrike", value); }
    public object AllCaps { get => GetBool("caps"); set => SetBool("caps", value); }
    public object SmallCaps { get => GetBool("smallCaps"); set => SetBool("smallCaps", value); }
    public object Hidden { get => GetBool("hidden"); set => SetBool("hidden", value); }

    public object Superscript
    {
        get => Get("vertAlign", v => Vba.FromBool(v?.GetValue<string>() == "superscript"));
        set => Set("vertAlign", Vba.ToBool(value) ? "superscript" : "baseline");
    }

    public object Subscript
    {
        get => Get("vertAlign", v => Vba.FromBool(v?.GetValue<string>() == "subscript"));
        set => Set("vertAlign", Vba.ToBool(value) ? "subscript" : "baseline");
    }

    /// <summary>Points. Reads wdUndefined where the size comes only from a style (not resolved yet).</summary>
    public object Size
    {
        get => Get("size", v => v is null ? WdConstants.wdUndefined : v.GetValue<double>());
        set => Set("size", Convert.ToDouble(value, CultureInfo.InvariantCulture));
    }

    public object Name
    {
        get => Get("font", v => v?.GetValue<string>() ?? "");
        set => Set("font", Convert.ToString(value, CultureInfo.InvariantCulture));
    }

    /// <summary>wdUnderline values: 0 none, 1 single, 2 words, 3 double, 4 dotted, 6 thick, 7 dash, 9 dot-dash, 10 dot-dot-dash, 11 wavy.</summary>
    public object Underline
    {
        get => Get("underline", v => UnderlineCodes.FirstOrDefault(kv => kv.Value == (v?.GetValue<string>() ?? "none")).Key);
        set
        {
            int code = value is bool b ? (b ? 1 : 0) : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (code == -1) code = 1; // True
            Set("underline", UnderlineCodes.TryGetValue(code, out var name) ? name : throw new NotSupportedException($"Underline {code} isn't mapped yet."));
        }
    }

    /// <summary>A wdColor / RGB() value: red + green*256 + blue*65536; wdColorAutomatic is -16777216.</summary>
    public object Color
    {
        get => Get("color", ColorFromJson);
        set => Set("color", ColorToJson(Convert.ToInt64(value, CultureInfo.InvariantCulture)));
    }

    public object ColorIndex
    {
        get => throw new NotSupportedException("Font.ColorIndex isn't supported yet; use Font.Color.");
        set => throw new NotSupportedException("Font.ColorIndex isn't supported yet; use Font.Color.");
    }

    public void Reset() => Apply(new JsonObject
    {
        ["bold"] = false, ["italic"] = false, ["underline"] = "none", ["strike"] = false,
        ["vertAlign"] = "baseline", ["caps"] = false, ["smallCaps"] = false, ["color"] = "Auto",
    });

    static readonly Dictionary<int, string> UnderlineCodes = new()
    {
        [0] = "none", [1] = "single", [2] = "words", [3] = "double", [4] = "dotted", [6] = "thick",
        [7] = "dash", [9] = "dotDash", [10] = "dotDotDash", [11] = "wave", [43] = "doubleWave",
    };

    static object ColorFromJson(JsonNode? v)
    {
        if (v is null || v is JsonValue jv && jv.TryGetValue<string>(out var s) && s.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return WdConstants.wdColorAutomatic;
        var rgb = v is JsonObject o ? o["Rgb"] : v;
        if (rgb is JsonArray a && a.Count == 3)
            return a[0]!.GetValue<int>() + a[1]!.GetValue<int>() * 256 + a[2]!.GetValue<int>() * 65536;
        if (rgb is JsonValue hex && hex.TryGetValue<string>(out var h) && int.TryParse(h.TrimStart('#'), NumberStyles.HexNumber, null, out var n))
            return ((n >> 16) & 0xFF) + ((n >> 8) & 0xFF) * 256 + (n & 0xFF) * 65536;
        return WdConstants.wdUndefined;
    }

    static JsonNode ColorToJson(long c)
    {
        if (c == WdConstants.wdColorAutomatic) return "Auto";
        int r = (int)(c & 0xFF), g = (int)((c >> 8) & 0xFF), b = (int)((c >> 16) & 0xFF);
        return $"{r:X2}{g:X2}{b:X2}";
    }

    object GetBool(string key) => Get(key, v => Vba.FromBool(v?.GetValue<bool>() == true));

    void SetBool(string key, object value)
    {
        bool on = Vba.IsToggle(value) ? !Vba.ToBool(GetBool(key)) : Vba.ToBool(value);
        Set(key, on);
    }

    void Set(string key, JsonNode? value) => Apply(new JsonObject { [key] = value });

    void Apply(JsonObject props)
    {
        if (_selection) _doc.Run("format.set", new JsonObject { ["props"] = props }, Effect.Props);
        else
        {
            var (s, e) = _span();
            _doc.ApplyOver(s, e, "format.set", new JsonObject { ["props"] = props });
        }
    }

    /// <summary>The value of one CharProps field across the span, or wdUndefined when it differs.</summary>
    object Get(string key, Func<JsonNode?, object> convert)
    {
        var map = _doc.FreshMap;
        var (s, e) = _selection ? (map.SelStart, map.SelEnd) : _span();
        if (e <= s) e = Math.Min(map.Length, s + 1); // an insertion point reports the character after it
        string? seen = null;
        bool first = true;
        foreach (var p in map.ParasIn(s, e))
        {
            int from = Math.Max(0, s - p.Start), to = Math.Min(p.Length, e - p.Start);
            if (to <= from) continue;
            foreach (var value in ValuesIn(p, from, to, key))
            {
                var j = value?.ToJsonString();
                if (first) { seen = j; first = false; }
                else if (j != seen) return WdConstants.wdUndefined;
            }
        }
        return convert(seen is null ? null : JsonNode.Parse(seen));
    }

    static IEnumerable<JsonNode?> ValuesIn(ParaEntry p, int from, int to, string key)
    {
        int cursor = from;
        foreach (var r in p.Runs.OrderBy(r => r.Start))
        {
            if (r.End <= from || r.Start >= to) continue;
            if (r.Start > cursor) yield return null; // an unformatted gap
            yield return r.Props[key];
            cursor = Math.Max(cursor, r.End);
        }
        if (cursor < to) yield return null;
    }
}
