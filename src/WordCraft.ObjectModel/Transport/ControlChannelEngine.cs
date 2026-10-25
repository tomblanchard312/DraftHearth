using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace WordCraft.ObjectModel.Transport;

/// <summary>
/// Drives a running WordCraft window over its control channel (<c>wordcraft --control PORT</c>):
/// one JSON object per line on 127.0.0.1, authorised with the window's key.
/// See docs/control-protocol.md in the WordCraft repository.
/// </summary>
public sealed class ControlChannelEngine : IEngine
{
    readonly TcpClient _tcp;
    readonly StreamReader _reader;
    readonly StreamWriter _writer;
    int _id;

    public ControlChannelEngine(string host, int port, string key)
    {
        if (!IsLoopback(host))
            throw new ArgumentException("The control key is only sent to loopback addresses; forward the port to 127.0.0.1 instead.");
        _tcp = new TcpClient();
        _tcp.Connect(host, port);
        var stream = _tcp.GetStream();
        var utf8 = new UTF8Encoding(false);
        _reader = new StreamReader(stream, utf8);
        _writer = new StreamWriter(stream, utf8) { AutoFlush = true, NewLine = "\n" };
        Send(new JsonObject { ["method"] = "auth", ["key"] = key }, "auth");
    }

    /// <summary>Connects to a window on this machine, reading its key from WORDCRAFT_CONTROL_KEY or its key file.</summary>
    public static ControlChannelEngine Connect(int port, string host = "127.0.0.1")
    {
        var key = Environment.GetEnvironmentVariable("WORDCRAFT_CONTROL_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            var file = Path.Combine(SettingsDir(), $"control-key.local-{port}");
            if (!File.Exists(file))
                throw new FileNotFoundException($"No control key at {file}. Start WordCraft with --control {port}, or set WORDCRAFT_CONTROL_KEY.");
            key = File.ReadAllText(file).Trim();
        }
        return new ControlChannelEngine(host, port, key);
    }

    public JsonNode? Execute(string command, JsonObject? parameters = null) =>
        Send(new JsonObject
        {
            ["method"] = "engine.execute",
            ["params"] = new JsonObject { ["command"] = command, ["params"] = parameters ?? new JsonObject() },
        }, command);

    JsonNode? Send(JsonObject request, string what)
    {
        request["id"] = ++_id;
        _writer.WriteLine(request.ToJsonString());
        var line = _reader.ReadLine() ?? throw new EngineException(what, "WordCraft closed the connection");
        var reply = JsonNode.Parse(line)?.AsObject() ?? throw new EngineException(what, "empty reply");
        if (reply["ok"]?.GetValue<bool>() != true)
            throw new EngineException(what, reply["error"]?.ToString() ?? "unknown error");
        return reply["result"];
    }

    static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

    static string SettingsDir()
    {
        var profile = Environment.GetEnvironmentVariable("WORDCRAFT_PROFILE_DIR");
        if (!string.IsNullOrEmpty(profile)) return profile;
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WordCraft");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", "WordCraft");
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg, "wordcraft");
    }

    public void Dispose()
    {
        _reader.Dispose();
        _writer.Dispose();
        _tcp.Dispose();
    }
}
