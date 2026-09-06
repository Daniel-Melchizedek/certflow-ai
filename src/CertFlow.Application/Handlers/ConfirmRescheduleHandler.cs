using CertFlow.Application.Interfaces;
using CertFlow.Contracts.Commands;
using CertFlow.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CertFlow.Application.Handlers;

public class ConfirmRescheduleHandler(
    IRescheduleRequestRepository requestRepo,
    IAppointmentRepository appointmentRepo,
    ISlotRepository slotRepo,
    IAuditRepository audit,
    IEmailSender emailSender,
    Services.PolicyEngine policyEngine,
    ILogger<ConfirmRescheduleHandler> logger)
{
    public async Task HandleAsync(ConfirmRescheduleCommand command, CancellationToken ct = default)
    {
        var request = await requestRepo.GetByIdAsync(command.RescheduleRequestId, ct)
            ?? throw new InvalidOperationException($"Reschedule request {command.RescheduleRequestId} not found.");

        if (request.Status is not RescheduleRequestStatus.ProposalSent and not RescheduleRequestStatus.Pending)
            throw new InvalidOperationException($"Request is in state {request.Status} — cannot confirm.");

        var appointment = await appointmentRepo.GetByIdAsync(request.AppointmentId, ct)
            ?? throw new InvalidOperationException($"Appointment {request.AppointmentId} not found.");

        var slot = await slotRepo.GetByIdAsync(command.SelectedSlotId, ct)
            ?? throw new InvalidOperationException($"Slot {command.SelectedSlotId} not found.");

        // The real guardrail. The agent is told the rules via the MCP policy tool, but a model
        // choosing to comply is not enforcement — and an agent proposal can sit in a mailbox
        // for hours before the candidate replies, by which point the 24h window may have
        // closed. Checked here, immediately before the write, where it cannot be bypassed.
        var (eligible, rejection) = await policyEngine.EvaluateAsync(
            appointment, appointment.Voucher.ExamProgram.Policy, ct);
        if (!eligible)
            throw new InvalidOperationException(rejection ?? "Reschedule is not permitted by policy.");

        var reserved = await slotRepo.TryReserveAsync(slot.Id, ct);
        if (!reserved)
            throw new InvalidOperationException("Slot is no longer available. Please request a new proposal.");

        var previousSlot = appointment.Slot;

        try
        {
            // Repointing the appointment IS the reschedule. Reserving the new slot without
            // this leaves the candidate booked on the old date with the new slot consumed.
            // Status stays Scheduled: the exam is still upcoming, just at a new time, and
            // GetUpcomingByCandidateAsync filters on Scheduled.
            appointment.SlotId = slot.Id;
            appointment.UpdatedAt = DateTimeOffset.UtcNow;

            // Kept as reporting only — rescheduling is unlimited, so this never gates anything.
            appointment.Voucher.RescheduleCount++;

            request.ConfirmedSlotId = slot.Id;
            request.Status = RescheduleRequestStatus.Committed;
            request.UpdatedAt = DateTimeOffset.UtcNow;

            await requestRepo.SaveChangesAsync(ct);
        }
        catch
        {
            // Don't strand the new slot as unavailable if the swap never persisted.
            await slotRepo.ReleaseAsync(slot.Id, ct);
            throw;
        }

        // Only after the swap is durable — releasing earlier could free the old slot while
        // the appointment still pointed at it.
        await slotRepo.ReleaseAsync(previousSlot.Id, ct);

        await audit.LogAsync(new AuditEvent
        {
            Id = Guid.NewGuid(),
            CorrelationId = request.CorrelationToken,
            EventType = "RescheduleCommitted",
            ActorType = "Candidate",
            CandidateEntraUserId = request.CandidateEntraUserId,
            Timestamp = DateTimeOffset.UtcNow
        }, ct);

        logger.LogInformation("Reschedule {RequestId} committed: appointment {AppointmentId} moved from slot {OldSlot} to {NewSlot}",
            request.Id, appointment.Id, previousSlot.Id, slot.Id);

        // Bulk callers send one consolidated receipt covering every exam once all commits are
        // done, so the per-exam mail is suppressed rather than arriving three times over.
        if (!command.SuppressConfirmationEmail)
            await SendConfirmationAsync(appointment, slot, request.CorrelationToken, command.NotifyEmail, ct);
    }

    /// <summary>
    /// The reschedule is already committed by this point, so a mail failure is logged rather
    /// than thrown — it must not roll the candidate back to their old slot.
    /// </summary>
    private async Task SendConfirmationAsync(
        Appointment appointment, AppointmentSlot slot, string token, string? notifyEmail, CancellationToken ct)
    {
        // Prefer the address that actually confirmed — the Candidate row is keyed on the
        // Entra user id and may not carry a usable mail address for every candidate.
        var to = !string.IsNullOrWhiteSpace(notifyEmail) ? notifyEmail : appointment.Candidate?.Email;
        if (string.IsNullOrWhiteSpace(to))
        {
            logger.LogWarning("No email for candidate {CandidateId} — skipping confirmation for appointment {AppointmentId}",
                appointment.CandidateEntraUserId, appointment.Id);
            return;
        }

        var (local, tzLabel) = ToCenterLocalTime(slot);
        var examCode = appointment.Voucher.ExamProgram.Code;
        var name = appointment.Candidate?.DisplayName ?? "there";
        var subject = $"Your {examCode} exam is rescheduled [REF:{token}]";
        // Signed as operations, not AI: this is a factual receipt for a booking already
        // committed. The portal's confirmation in AppointmentEndpoints uses the same sign-off.
        var body = Services.RescheduleEmailComposer.SignAsOperations($"""
            <p>Hi {name},</p>
            <p>Your <strong>{examCode}</strong> exam has been rescheduled — no charge applied.</p>
            <ul>
              <li><strong>New date and time:</strong> {local:dddd, dd MMM yyyy} at {local:HH:mm} ({tzLabel})</li>
              <li><strong>Location:</strong> {slot.TestCenter.Name}, {slot.TestCenter.City}</li>
              <li><strong>Order number:</strong> {appointment.OrderNumber}</li>
              <li><strong>Registration ID:</strong> {appointment.RegistrationId}</li>
            </ul>
            <p>Please arrive 30 minutes early with valid photo ID. Reference: [REF:{token}]</p>
            """);

        try
        {
            await emailSender.SendAsync(to, subject, body, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reschedule {AppointmentId} committed but confirmation email to {To} failed",
                appointment.Id, to);
        }
    }

    private static (DateTime Local, string Label) ToCenterLocalTime(AppointmentSlot slot)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(slot.TestCenter.IanaTimeZone);
            return (TimeZoneInfo.ConvertTime(slot.StartUtc, tz).DateTime, slot.TestCenter.IanaTimeZone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return (slot.StartUtc.UtcDateTime, "UTC");
        }
    }
}
