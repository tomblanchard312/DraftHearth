using System.Globalization;
using System.Text.RegularExpressions;
using WordCraft.ObjectModel.Model;

namespace WordCraft.ObjectModel;

/// <summary>VBA value conventions: True is -1, False 0; wdToggle flips; wdUndefined means mixed.</summary>
internal static class Vba
{
    public static bool ToBool(object? v) => v switch
    {
        null => false,
        bool b => b,
        string s => bool.TryParse(s, out var b) ? b : double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) && d != 0,
        _ => Convert.ToDouble(v, CultureInfo.InvariantCulture) != 0,
    };

    public static bool IsToggle(object? v) => v is not bool && v is not null && v is not string && Convert.ToInt64(v, CultureInfo.InvariantCulture) == WdConstants.wdToggle;

    public static object FromBool(bool b) => b ? -1 : 0;
}

/// <summary>Built-in style names and ids. WordCraft ids drop the spaces ("Heading1"); Word shows "Heading 1".</summary>
internal static class Styles
{
    static readonly Dictionary<int, string> BuiltIn = new()
    {
        [WdConstants.wdStyleNormal] = "Normal",
        [WdConstants.wdStyleHeading1] = "Heading 1",
        [WdConstants.wdStyleHeading2] = "Heading 2",
        [WdConstants.wdStyleHeading3] = "Heading 3",
        [WdConstants.wdStyleHeading4] = "Heading 4",
        [WdConstants.wdStyleHeading5] = "Heading 5",
        [WdConstants.wdStyleHeading6] = "Heading 6",
        [WdConstants.wdStyleHeading7] = "Heading 7",
        [WdConstants.wdStyleHeading8] = "Heading 8",
        [WdConstants.wdStyleHeading9] = "Heading 9",
        [WdConstants.wdStyleTitle] = "Title",
        [WdConstants.wdStyleSubtitle] = "Subtitle",
        [WdConstants.wdStyleQuote] = "Quote",
        [WdConstants.wdStyleListParagraph] = "List Paragraph",
        [WdConstants.wdStyleListBullet] = "List Bullet",
        [WdConstants.wdStyleListNumber] = "List Number",
        [WdConstants.wdStyleBodyText] = "Body Text",
        [WdConstants.wdStyleCaption] = "Caption",
    };

    /// <summary>A style argument as WordCraft's para.style takes it (name or id).</summary>
    public static string Resolve(object? value) => value switch
    {
        null => "Normal",
        string s => s,
        Style st => st.NameLocal,
        _ when value is IConvertible && int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var id) =>
            BuiltIn.TryGetValue(id, out var name) ? name : throw new NotSupportedException($"Built-in style {id} isn't mapped yet."),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "Normal",
    };

    public static string DisplayName(string id)
    {
        var m = Regex.Match(id, @"^(Heading|TOC)(\d)$");
        if (m.Success) return $"{m.Groups[1].Value} {m.Groups[2].Value}";
        return id switch
        {
            "ListParagraph" => "List Paragraph",
            "ListBullet" => "List Bullet",
            "ListNumber" => "List Number",
            "BodyText" => "Body Text",
            "NoSpacing" => "No Spacing",
            "IntenseQuote" => "Intense Quote",
            _ => id,
        };
    }
}

/// <summary>Moving and expanding by Word units over the story text.</summary>
internal static partial class Units
{
    [GeneratedRegex(@"[\p{L}\p{N}_'’]+ *|[^\p{L}\p{N}_ \r] *|\r| +")]
    private static partial Regex WordToken();

    public static (int, int) Expand(StoryMap map, int unit, int start, int end)
    {
        switch (unit)
        {
            case WdConstants.wdStory:
                return (0, map.Length);
            case WdConstants.wdParagraph:
            {
                var first = map.ParaAt(start);
                var last = end > start ? map.ParaAt(end - 1) : first;
                return (first.Start, Math.Min(map.Length, last.Mark + 1));
            }
            case WdConstants.wdCharacter:
                return end > start ? (start, end) : (start, Math.Min(map.Length, start + 1));
            case WdConstants.wdWord:
            {
                int s = start, e = end;
                foreach (Match m in WordToken().Matches(map.Text))
                {
                    if (m.Index <= start && start < m.Index + m.Length) s = m.Index;
                    if (m.Index < Math.Max(end, start + 1) && Math.Max(end, start + 1) <= m.Index + m.Length) { e = m.Index + m.Length; break; }
                }
                return (s, Math.Max(s, e));
            }
            default:
                throw new NotSupportedException($"Unit {unit} isn't supported yet (characters, words, paragraphs and the story are).");
        }
    }

    public static int Move(StoryMap map, object? unitArg, object? countArg, int pos)
    {
        int unit = unitArg is null ? WdConstants.wdCharacter : Convert.ToInt32(unitArg);
        int count = countArg is null ? 1 : Convert.ToInt32(countArg);
        switch (unit)
        {
            case WdConstants.wdCharacter:
                return map.Clamp(pos + count);
            case WdConstants.wdStory:
                return count >= 0 ? map.Length : 0;
            case WdConstants.wdParagraph:
            {
                var paras = map.Paras;
                int i = paras.IndexOf(map.ParaAt(pos));
                if (count > 0) { int j = Math.Min(paras.Count - 1, i + count); return j == i ? map.Length : paras[j].Start; }
                if (count < 0) { int j = pos > paras[i].Start ? i + count + 1 : i + count; return paras[Math.Max(0, j)].Start; }
                return pos;
            }
            case WdConstants.wdWord:
            {
                var starts = WordToken().Matches(map.Text).Select(m => m.Index).Append(map.Length).ToList();
                int idx = starts.FindIndex(s => s > pos);
                if (idx < 0) idx = starts.Count - 1;
                if (count > 0) return starts[Math.Min(starts.Count - 1, idx + count - 1)];
                if (count < 0) { int cur = starts.FindLastIndex(s => s < pos); return cur < 0 ? 0 : starts[Math.Max(0, cur + count + 1)]; }
                return pos;
            }
            default:
                throw new NotSupportedException($"Unit {unit} isn't supported yet.");
        }
    }
}
