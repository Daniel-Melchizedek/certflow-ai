using CertFlow.Application.Handlers;
using CertFlow.Application.Interfaces;
using CertFlow.Application.Services;
using CertFlow.Contracts.Commands;
using CertFlow.Domain.Entities;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace CertFlow.McpServer.Tools;

[McpServerToolType]
public class AppointmentTools(
    IAppointmentRepository appointments,
    ISlotRepository slots,
    IRescheduleRequestRepository requests,
    IBulkRescheduleRepository bulkSessions,
    ConfirmRescheduleHandler confirmHandler,
    CorrelationTokenService tokenService)
{
    [McpServerTool, Description("Get all upcoming appointments for an Entra user.")]
    public async Task<string> GetUpcomingAppointments(
        [Description("Entra user ID or email")] string entraUserId,
        CancellationToken ct)
    {
        var appts = await appointments.GetUpcomingByCandidateAsync(entraUserId, ct);
        return JsonSerializer.Serialize(appts.Select(a => new
        {
            appointmentId = a.Id,
            examCode = a.Voucher.ExamProgram.Code,
            examName = a.Voucher.ExamProgram.Name,
            startUtc = a.Slot.StartUtc,
            durationMinutes = a.Slot.DurationMinutes,
            testCenter = a.Slot.TestCenter.Name,
            city = a.Slot.TestCenter.City,
            orderNumber = a.OrderNumber,
            registrationId = a.RegistrationId
        }));
    }

    [McpServerTool, Description("Search available slots by city and date range.")]
    public async Task<string> SearchAvailableSlots(
        [Description("Test center city")] string city,
        [Description("Start date (yyyy-MM-dd)")] string fromDate,
        [Description("End date (yyyy-MM-dd)")] string toDate,
        [Description("Preferred day: Monday/Tuesday/.../Saturday/Sunday/Weekday/Weekend")] string? preferredDay,
        [Description("Preferred time: Morning/Afternoon/Evening")] string? preferredTime,
        CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Clamp `from` to today so the LLM never gets past slots back.
        var from = DateOnly.Parse(fromDate) > today ? DateOnly.Parse(fromDate) : today;
        var to = DateOnly.Parse(toDate);

        // A model that resolved a bare "September 10" to the wrong year leaves `to` behind
        // `from` once `from` is clamped forward. That window can never match, so rather than
        // returning a bare [] that reads as "no availability", slide the window to keep the
        // caller's requested span and tell it what happened.
        if (to < from)
        {
            var requestedSpan = to.DayNumber - DateOnly.Parse(fromDate).DayNumber;
            var corrected = from.AddDays(Math.Max(requestedSpan, 14));
            return JsonSerializer.Serialize(new
            {
                error = $"Date window ended ({toDate}) before it began ({fromDate} clamped to today {today:yyyy-MM-dd}) "
                      + $"— the year is probably wrong. Today is {today:yyyy-MM-dd}. "
                      + $"Retry with fromDate={from:yyyy-MM-dd} and toDate={corrected:yyyy-MM-dd}.",
                today = today.ToString("yyyy-MM-dd"),
                suggestedFromDate = from.ToString("yyyy-MM-dd"),
                suggestedToDate = corrected.ToString("yyyy-MM-dd")
            });
        }

        var available = await slots.SearchAvailableAsync(city, from, to, preferredDay, preferredTime, ct);
        return JsonSerializer.Serialize(available.Take(10).Select(s => new
        {
            slotId = s.Id,
            startUtc = s.StartUtc,
            durationMinutes = s.DurationMinutes,
            testCenter = s.TestCenter.Name,
            city = s.TestCenter.City
        }));
    }

    [McpServerTool, Description("Preview the impact of rescheduling an appointment to a given slot.")]
    public async Task<string> PreviewReschedule(
        [Description("Appointment ID")] string appointmentId,
        [Description("Proposed slot ID")] string slotId,
        CancellationToken ct)
    {
        var appt = await appointments.GetByIdAsync(Guid.Parse(appointmentId), ct);
        var slot = await slots.GetByIdAsync(Guid.Parse(slotId), ct);
        if (appt is null || slot is null)
            return JsonSerializer.Serialize(new { error = "Appointment or slot not found" });

        // Mirrors the commit-time guardrail so the agent can avoid proposing something that
        // would be refused. Advisory only — the authoritative check runs again at the write.
        var minHours = appt.Voucher.ExamProgram.Policy.MinHoursBeforeExam;
        var hoursUntilExam = (appt.Slot.StartUtc - DateTimeOffset.UtcNow).TotalHours;
        var withinNotice = hoursUntilExam >= minHours;

        return JsonSerializer.Serialize(new
        {
            currentStart = appt.Slot.StartUtc,
            currentCenter = appt.Slot.TestCenter.Name,
            proposedStart = slot.StartUtc,
            proposedCenter = slot.TestCenter.Name,
            rescheduleFee = 0,          // always free
            voucherExpiry = appt.Voucher.ExpiryDate,
            reschedulesUsed = appt.Voucher.RescheduleCount,
            reschedulesAllowed = "unlimited",
            minHoursBeforeExam = minHours,
            hoursUntilCurrentExam = Math.Round(hoursUntilExam, 1),
            eligible = withinNotice,
            blockedReason = withinNotice
                ? null
                : $"The current exam starts in {hoursUntilExam:F0} hours, inside the "
                  + $"{minHours}-hour minimum notice window, so it can no longer be rescheduled."
        });
    }

    /// <summary>
    /// Write tool. Records one proposal covering several exams so the candidate can answer all
    /// of them in a single reply. The returned token is the only one that routes: it goes in
    /// the email as [REF:token], and the per-exam child tokens exist purely to satisfy the
    /// unique index.
    /// </summary>
    [McpServerTool, Description(
        "Create a bulk reschedule session covering several exams for one candidate. "
        + "Returns a sessionToken to embed in the proposal email as [REF:token]. "
        + "Idempotent: calling again with the same sourceMessageId returns the existing session.")]
    public async Task<string> CreateBulkRescheduleSession(
        [Description("Candidate Entra user ID")] string entraUserId,
        [Description("Candidate display name")] string displayName,
        [Description("Graph message ID of the candidate's original email")] string sourceMessageId,
        [Description("JSON array of exams: [{\"appointmentId\":\"guid\",\"proposedSlotIds\":[\"guid\",...],\"agentReasoning\":\"...\"}]")]
        string examsJson,
        CancellationToken ct)
    {
        List<BulkExamInput>? exams;
        try
        {
            exams = JsonSerializer.Deserialize<List<BulkExamInput>>(examsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            return Error($"examsJson was not valid JSON: {ex.Message}");
        }

        if (exams is null || exams.Count == 0)
            return Error("examsJson must contain at least one exam.");

        var idempotencyKey = tokenService.BuildIdempotencyKey(
            entraUserId, Guid.Empty, sourceMessageId);

        // A retried tool call must not create a second session and a second [REF:] token —
        // the candidate would then hold two proposals for the same exams and only one of
        // them would resolve.
        var existing = await bulkSessions.GetByIdempotencyKeyAsync(idempotencyKey, ct);
        if (existing is not null)
            return JsonSerializer.Serialize(new
            {
                sessionToken = existing.CorrelationToken,
                alreadyExisted = true,
                exams = existing.ChildRequests.Select(r => new
                {
                    rescheduleRequestId = r.Id,
                    appointmentId = r.AppointmentId,
                    examCode = r.Appointment.Voucher.ExamProgram.Code
                })
            });

        var sessionToken = tokenService.Generate();
        var now = DateTimeOffset.UtcNow;

        var session = new BulkRescheduleSession
        {
            Id = Guid.NewGuid(),
            CorrelationToken = sessionToken,
            IdempotencyKey = idempotencyKey,
            CandidateEntraUserId = entraUserId,
            DisplayName = displayName,
            SourceMessageId = sourceMessageId,
            Status = BulkSessionStatus.ProposalSent,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now.AddHours(48)
        };

        var summaries = new List<object>();
        foreach (var exam in exams)
        {
            if (!Guid.TryParse(exam.AppointmentId, out var appointmentId))
                return Error($"appointmentId '{exam.AppointmentId}' is not a valid GUID.");

            var appointment = await appointments.GetByIdAsync(appointmentId, ct);
            if (appointment is null)
                return Error($"Appointment {appointmentId} not found.");

            var slotIds = new List<Guid>();
            foreach (var raw in exam.ProposedSlotIds ?? [])
            {
                if (!Guid.TryParse(raw, out var slotId))
                    return Error($"proposedSlotId '{raw}' is not a valid GUID.");
                slotIds.Add(slotId);
            }

            var child = new RescheduleRequest
            {
                Id = Guid.NewGuid(),
                AppointmentId = appointmentId,
                CandidateEntraUserId = entraUserId,
                Status = slotIds.Count > 0
                    ? RescheduleRequestStatus.ProposalSent
                    : RescheduleRequestStatus.Rejected,
                ProposedSlotIds = slotIds,
                CorrelationToken = tokenService.Generate(),
                IdempotencyKey = tokenService.BuildIdempotencyKey(entraUserId, appointmentId, sourceMessageId),
                AgentReasoning = exam.AgentReasoning,
                Channel = "Email",
                CreatedAt = now,
                UpdatedAt = now,
                ExpiresAt = now.AddHours(48)
            };

            session.ChildRequests.Add(child);
            summaries.Add(new
            {
                rescheduleRequestId = child.Id,
                appointmentId,
                examCode = appointment.Voucher.ExamProgram.Code,
                proposedSlotCount = slotIds.Count
            });
        }

        await bulkSessions.AddAsync(session, ct);

        return JsonSerializer.Serialize(new
        {
            sessionToken,
            alreadyExisted = false,
            exams = summaries
        });
    }

    /// <summary>
    /// Write tool. The single authoritative commit path for the email channel — it re-checks
    /// policy immediately before the write, because an agent choosing compliant arguments is
    /// not enforcement and a proposal can sit in a mailbox until the 24-hour window closes.
    /// Never sends mail: the caller sends one consolidated confirmation once every exam in
    /// the reply has been attempted.
    /// </summary>
    [McpServerTool, Description(
        "Commit one exam reschedule to a slot the candidate chose. Enforces the minimum-notice "
        + "policy and only accepts a slot that was actually proposed. Returns committed=false "
        + "with a reason when the change is not allowed; does not send email.")]
    public async Task<string> ConfirmRescheduleSlot(
        [Description("The rescheduleRequestId returned by create_bulk_reschedule_session")] string rescheduleRequestId,
        [Description("The slot ID the candidate chose, which must be one of that request's proposed slots")] string slotId,
        [Description("Email address that confirmed, used for the audit trail")] string? notifyEmail,
        CancellationToken ct)
    {
        if (!Guid.TryParse(rescheduleRequestId, out var requestId))
            return NotCommitted(null, $"rescheduleRequestId '{rescheduleRequestId}' is not a valid GUID.");
        if (!Guid.TryParse(slotId, out var chosenSlotId))
            return NotCommitted(null, $"slotId '{slotId}' is not a valid GUID.");

        var request = await requests.GetByIdAsync(requestId, ct);
        if (request is null)
            return NotCommitted(null, $"Reschedule request {requestId} not found.");

        var appointment = await appointments.GetByIdAsync(request.AppointmentId, ct);
        var examCode = appointment?.Voucher.ExamProgram.Code;

        // The proposal list is the authorisation boundary. Without this an agent that
        // misreads the reply — or one steered by text in the candidate's own email — could
        // book a slot that was never offered.
        if (!request.ProposedSlotIds.Contains(chosenSlotId))
            return NotCommitted(examCode,
                "That slot was not one of the options offered for this exam, so it cannot be booked.");

        if (request.Status is not RescheduleRequestStatus.ProposalSent
                          and not RescheduleRequestStatus.Pending)
            return NotCommitted(examCode,
                $"This exam is already in state {request.Status} and cannot be confirmed again.");

        try
        {
            await confirmHandler.HandleAsync(
                new ConfirmRescheduleCommand(requestId, chosenSlotId, "Email", notifyEmail,
                    SuppressConfirmationEmail: true),
                ct);
        }
        catch (InvalidOperationException ex)
        {
            // Policy refusal or a slot taken since the proposal — both are outcomes the
            // candidate needs told about, not faults, so they come back as data.
            return NotCommitted(examCode, ex.Message);
        }

        var slot = await slots.GetByIdAsync(chosenSlotId, ct);
        var zone = slot?.TestCenter.IanaTimeZone;
        var local = slot is null
            ? (DateTime?)null
            : RescheduleEmailComposer.ToCentreLocal(slot.StartUtc, zone);

        return JsonSerializer.Serialize(new
        {
            committed = true,
            examCode,
            orderNumber = appointment?.OrderNumber,
            newStartUtc = slot?.StartUtc,
            newStartLocal = local?.ToString("yyyy-MM-ddTHH:mm:ss"),
            timeZone = zone,
            testCenter = slot?.TestCenter.Name,
            city = slot?.TestCenter.City
        });
    }

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { error = message });

    private static string NotCommitted(string? examCode, string reason) =>
        JsonSerializer.Serialize(new { committed = false, examCode, reason });

    private record BulkExamInput(string AppointmentId, List<string>? ProposedSlotIds, string? AgentReasoning);
}
