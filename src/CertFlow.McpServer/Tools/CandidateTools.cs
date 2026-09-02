using Microsoft.Graph;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace CertFlow.McpServer.Tools;

[McpServerToolType]
public class CandidateTools(GraphServiceClient graphClient)
{
    [McpServerTool, Description("Resolve a candidate by email and return their Entra profile.")]
    public async Task<string> GetUserProfile(
        [Description("Candidate email address")] string email,
        CancellationToken ct)
    {
        try
        {
            var user = await graphClient.Users[email]
                .GetAsync(req =>
                {
                    req.QueryParameters.Select = ["id", "displayName", "mail", "department", "accountEnabled"];
                }, ct);

            if (user is null) return JsonSerializer.Serialize(new { error = "User not found" });

            return JsonSerializer.Serialize(new
            {
                entraUserId = user.Id,
                displayName = user.DisplayName,
                email = user.Mail,
                department = user.Department,
                accountEnabled = user.AccountEnabled
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [McpServerTool, Description("Get the exam policy for a given exam code.")]
    public Task<string> GetExamPolicy(
        [Description("Exam code, e.g. CF-204")] string examCode)
    {
        // Simplified policy lookup — production would query AI Search
        var policies = new Dictionary<string, object>
        {
            ["CF-204"] = new { minHoursBeforeExam = 24, maxReschedules = 3, fee = 0 },
            ["CF-305"] = new { minHoursBeforeExam = 48, maxReschedules = 2, fee = 0 },
            ["CF-101"] = new { minHoursBeforeExam = 24, maxReschedules = 3, fee = 0 }
        };

        return Task.FromResult(policies.TryGetValue(examCode, out var policy)
            ? JsonSerializer.Serialize(policy)
            : JsonSerializer.Serialize(new { error = $"Policy not found for exam {examCode}" }));
    }
}
