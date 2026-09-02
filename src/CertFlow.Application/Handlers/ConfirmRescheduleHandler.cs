using CertFlow.Application.Interfaces;
using CertFlow.Contracts.Commands;
using CertFlow.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CertFlow.Application.Handlers;

public class ConfirmRescheduleHandler(
    IRescheduleRequestRepository requestRepo,
    ISlotRepository slotRepo,
    IAuditRepository audit,
    IEmailSender emailSender,
    ILogger<ConfirmRescheduleHandler> logger)
{
    public async Task HandleAsync(ConfirmRescheduleCommand command, CancellationToken ct = default)
    {
        var request = await requestRepo.GetByIdAsync(command.RescheduleRequestId, ct)
            ?? throw new InvalidOperationException($"Reschedule request {command.RescheduleRequestId} not found.");

        if (request.Status is not RescheduleRequestStatus.ProposalSent and not RescheduleRequestStatus.Pending)
            throw new InvalidOperationException($"Request is in state {request.Status} — cannot confirm.");

        var slot = await slotRepo.GetByIdAsync(command.SelectedSlotId, ct)
            ?? throw new InvalidOperationException($"Slot {command.SelectedSlotId} not found.");

        var reserved = await slotRepo.TryReserveAsync(slot.Id, ct);
        if (!reserved)
            throw new InvalidOperationException("Slot is no longer available. Please request a new proposal.");

        request.ConfirmedSlotId = slot.Id;
        request.Status = RescheduleRequestStatus.Committed;
        request.UpdatedAt = DateTimeOffset.UtcNow;

        await requestRepo.SaveChangesAsync(ct);

        await audit.LogAsync(new AuditEvent
        {
            Id = Guid.NewGuid(),
            CorrelationId = request.CorrelationToken,
            EventType = "RescheduleCommitted",
            ActorType = command.ConfirmedBy == "Email" ? "Candidate" : "Candidate",
            CandidateEntraUserId = request.CandidateEntraUserId,
            Timestamp = DateTimeOffset.UtcNow
        }, ct);

        logger.LogInformation("Reschedule {RequestId} committed to slot {SlotId}", request.Id, slot.Id);
    }
}
