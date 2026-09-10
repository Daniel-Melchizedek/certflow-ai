using System.Diagnostics;

namespace CertFlow.McpServer;

/// <summary>
/// Tool-level spans for the MCP tools.
///
/// Over the MCP protocol every tool call arrives as <c>POST /</c> on a single endpoint, so the
/// ASP.NET Core request span cannot say which tool ran — the whole surface collapses into one
/// indistinguishable operation name. The per-path REST shim used to reveal the tool for free.
///
/// The source is owned here rather than subscribing to the MCP SDK's own ActivitySource: the SDK
/// is a preview package, and a source name that turns out to be wrong (or that emits nothing
/// server-side) fails silently, leaving no spans and no error to notice.
/// </summary>
internal static class McpTelemetry
{
    /// <summary>Must be passed to AddSource, otherwise these spans are dropped.</summary>
    public const string SourceName = "CertFlow.McpServer.Tools";

    private static readonly ActivitySource Source = new(SourceName);

    /// <summary>
    /// Returns null when nothing is listening, which is the normal no-listener case — callers
    /// dispose it with `using` and need no null check.
    /// </summary>
    public static Activity? StartTool(string toolName) =>
        Source.StartActivity($"mcp.tool {toolName}", ActivityKind.Internal)
             ?.SetTag("mcp.tool.name", toolName);
}
