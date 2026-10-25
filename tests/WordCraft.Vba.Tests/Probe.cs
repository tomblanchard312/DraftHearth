using System.Text;
using System.Text.Json.Nodes;
using WordCraft.ObjectModel.Transport;
using WordCraft.Vba.Testing;

/// <summary>Records every command id sent to the engine.</summary>
sealed class LoggingEngine(IEngine inner) : IEngine
{
    public List<string> Log { get; } = [];

    public JsonNode? Execute(string command, JsonObject? parameters = null)
    {
        Log.Add(command);
        return inner.Execute(command, parameters);
    }

    public void Dispose() => inner.Dispose();
}

/// <summary>
/// Test access to a document through <c>document.inspect</c> only, so the same assertions run
/// against FakeEngine and against real WordCraft (set WCVBA_CLI to a wordcraft-cli binary).
/// </summary>
sealed class Probe : IDisposable
{
    static readonly List<Probe> Open = [];
    readonly LoggingEngine _engine;

    public static string? Cli => Environment.GetEnvironmentVariable("WCVBA_CLI");
    public static bool Real => !string.IsNullOrEmpty(Cli);

    public IEngine Engine => _engine;
    public List<string> Log => _engine.Log;

    Probe(IEngine engine)
    {
        _engine = new LoggingEngine(engine);
        Open.Add(this);
    }

    /// <summary>A fresh document holding these paragraphs, caret at the start.</summary>
    public static Probe Create(params string[] paragraphs)
    {
        if (!Real) return new Probe(new FakeEngine(paragraphs));
        var probe = new Probe(new McpStdioEngine(Cli!));
        var e = probe._engine;
        e.Execute("file.new", new JsonObject { ["template"] = "blank" });
        for (int i = 0; i < paragraphs.Length; i++)
        {
            if (i > 0) e.Execute("text.newParagraph");
            if (paragraphs[i].Length > 0) e.Execute("text.insert", new JsonObject { ["text"] = paragraphs[i], ["raw"] = true });
        }
        e.Execute("caret.docStart");
        probe.Log.Clear();
        return probe;
    }

    public static void DisposeAll()
    {
        foreach (var p in Open) p.Dispose();
        Open.Clear();
    }

    JsonArray Blocks => _engine.Execute("document.inspect", new JsonObject())!["blocks"]!.AsArray();
    JsonNode Block(int i) => Blocks[i]!;

    public string[] Paragraphs => Blocks.Select(b => b!["text"]?.GetValue<string>() ?? "").ToArray();
    public string StyleOf(int paragraph) => Block(paragraph)["style"]!.GetValue<string>();
    public JsonObject ParaProps(int paragraph) => Block(paragraph)["props"] as JsonObject ?? [];

    /// <summary>The explicit CharProps covering one character (UTF-16 index).</summary>
    public JsonObject CharProps(int paragraph, int ch)
    {
        var b = Block(paragraph);
        var text = b["text"]!.GetValue<string>();
        int at = Encoding.UTF8.GetByteCount(text.AsSpan(0, ch));
        foreach (var r in b["runs"]?.AsArray() ?? [])
            if (r!["start"]!.GetValue<int>() <= at && at < r["end"]!.GetValue<int>())
                return r["props"]!.AsObject();
        return [];
    }

    public void Dispose() => _engine.Dispose();
}
