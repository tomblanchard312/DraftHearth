using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace WordCraft.ObjectModel.Transport;

/// <summary>
/// Runs a headless WordCraft session through <c>wordcraft-cli mcp</c> (MCP over stdio, newline-delimited JSON-RPC).
/// No window is needed, so this is the transport for batch runs and CI.
/// </summary>
public sealed class McpStdioEngine : IEngine
{
    readonly Process _process;
    readonly StringBuilder _stderr = new();
    int _id;

    /// <param name="cliPath">Path to wordcraft-cli.</param>
    /// <param name="extraArgs">Extra arguments after <c>mcp</c>, e.g. --automation-read-root DIR --automation-write-root DIR.</param>
    public McpStdioEngine(string cliPath, params string[] extraArgs)
    {
        var psi = new ProcessStartInfo(cliPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("mcp");
        foreach (var a in extraArgs) psi.ArgumentList.Add(a);
        _process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {cliPath}");
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_stderr) { if (_stderr.Length < 16_384) _stderr.AppendLine(e.Data); }
        };
        _process.BeginErrorReadLine();

        Rpc("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "wordcraft-vba", ["version"] = "0.1" },
        });
        Write(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
    }

    public JsonNode? Execute(string command, JsonObject? parameters = null) =>
        Tool("execute", new JsonObject { ["command"] = command, ["params"] = parameters ?? new JsonObject() }, command);

    /// <summary>Calls an MCP tool directly (e.g. new_document, open_document).</summary>
    public JsonNode? Tool(string name, JsonObject arguments, string? label = null)
    {
        var result = Rpc("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments });
        var text = result?["content"]?[0]?["text"]?.GetValue<string>() ?? "null";
        if (result?["isError"]?.GetValue<bool>() == true)
            throw new EngineException(label ?? name, text);
        try { return JsonNode.Parse(text); }
        catch (System.Text.Json.JsonException) { return JsonValue.Create(text); }
    }

    JsonNode? Rpc(string method, JsonObject parameters)
    {
        var id = ++_id;
        Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
        while (true)
        {
            var line = _process.StandardOutput.ReadLine();
            if (line is null)
            {
                string err;
                lock (_stderr) err = _stderr.ToString();
                throw new EngineException(method, $"wordcraft-cli exited. {err}".Trim());
            }
            if (string.IsNullOrWhiteSpace(line)) continue;
            var msg = JsonNode.Parse(line)?.AsObject();
            if (msg?["id"]?.GetValue<int>() != id) continue; // notifications and unrelated replies
            if (msg["error"] is JsonNode error)
                throw new EngineException(method, error["message"]?.ToString() ?? error.ToJsonString());
            return msg["result"];
        }
    }

    void Write(JsonObject message)
    {
        _process.StandardInput.WriteLine(message.ToJsonString());
        _process.StandardInput.Flush();
    }

    public void Dispose()
    {
        try
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        _process.Dispose();
    }
}
