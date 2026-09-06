using CertFlow.Application.Interfaces;
using CertFlow.Domain.Entities;

namespace CertFlow.Api.Endpoints;

public static class AppointmentEndpoints
{
    public static void MapAppointmentEndpoints(this IEndpointRouteBuilder app)
    {
        // Called by /my-exams — returns the signed-in candidate's upcoming appointments.
        // The Entra OID is passed by the portal from the MSAL token; the API does not
        // hold a token itself so it trusts the caller-supplied identifier.
        app.MapGet("/api/appointments/my", async (
            string entraUserId,
            IAppointmentRepository appointments,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(entraUserId))
                return Results.BadRequest(new { error = "entraUserId is required." });

            var appts = await appointments.GetUpcomingByCandidateAsync(entraUserId, ct);
            return Results.Ok(appts.Select(a => new
            {
                id = a.Id,
                examCode = a.Voucher.ExamProgram.Code,
                examName = a.Voucher.ExamProgram.Name,
                startUtc = a.Slot.StartUtc,
                // Resolved here for the same reason as the detail endpoint below: the container
                // runs in UTC, so a client calling ToLocalTime() on the raw instant renders UTC
                // and a 9:00 AM exam in Delhi shows as 3:30 AM.
                startLocal = ToCentreLocal(a.Slot.StartUtc, a.Slot.TestCenter.IanaTimeZone)
                    .ToString("yyyy-MM-ddTHH:mm:ss"),
                ianaTimeZone = a.Slot.TestCenter.IanaTimeZone,
                testCenterName = a.Slot.TestCenter.Name,
                testCenterCity = a.Slot.TestCenter.City,
                orderNumber = a.OrderNumber,
                status = a.Status.ToString()
            }));
        });

        // Called by the appointment detail page and by the reschedule wizard's
        // OnInitializedAsync. StartLocal is resolved server-side against the test
        // centre's own zone — a candidate cares about the clock at the venue, not
        // the one on the device they happen to be browsing from.
        app.MapGet("/api/appointments/{id:guid}", async (
            Guid id,
            IAppointmentRepository appointments,
            CancellationToken ct) =>
        {
            var a = await appointments.GetByIdAsync(id, ct);
            if (a is null) return Results.NotFound();

            var tc = a.Slot.TestCenter;
            TimeZoneInfo tz;
            try { tz = TimeZoneInfo.FindSystemTimeZoneById(tc.IanaTimeZone); }
            catch { tz = TimeZoneInfo.Utc; }
            var local = TimeZoneInfo.ConvertTime(a.Slot.StartUtc, tz);

            return Results.Ok(new
            {
                id = a.Id,
                examCode = a.Voucher.ExamProgram.Code,
                examName = a.Voucher.ExamProgram.Name,
                language = a.Voucher.ExamProgram.Language,
                durationMinutes = a.Voucher.ExamProgram.DurationMinutes,
                startUtc = a.Slot.StartUtc,
                startLocal = local.ToString("yyyy-MM-ddTHH:mm:ss"),
                ianaTimeZone = tc.IanaTimeZone,
                testCenterName = tc.Name,
                addressLine1 = tc.AddressLine1,
                addressLine2 = tc.AddressLine2,
                testCenterCity = tc.City,
                state = tc.State,
                postalCode = tc.PostalCode,
                country = tc.Country,
                orderNumber = a.OrderNumber,
                registrationId = a.RegistrationId,
                status = a.Status.ToString(),
                hasAccommodations = a.HasAccommodations
            });
        });

        // Called by step 3 of the wizard to populate the time-slot picker.
        app.MapGet("/api/slots/search", async (
            string city,
            string from,
            string to,
            ISlotRepository slots,
            CancellationToken ct) =>
        {
            if (!DateOnly.TryParse(from, out var fromDate) || !DateOnly.TryParse(to, out var toDate))
                return Results.BadRequest(new { error = "from and to must be yyyy-MM-dd dates." });

            var results = await slots.SearchAvailableAsync(city, fromDate, toDate, null, null, ct);
            return Results.Ok(results.Select(s => new
            {
                id = s.Id,
                startUtc = s.StartUtc,
                durationMinutes = s.DurationMinutes,
                testCenterName = s.TestCenter.Name,
                testCenterCity = s.TestCenter.City,
                ianaTimeZone = s.TestCenter.IanaTimeZone,
                addressLine1 = s.TestCenter.AddressLine1,
                addressLine2 = s.TestCenter.AddressLine2,
                state = s.TestCenter.State,
                postalCode = s.TestCenter.PostalCode,
                country = s.TestCenter.Country
            }));
        });

        // Called by the wizard's Confirm Reschedule button. No agent involved — portal
        // candidates reschedule directly, which is exactly why the policy check below has to
        // live here as well as in the email channel's handler.
        app.MapPost("/api/appointments/{id:guid}/reschedule", async (
            Guid id,
            PortalRescheduleRequest body,
            IAppointmentRepository appointmentRepo,
            ISlotRepository slotRepo,
            IAuditRepository audit,
            IEmailSender emailSender,
            CertFlow.Application.Services.PolicyEngine policyEngine,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("AppointmentEndpoints");
            var appointment = await appointmentRepo.GetByIdAsync(id, ct);
            if (appointment is null) return Results.NotFound();

            if (appointment.Status != AppointmentStatus.Scheduled)
                return Results.BadRequest(new { error = "Appointment is not in Scheduled status." });

            // This channel never consults the MCP server or an agent, so the policy check has
            // to happen here too — the wizard hides ineligible options, but a hidden button is
            // not a guardrail against a direct POST.
            var (eligible, rejection) = await policyEngine.EvaluateAsync(
                appointment, appointment.Voucher.ExamProgram.Policy, ct);
            if (!eligible)
                return Results.BadRequest(new { error = rejection ?? "Reschedule is not permitted by policy." });

            var newSlot = await slotRepo.GetByIdAsync(body.SelectedSlotId, ct);
            if (newSlot is null)
                return Results.BadRequest(new { error = "Slot not found." });

            var reserved = await slotRepo.TryReserveAsync(newSlot.Id, ct);
            if (!reserved)
                return Results.Conflict(new { error = "Slot is no longer available — please pick another." });

            var previousSlotId = appointment.SlotId;
            try
            {
                appointment.SlotId = newSlot.Id;
                appointment.UpdatedAt = DateTimeOffset.UtcNow;
                appointment.Voucher.RescheduleCount++;
                await appointmentRepo.SaveChangesAsync(ct);
            }
            catch
            {
                await slotRepo.ReleaseAsync(newSlot.Id, ct);
                throw;
            }

            await slotRepo.ReleaseAsync(previousSlotId, ct);

            await audit.LogAsync(new AuditEvent
            {
                Id = Guid.NewGuid(),
                CorrelationId = $"portal-{id:N}",
                EventType = "RescheduleCommitted",
                ActorType = "Portal",
                CandidateEntraUserId = appointment.CandidateEntraUserId,
                Timestamp = DateTimeOffset.UtcNow
            }, ct);

            // Best-effort confirmation email — failure must not undo the committed reschedule.
            var to = !string.IsNullOrWhiteSpace(body.NotifyEmail)
                ? body.NotifyEmail
                : appointment.Candidate?.Email;

            if (!string.IsNullOrWhiteSpace(to))
            {
                try
                {
                    var examCode = appointment.Voucher.ExamProgram.Code;
                    var name = appointment.Candidate?.DisplayName ?? "there";
                    var token = $"portal-{id:N}";

                    TimeZoneInfo tz;
                    try { tz = TimeZoneInfo.FindSystemTimeZoneById(newSlot.TestCenter.IanaTimeZone); }
                    catch { tz = TimeZoneInfo.Utc; }
                    var local = TimeZoneInfo.ConvertTime(newSlot.StartUtc, tz).DateTime;
                    var tzLabel = newSlot.TestCenter.IanaTimeZone;

                    // Same operations sign-off as the email channel's confirmation in
                    // ConfirmRescheduleHandler — a candidate should see one consistent voice
                    // regardless of which channel they rescheduled through.
                    await emailSender.SendAsync(to,
                        $"Your {examCode} exam is rescheduled [REF:{token}]",
                        CertFlow.Application.Services.RescheduleEmailComposer.SignAsOperations($"""
                        <p>Hi {name},</p>
                        <p>Your <strong>{examCode}</strong> exam has been rescheduled via the candidate portal — no charge applied.</p>
                        <ul>
                          <li><strong>New date and time:</strong> {local:dddd, dd MMM yyyy} at {local:HH:mm} ({tzLabel})</li>
                          <li><strong>Location:</strong> {newSlot.TestCenter.Name}, {newSlot.TestCenter.City}</li>
                          <li><strong>Order number:</strong> {appointment.OrderNumber}</li>
                        </ul>
                        <p>Please arrive 30 minutes early with valid photo ID.</p>
                        """),
                        ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Portal reschedule committed but confirmation email to {To} failed", to);
                }
            }

            return Results.Ok(new { orderNumber = appointment.OrderNumber });
        });
    }

    /// <summary>
    /// A candidate cares about the clock at the venue, not the one on the server or on whatever
    /// device they happen to be browsing from. Falls back to UTC rather than throwing, since a
    /// bad zone id on one centre should not take the whole list down.
    /// </summary>
    private static DateTime ToCentreLocal(DateTimeOffset utc, string? ianaTimeZone)
    {
        if (string.IsNullOrWhiteSpace(ianaTimeZone)) return utc.UtcDateTime;
        try
        {
            return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById(ianaTimeZone)).DateTime;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return utc.UtcDateTime;
        }
    }

    private record PortalRescheduleRequest(Guid SelectedSlotId, string? ConfirmedBy, string? NotifyEmail);
}
