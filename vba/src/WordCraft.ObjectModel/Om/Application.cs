using System.Collections;
using System.Text.Json.Nodes;
using WordCraft.ObjectModel.Transport;

namespace WordCraft.ObjectModel;

/// <summary>Word's Application over one WordCraft engine (a window or a headless session).</summary>
public sealed class Application
{
    readonly IEngine _engine;

    public Application(IEngine engine, string? openPath = null)
    {
        _engine = engine;
        Documents = new Documents(this, engine);
        Selection = new Selection(this);
        Documents.Attach(openPath);
    }

    public string Name => "Qanloom";
    public string Version => "16.0"; // macros branch on this; Nisaba targets Word 16 (Microsoft 365) behaviour
    public Documents Documents { get; }
    public Selection Selection { get; }

    public Document ActiveDocument => Documents.Active ?? throw new InvalidOperationException("This command is not available because no document is open. (4248)");

    public bool ScreenUpdating { get; set; } = true;
    public object DisplayAlerts { get; set; } = WdConstants.wdAlertsAll;
    public string StatusBar { get; set; } = "";
    public string UserName
    {
        get => "WordCraft User";
        set => _engine.Execute("file.setAuthor", new JsonObject { ["name"] = value });
    }

    public void ScreenRefresh() { }
    public void Quit(object? SaveChanges = null) { }
}

public sealed class Documents : IEnumerable
{
    readonly Application _app;
    readonly IEngine _engine;
    readonly List<Document> _docs = [];

    internal Documents(Application app, IEngine engine)
    {
        _app = app;
        _engine = engine;
    }

    internal Document? Active => _docs.Count > 0 ? _docs[^1] : null;

    internal void Attach(string? path) => _docs.Add(new Document(_app, _engine, path));
    internal void Remove(Document d) => _docs.Remove(d);

    public int Count => _docs.Count;
    public Document this[object index] => index switch
    {
        string name => _docs.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || d.FullName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No open document named {name}."),
        _ => _docs[Convert.ToInt32(index) - 1],
    };

    /// <summary>One WordCraft session holds one document, so opening replaces the current one.</summary>
    public Document Open(object FileName, object? ConfirmConversions = null, object? ReadOnly = null, object? AddToRecentFiles = null,
        object? PasswordDocument = null)
    {
        var path = Path.GetFullPath(Convert.ToString(FileName) ?? throw new ArgumentException("FileName is required"));
        var p = new JsonObject { ["path"] = path };
        if (PasswordDocument is not null) p["password"] = Convert.ToString(PasswordDocument);
        _engine.Execute("file.open", p);
        _docs.Clear();
        Attach(path);
        return _docs[^1];
    }

    public Document Add(object? Template = null, object? NewTemplate = null, object? DocumentType = null, object? Visible = null)
    {
        var p = new JsonObject();
        if (Template is string t && !string.IsNullOrEmpty(t)) p["template"] = t;
        _engine.Execute("file.new", p);
        _docs.Clear();
        Attach(null);
        return _docs[^1];
    }

    public IEnumerator GetEnumerator() => _docs.ToList().GetEnumerator();
}
