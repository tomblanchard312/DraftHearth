using System.Text;
using System.Text.Json.Nodes;
using WordCraft.ObjectModel.Model;
using WordCraft.ObjectModel.Transport;

namespace WordCraft.ObjectModel;

/// <summary>What a command does to the cached story map.</summary>
internal enum Effect { None, Props, Text }

/// <summary>Word's Document object over one WordCraft session.</summary>
public sealed class Document
{
    internal readonly IEngine Engine;
    readonly Application _app;
    readonly List<WeakReference<Range>> _live = [];
    StoryMap? _map;
    bool _propsStale;
    string? _path;

    internal Document(Application app, IEngine engine, string? path)
    {
        _app = app;
        Engine = engine;
        _path = path;
    }

    // ---- Word API -------------------------------------------------------------------------

    public Application Application => _app;
    public string FullName => _path ?? Name;
    public string Name => _path is null ? "Document1" : System.IO.Path.GetFileName(_path);
    public string Path => _path is null ? "" : System.IO.Path.GetDirectoryName(_path) ?? "";
    public bool Saved { get; internal set; } = true;

    public Range Content => new(this, 0, Map.Length);

    /// <summary>Document.Range(Start, End); both default to the whole main story.</summary>
    public Range Range(object? Start = null, object? End = null)
    {
        int s = Start is null ? 0 : Convert.ToInt32(Start);
        int e = End is null ? Map.Length : Convert.ToInt32(End);
        return new Range(this, s, e);
    }

    public Paragraphs Paragraphs => Content.Paragraphs;
    public ContentControls ContentControls => new(this);
    public ContentControls SelectContentControlsByTag(string Tag) => new(this, tag: Tag);
    public ContentControls SelectContentControlsByTitle(string Title) => new(this, title: Title);

    public object TrackRevisions
    {
        get => Engine.Execute("document.inspect", new JsonObject { ["text"] = false })?["trackChanges"]?.GetValue<bool>() == true ? -1 : 0;
        set => Run("review.trackChanges", new JsonObject { ["value"] = Vba.ToBool(value) }, Effect.None);
    }

    public void Save()
    {
        Run("file.save", _path is null ? null : new JsonObject { ["path"] = _path }, Effect.None);
        Saved = true;
    }

    public void SaveAs2(object FileName, object? FileFormat = null, object? LockComments = null, object? Password = null,
        object? AddToRecentFiles = null, object? WritePassword = null, object? ReadOnlyRecommended = null,
        object? EmbedTrueTypeFonts = null, object? SaveNativePictureFormat = null, object? SaveFormsData = null,
        object? SaveAsAOCELetter = null, object? Encoding = null, object? InsertLineBreaks = null,
        object? AllowSubstitutions = null, object? LineEnding = null, object? AddBiDiMarks = null, object? CompatibilityMode = null)
    {
        var path = System.IO.Path.GetFullPath(Convert.ToString(FileName) ?? throw new ArgumentException("FileName is required"));
        var p = new JsonObject { ["path"] = path };
        if (Password is not null) p["password"] = Convert.ToString(Password);
        Run("file.saveAs", p, Effect.None);
        _path = path;
        Saved = true;
    }

    public void SaveAs(object FileName, object? FileFormat = null) => SaveAs2(FileName, FileFormat);

    public void Close(object? SaveChanges = null)
    {
        if (SaveChanges is not null && Convert.ToInt32(SaveChanges) == WdConstants.wdSaveChanges) Save();
        Run("file.close", new JsonObject { ["force"] = true }, Effect.Text);
        _app.Documents.Remove(this);
    }

    public void Undo(object? Times = null)
    {
        int n = Times is null ? 1 : Convert.ToInt32(Times);
        for (int i = 0; i < n; i++) Run("edit.undo", null, Effect.Text);
    }

    public void Redo(object? Times = null)
    {
        int n = Times is null ? 1 : Convert.ToInt32(Times);
        for (int i = 0; i < n; i++) Run("edit.redo", null, Effect.Text);
    }

    public void Activate() { }

    // ---- engine plumbing ---------------------------------------------------------------------

    internal JsonNode? Run(string command, JsonObject? parameters, Effect effect)
    {
        try
        {
            return Engine.Execute(command, parameters);
        }
        finally
        {
            if (effect == Effect.Text) _map = null;
            if (effect != Effect.None) { _propsStale = true; Saved = false; }
        }
    }

    internal void Invalidate() => _map = null;

    /// <summary>The story map, refreshed when the text changed.</summary>
    internal StoryMap Map => _map ??= Refresh();

    /// <summary>The story map with current formatting (for property getters).</summary>
    internal StoryMap FreshMap => _propsStale ? (_map = Refresh()) : Map;

    StoryMap Refresh()
    {
        _propsStale = false;
        var inspect = Engine.Execute("document.inspect", new JsonObject()) ?? throw new EngineException("document.inspect", "no result");
        return StoryMap.Build(inspect, path =>
        {
            try
            {
                return Engine.Execute("document.paragraph", new JsonObject { ["path"] = new JsonArray(path.Select(x => (JsonNode)x).ToArray()) })
                    ?["paragraph"]?["text"]?.GetValue<string>();
            }
            catch (EngineException) { return null; }
        });
    }

    internal void Select(int start, int end)
    {
        var map = Map;
        start = map.Clamp(start);
        end = map.Clamp(end);
        Run("select.range", new JsonObject { ["anchor"] = map.ToPos(start), ["focus"] = map.ToPos(end) }, Effect.None);
        map.SelAnchor = start;
        map.SelFocus = end;
    }

    internal void Register(Range r)
    {
        if (_live.Count > 64) _live.RemoveAll(w => !w.TryGetTarget(out _));
        _live.Add(new WeakReference<Range>(r));
    }

    /// <summary>
    /// Replaces [start, end) with text, the way Range.Text = … does. Returns the inserted span.
    /// Other live ranges and (unless typing at the selection) the user's selection are adjusted.
    /// </summary>
    internal (int Start, int End) Replace(int start, int end, string text, bool atSelection, bool raw = true)
    {
        var before = Map;
        start = before.Clamp(start);
        end = Math.Max(start, before.Clamp(end));
        int oldLength = before.Length;
        var (selA, selF) = (before.SelAnchor, before.SelFocus);

        Select(start, end);
        if (end > start) Run("text.delete", null, Effect.Text);
        InsertAtCaret(text, raw);

        var after = Map;
        int caret = after.SelFocus;
        int inserted = WordLength(text);
        var span = (Math.Max(0, caret - inserted), caret);
        int delta = after.Length - oldLength;

        foreach (var w in _live)
            if (w.TryGetTarget(out var r)) r.Shift(start, end, delta);

        if (!atSelection)
            Select(Shift(selA, start, end, delta), Shift(selF, start, end, delta));
        return span;
    }

    /// <summary>Runs a selection-based formatting command over [start, end) and puts the selection back.</summary>
    internal void ApplyOver(int start, int end, string command, JsonObject parameters)
    {
        var map = Map;
        var (selA, selF) = (map.SelAnchor, map.SelFocus);
        Select(start, end);
        Run(command, parameters, Effect.Props);
        Select(selA, selF);
    }

    /// <summary>Types text at the caret: Chr(13)/vbLf start paragraphs, Chr(11) is a line break, Chr(12) a page break.</summary>
    internal void InsertAtCaret(string text, bool raw)
    {
        var buf = new StringBuilder();
        void Flush()
        {
            if (buf.Length == 0) return;
            Run("text.insert", new JsonObject { ["text"] = buf.ToString(), ["raw"] = raw }, Effect.Text);
            buf.Clear();
        }
        var s = text.Replace("\r\n", "\r");
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\r' or '\n': Flush(); Run("text.newParagraph", null, Effect.Text); break;
                case '\v': Flush(); Run("text.lineBreak", null, Effect.Text); break;
                case '\f': Flush(); Run("text.pageBreak", null, Effect.Text); break;
                default: buf.Append(ch); break;
            }
        }
        Flush();
    }

    /// <summary>How many Word positions a string occupies once inserted.</summary>
    internal static int WordLength(string text) => text.Replace("\r\n", "\r").Length;

    /// <summary>Where position p ends up after [start, end) changed length by delta.</summary>
    internal static int Shift(int p, int start, int end, int delta)
    {
        if (p < start) return p;
        if (p >= end) return Math.Max(start, p + delta); // after the edit (an insertion point moves past new text)
        return start;                                     // inside replaced text: collapse to its start
    }
}
