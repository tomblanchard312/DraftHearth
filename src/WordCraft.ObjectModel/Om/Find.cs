using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WordCraft.ObjectModel;

/// <summary>Something a Find redefines when it succeeds: a Range or the Selection.</summary>
internal interface IFindTarget
{
    (int Start, int End) Span { get; }
    void SetSpan(int start, int end);
    bool IsSelection { get; }
}

public sealed class Replacement
{
    public string Text { get; set; } = "";
    public void ClearFormatting() { }
}

/// <summary>
/// Word's Find. Matching runs here over the story text (so ranges, loops and wraps behave like
/// Word); a Replace All over the whole document goes to WordCraft's own <c>edit.replaceAll</c>,
/// which speaks Word's ^-codes and wildcards and records one Undo step.
/// </summary>
public sealed class Find
{
    readonly Document _doc;
    readonly IFindTarget _target;
    (int Start, int End)? _lastMatch;
    int _scopeEnd;

    internal Find(Document doc, IFindTarget target)
    {
        _doc = doc;
        _target = target;
    }

    public string Text { get; set; } = "";
    public Replacement Replacement { get; } = new();
    public object Forward { get; set; } = true;
    public object Wrap { get; set; } = WdConstants.wdFindStop;
    public object Format { get; set; } = false;
    public object MatchCase { get; set; } = false;
    public object MatchWholeWord { get; set; } = false;
    public object MatchWildcards { get; set; } = false;
    public object MatchSoundsLike { get; set; } = false;
    public object MatchAllWordForms { get; set; } = false;
    public bool Found { get; private set; }

    public void ClearFormatting() => Format = false;

    public bool Execute(object? FindText = null, object? MatchCase = null, object? MatchWholeWord = null,
        object? MatchWildcards = null, object? MatchSoundsLike = null, object? MatchAllWordForms = null,
        object? Forward = null, object? Wrap = null, object? Format = null, object? ReplaceWith = null,
        object? Replace = null, object? MatchKashida = null, object? MatchDiacritics = null,
        object? MatchAlefHamza = null, object? MatchControl = null)
    {
        // Arguments given to Execute stick, as in Word.
        if (FindText is not null) Text = Convert.ToString(FindText, CultureInfo.InvariantCulture) ?? "";
        if (MatchCase is not null) this.MatchCase = MatchCase;
        if (MatchWholeWord is not null) this.MatchWholeWord = MatchWholeWord;
        if (MatchWildcards is not null) this.MatchWildcards = MatchWildcards;
        if (MatchSoundsLike is not null) this.MatchSoundsLike = MatchSoundsLike;
        if (MatchAllWordForms is not null) this.MatchAllWordForms = MatchAllWordForms;
        if (Forward is not null) this.Forward = Forward;
        if (Wrap is not null) this.Wrap = Wrap;
        if (Format is not null) this.Format = Format;
        if (ReplaceWith is not null) Replacement.Text = Convert.ToString(ReplaceWith, CultureInfo.InvariantCulture) ?? "";

        if (Vba.ToBool(this.Format)) throw new NotSupportedException("Find with formatting isn't supported yet.");
        if (Vba.ToBool(this.MatchSoundsLike) || Vba.ToBool(this.MatchAllWordForms))
            throw new NotSupportedException("Sounds-like and all-word-forms matching aren't supported.");

        int mode = Replace is null ? WdConstants.wdReplaceNone : Convert.ToInt32(Replace, CultureInfo.InvariantCulture);
        bool wildcards = Vba.ToBool(this.MatchWildcards);
        var rx = FindPattern.Build(Text, Vba.ToBool(this.MatchCase), Vba.ToBool(this.MatchWholeWord), wildcards);
        Found = mode == WdConstants.wdReplaceAll ? ReplaceAll(rx, wildcards) : FindOne(rx, mode, wildcards);
        return Found;
    }

    bool FindOne(Regex rx, int mode, bool wildcards)
    {
        var map = _doc.Map;
        var text = map.Text;
        var (s, e) = _target.Span;
        bool forward = Vba.ToBool(Forward);
        int wrap = Convert.ToInt32(Wrap, CultureInfo.InvariantCulture);

        (int From, int To) region;
        if (_target.IsSelection)
            region = forward ? (s == e ? s : e, map.Length) : (0, s);
        else if (_lastMatch == (s, e))
            region = forward ? (e, _scopeEnd) : (0, s); // continuing a Do While .Execute loop
        else
        {
            region = s == e ? (forward ? (s, map.Length) : (0, s)) : (s, e);
            _scopeEnd = region.To;
        }

        var m = Search(rx, text, region, forward);
        if (m is null && wrap == WdConstants.wdFindContinue && (_target.IsSelection || s == e))
            m = Search(rx, text, forward ? (0, region.From) : (region.To, map.Length), forward);
        if (m is null) return false;

        var (ms, me) = (m.Index, m.Index + m.Length);
        if (mode == WdConstants.wdReplaceOne)
            (ms, me) = _doc.Replace(ms, me, FindPattern.Expand(Replacement.Text, m, wildcards), atSelection: _target.IsSelection);
        _lastMatch = (ms, me);
        if (_target.IsSelection) _doc.Select(ms, me);
        _target.SetSpan(ms, me);
        return true;
    }

    bool ReplaceAll(Regex rx, bool wildcards)
    {
        var map = _doc.Map;
        var (s, e) = _target.Span;
        int wrap = Convert.ToInt32(Wrap, CultureInfo.InvariantCulture);
        var region = s != e ? (s, e) : wrap == WdConstants.wdFindContinue || !_target.IsSelection && s == 0 ? (0, map.Length) : (s, map.Length);

        if (region == (0, map.Length))
            return ReplaceAllInEngine(wildcards);

        // Part of the story: replace from the last match back so earlier positions stay valid.
        var matches = rx.Matches(map.Text).Where(m => m.Length > 0 && m.Index >= region.Item1 && m.Index + m.Length <= region.Item2).ToList();
        for (int i = matches.Count - 1; i >= 0; i--)
            _doc.Replace(matches[i].Index, matches[i].Index + matches[i].Length, FindPattern.Expand(Replacement.Text, matches[i], wildcards), atSelection: false);
        return matches.Count > 0;
    }

    bool ReplaceAllInEngine(bool wildcards)
    {
        var map = _doc.Map;
        var (selA, selF) = (map.SelAnchor, map.SelFocus);
        _doc.Select(0, 0); // a non-empty selection would limit WordCraft's Replace All to it
        var result = _doc.Run("edit.replaceAll", new JsonObject
        {
            ["text"] = Text,
            ["with"] = Replacement.Text,
            ["matchCase"] = Vba.ToBool(MatchCase),
            ["wholeWord"] = Vba.ToBool(MatchWholeWord),
            ["wildcards"] = wildcards,
        }, Effect.Text);
        var after = _doc.Map;
        _doc.Select(after.Clamp(selA), after.Clamp(selF));
        return (result?["replaced"]?.GetValue<int>() ?? 0) > 0;
    }

    static Match? Search(Regex rx, string text, (int From, int To) region, bool forward)
    {
        if (region.To <= region.From) return null;
        Match? found = null;
        for (var m = rx.Match(text, region.From); m.Success && m.Index + m.Length <= region.To; m = m.NextMatch())
        {
            if (m.Length == 0) continue;
            found = m;
            if (forward) break;
        }
        return found;
    }
}

/// <summary>Word's Find syntax (^-codes and wildcards) translated to .NET regular expressions.</summary>
public static class FindPattern
{
    public static Regex Build(string text, bool matchCase, bool wholeWord, bool wildcards)
    {
        var body = wildcards ? FromWildcards(text) : FromLiteral(text);
        if (wholeWord && !wildcards) body = $@"(?<![\p{{L}}\p{{N}}_]){body}(?![\p{{L}}\p{{N}}_])";
        var opts = RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        return new Regex(body, opts, TimeSpan.FromSeconds(2));
    }

    static string Caret(char c, bool inWildcards) => c switch
    {
        'p' => @"\r",
        't' => @"\t",
        'l' or 'n' => @"\v",
        'm' => @"\f",
        '^' => @"\^",
        '?' when !inWildcards => ".",
        '#' when !inWildcards => @"\d",
        '$' when !inWildcards => @"\p{L}",
        'w' when !inWildcards => @"[ \t ]+",
        's' => @" ",
        '~' => @"‑",
        '+' => @"—",
        '=' => @"–",
        _ => throw new NotSupportedException($"Find code ^{c} isn't supported yet."),
    };

    static string FromLiteral(string text)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '^' && i + 1 < text.Length) sb.Append(Caret(text[++i], false));
            else sb.Append(Regex.Escape(text[i].ToString()));
        }
        return sb.ToString();
    }

    static string FromWildcards(string text)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '\\' when i + 1 < text.Length: sb.Append(Regex.Escape(text[++i].ToString())); break;
                case '^' when i + 1 < text.Length: sb.Append(Caret(text[++i], true)); break;
                case '?': sb.Append('.'); break;
                case '*': sb.Append("(?:.|\\r)*?"); break;
                case '@': sb.Append('+'); break;
                case '<': sb.Append(@"\b(?=\w)"); break;
                case '>': sb.Append(@"\b(?<=\w)"); break;
                case '(' or ')' or '|': sb.Append(c); break;
                case '{':
                {
                    int close = text.IndexOf('}', i);
                    if (close < 0) throw new ArgumentException("Unclosed { in wildcard pattern.");
                    sb.Append('{').Append(text[(i + 1)..close].Replace(';', ',')).Append('}');
                    i = close;
                    break;
                }
                case '[':
                {
                    int close = text.IndexOf(']', i + 1);
                    if (close < 0) throw new ArgumentException("Unclosed [ in wildcard pattern.");
                    var inner = text[(i + 1)..close];
                    sb.Append('[').Append(inner.StartsWith('!') ? "^" + inner[1..] : inner).Append(']');
                    i = close;
                    break;
                }
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>The replacement text for one match: ^p ^t ^& ^^, and \1–\9 with wildcards.</summary>
    public static string Expand(string replacement, Match m, bool wildcards)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < replacement.Length; i++)
        {
            char c = replacement[i];
            if (c == '^' && i + 1 < replacement.Length)
            {
                char k = replacement[++i];
                sb.Append(k switch
                {
                    'p' => "\r", 't' => "\t", 'l' or 'n' => "\v", 'm' => "\f", '^' => "^", '&' => m.Value,
                    's' => " ", '+' => "—", '=' => "–",
                    _ => throw new NotSupportedException($"Replacement code ^{k} isn't supported yet."),
                });
            }
            else if (wildcards && c == '\\' && i + 1 < replacement.Length && char.IsDigit(replacement[i + 1]))
                sb.Append(m.Groups[replacement[++i] - '0'].Value);
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
