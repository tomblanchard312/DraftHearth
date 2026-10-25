using System.Text.Json.Nodes;

namespace WordCraft.ObjectModel.Transport;

/// <summary>
/// One WordCraft editing session. Every call runs a command from WordCraft's command
/// registry by id (the same ids the ribbon, CLI, control channel and MCP server use).
/// </summary>
public interface IEngine : IDisposable
{
    /// <summary>Runs a command and returns its JSON result. Throws <see cref="EngineException"/> on failure.</summary>
    JsonNode? Execute(string command, JsonObject? parameters = null);
}

public sealed class EngineException(string command, string message) : Exception($"WordCraft {command}: {message}")
{
    public string Command { get; } = command;
    public string EngineMessage { get; } = message;
}
