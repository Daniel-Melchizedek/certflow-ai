using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;

namespace CertFlow.Agent;

/// <summary>
/// Executes agent tool calls by routing to the MCP server's HTTP endpoints.
/// The agent declares tool schemas; this executor performs the actual work.
/// </summary>
public class McpToolExecutor(HttpClient httpClient, ILogger<McpToolExecutor> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public async Task<string> ExecuteAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        logger.LogDebug("Executing MCP tool: {Tool}", toolName);

        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsJson, JsonOpts) ?? [];

        // Only the bulk session opener is routed here. Every other tool is now invoked by Foundry
        // over the MCP protocol, so mirroring them would mean maintaining a second contract for
        // callers that no longer exist. This one is not an agent tool: InboundEmailConsumer calls
        // it directly while composing a bulk proposal.
        var result = toolName switch
        {
            "create_bulk_reschedule_session" => await CallAsync("/mcp/bulk/create-session", new
            {
                entraUserId = args["entraUserId"].GetString(),
                displayName = args.TryGetValue("displayName", out var n) ? n.GetString() : "there",
                sourceMessageId = args["sourceMessageId"].GetString(),
                // The model sometimes emits this as a JSON array rather than the string the
                // schema asks for. Re-serialising a non-string element keeps both shapes working.
                examsJson = args["examsJson"].ValueKind == JsonValueKind.String
                    ? args["examsJson"].GetString()
                    : args["examsJson"].GetRawText()
            }, ct),
            _ => JsonSerializer.Serialize(new { error = $"Unknown tool: {toolName}" })
        };

        return result;
    }

    private async Task<string> CallAsync<T>(string path, T body, CancellationToken ct)
    {
        var response = await httpClient.PostAsJsonAsync(path, body, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }
}
