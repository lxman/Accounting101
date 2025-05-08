using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Accounting101.Angular.Server.MCP;

/// <summary>
/// Minimal echo tool following the exact pattern from the official SDK examples
/// </summary>
[McpServerToolType]
public static class MinimalEchoTool
{
    [McpServerTool]
    [Description("Echoes the message back to the client.")]
    public static string Echo(string message) => $"hello {message}";
}
