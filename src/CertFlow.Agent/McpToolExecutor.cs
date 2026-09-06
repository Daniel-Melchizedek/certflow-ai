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

        var result = toolName switch
        {
            "get_candidate_context" => await CallAsync("/mcp/candidate/context",
                new { email = args["email"].GetString() }, ct),
            "get_user_profile" => await CallAsync("/mcp/candidate/profile",
                new { email = args["email"].GetString() }, ct),
            "get_upcoming_appointments" => await CallAsync("/mcp/appointments/upcoming",
                new { entraUserId = args["entraUserId"].GetString() }, ct),
            "get_exam_policy" => await CallAsync("/mcp/policy",
                new { examCode = args["examCode"].GetString() }, ct),
            "search_available_slots" => await CallAsync("/mcp/slots/search", new
            {
                city = args.GetValueOrDefault("city").GetString(),
                fromDate = args.GetValueOrDefault("fromDate").GetString(),
                toDate = args.GetValueOrDefault("toDate").GetString(),
                preferredDay = args.TryGetValue("preferredDay", out var d) ? d.GetString() : null,
                preferredTime = args.TryGetValue("preferredTime", out var t) ? t.GetString() : null
            }, ct),
            "preview_reschedule" => await CallAsync("/mcp/appointments/preview-reschedule", new
            {
                appointmentId = args["appointmentId"].GetString(),
                slotId = args["slotId"].GetString()
            }, ct),
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
            "confirm_reschedule_slot" => await CallAsync("/mcp/appointments/confirm-reschedule", new
            {
                rescheduleRequestId = args["rescheduleRequestId"].GetString(),
                slotId = args["slotId"].GetString(),
                notifyEmail = args.TryGetValue("notifyEmail", out var e) ? e.GetString() : null
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
