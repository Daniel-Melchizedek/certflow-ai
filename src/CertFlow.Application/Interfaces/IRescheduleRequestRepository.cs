using CertFlow.Domain.Entities;

namespace CertFlow.Application.Interfaces;

public interface IRescheduleRequestRepository
{
    Task<RescheduleRequest?> GetByCorrelationTokenAsync(string token, CancellationToken ct = default);
    Task<RescheduleRequest?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<RescheduleRequest>> GetByAppointmentAsync(Guid appointmentId, CancellationToken ct = default);
    Task AddAsync(RescheduleRequest request, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// Records that an inbound email is being handled. Returns false when the key is already
    /// present, which means a redelivery of a message that was already answered — the caller
    /// should drop it rather than reply twice.
    /// </summary>
    Task<bool> TryClaimInboundMessageAsync(
        string idempotencyKey, string senderEmail, string sourceMessageId, CancellationToken ct = default);

    /// <summary>Undoes a claim so a failed message can be retried on redelivery.</summary>
    Task ReleaseInboundClaimAsync(string idempotencyKey, CancellationToken ct = default);
}
