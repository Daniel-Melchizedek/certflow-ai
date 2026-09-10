using CertFlow.Application.Interfaces;
using CertFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace CertFlow.McpServer.Tools;

[McpServerToolType]
public class CandidateTools(
    GraphServiceClient graphClient,
    IAppointmentRepository appointments,
    CertFlowDbContext db)
{
    /// <summary>
    /// Single call that resolves the sender's Entra profile AND their upcoming appointments
    /// so the Intent Agent never needs to make a dependent second tool call.
    /// </summary>
    // Names are pinned explicitly: the SDK would otherwise derive them from the C# method name, and
    // the agent system prompts refer to these tools by their snake_case names.
    [McpServerTool(Name = "get_candidate_context"), Description("Look up a candidate by email: returns their Entra profile plus all upcoming exam appointments.")]
    public async Task<string> GetCandidateContext(
        [Description("Candidate email address")] string email,
        CancellationToken ct)
    {
        using var span = McpTelemetry.StartTool("get_candidate_context");
        try
        {
            var user = await graphClient.Users[email]
                .GetAsync(req =>
                {
                    req.QueryParameters.Select = ["id", "displayName", "mail", "department", "accountEnabled"];
                }, ct);

            if (user is null) return JsonSerializer.Serialize(new { error = "User not found in Entra ID" });

            var appts = await appointments.GetUpcomingByCandidateAsync(user.Id!, ct);

            return JsonSerializer.Serialize(new
            {
                entraUserId = user.Id,
                displayName = user.DisplayName,
                email = user.Mail,
                accountEnabled = user.AccountEnabled,
                upcomingAppointments = appts.Select(a => new
                {
                    appointmentId = a.Id,
                    examCode = a.Voucher.ExamProgram.Code,
                    examName = a.Voucher.ExamProgram.Name,
                    startUtc = a.Slot.StartUtc,
                    testCenter = a.Slot.TestCenter.Name,
                    city = a.Slot.TestCenter.City,
                    orderNumber = a.OrderNumber
                })
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [McpServerTool(Name = "get_user_profile"), Description("Resolve a candidate by email and return their Entra profile.")]
    public async Task<string> GetUserProfile(
        [Description("Candidate email address")] string email,
        CancellationToken ct)
    {
        using var span = McpTelemetry.StartTool("get_user_profile");
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

    /// <summary>
    /// Reads the policy from the database rather than a hardcoded table. The rules are already
    /// seeded per exam program, and a second copy here would quietly start lying the moment the
    /// catalogue changed — the agent would be told "policy not found" for a perfectly valid
    /// exam and refuse an eligible reschedule.
    ///
    /// This is advisory: it tells the agent the rules so it does not propose slots that will be
    /// refused. It is not the enforcement point — the same rules are re-checked at the commit
    /// boundary, which is the only place a write actually happens.
    /// </summary>
    [McpServerTool(Name = "get_exam_policy"), Description("Get the exam policy for a given exam code.")]
    public async Task<string> GetExamPolicy(
        [Description("Exam code, e.g. AZ-900")] string examCode,
        CancellationToken ct = default)
    {
        using var span = McpTelemetry.StartTool("get_exam_policy");
        var policy = await db.ReschedulePolicies
            .Include(p => p.ExamProgram)
            .Where(p => p.ExamProgram.Code == examCode)
            .Select(p => new
            {
                examCode = p.ExamProgram.Code,
                examName = p.ExamProgram.Name,
                durationMinutes = p.ExamProgram.DurationMinutes,
                minHoursBeforeExam = p.MinHoursBeforeExam,
                maxReschedules = "unlimited",
                fee = 0,
                notes = $"Rescheduling is free and may be done any number of times, provided the "
                      + $"request is made at least {p.MinHoursBeforeExam} hours before the exam start time."
            })
            .FirstOrDefaultAsync(ct);

        return JsonSerializer.Serialize(
            policy is not null
                ? (object)policy
                : new { error = $"Policy not found for exam {examCode}" });
    }
}
