using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace WordCraft.ObjectModel.Transport;

/// <summary>
/// Drives a running Qanloom (or WordCraft) window over its control channel (<c>qanloom --control PORT</c>):
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

    /// <summary>
    /// Connects to a window on this machine, reading its key from WORDCRAFT_CONTROL_KEY or its key
    /// file: Nisaba's settings folder first, then upstream WordCraft's.
    /// </summary>
    public static ControlChannelEngine Connect(int port, string host = "127.0.0.1")
    {
        var key = Environment.GetEnvironmentVariable("WORDCRAFT_CONTROL_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            var name = $"control-key.local-{port}";
            var dirs = SettingsDirs();
            var file = dirs.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException(
                    $"No control key {name} in {string.Join(" or ", dirs)}. Start Qanloom with --control {port}, or set WORDCRAFT_CONTROL_KEY.");
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
        var line = _reader.ReadLine() ?? throw new EngineException(what, "the app closed the connection");
        var reply = JsonNode.Parse(line)?.AsObject() ?? throw new EngineException(what, "empty reply");
        if (reply["ok"]?.GetValue<bool>() != true)
            throw new EngineException(what, reply["error"]?.ToString() ?? "unknown error");
        return reply["result"];
    }

    static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

    /// <summary>
    /// Where the app writes its key file: WORDCRAFT_PROFILE_DIR if set, else the per-user settings
    /// folder of Nisaba and then of WordCraft (Nisaba keeps WordCraft's environment variable names).
    /// </summary>
    public static IReadOnlyList<string> SettingsDirs()
    {
        var profile = Environment.GetEnvironmentVariable("WORDCRAFT_PROFILE_DIR");
        if (!string.IsNullOrEmpty(profile)) return [profile];
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Root()
        {
            if (OperatingSystem.IsWindows()) return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (OperatingSystem.IsMacOS()) return Path.Combine(home, "Library", "Application Support");
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            return string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg;
        }
        // Linux and BSD use lower-case folder names; Windows and macOS capitalised ones.
        var lower = !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();
        return [Path.Combine(Root(), lower ? "nisaba" : "Nisaba"), Path.Combine(Root(), lower ? "wordcraft" : "WordCraft")];
    }

    public void Dispose()
    {
        _reader.Dispose();
        _writer.Dispose();
        _tcp.Dispose();
    }
}
