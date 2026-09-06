using CertFlow.Application.Interfaces;
using CertFlow.Domain.Entities;
using CertFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CertFlow.Infrastructure.Repositories;

public class RescheduleRequestRepository(CertFlowDbContext db) : IRescheduleRequestRepository
{
    public Task<RescheduleRequest?> GetByCorrelationTokenAsync(string token, CancellationToken ct) =>
        db.RescheduleRequests.FirstOrDefaultAsync(r => r.CorrelationToken == token, ct);

    public Task<RescheduleRequest?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.RescheduleRequests.FindAsync([id], ct).AsTask();

    public Task<IReadOnlyList<RescheduleRequest>> GetByAppointmentAsync(Guid appointmentId, CancellationToken ct) =>
        db.RescheduleRequests.Where(r => r.AppointmentId == appointmentId)
            .ToListAsync(ct)
            .ContinueWith(t => (IReadOnlyList<RescheduleRequest>)t.Result, ct);

    public async Task AddAsync(RescheduleRequest request, CancellationToken ct)
    {
        db.RescheduleRequests.Add(request);
        await db.SaveChangesAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    public async Task<bool> TryClaimInboundMessageAsync(
        string idempotencyKey, string senderEmail, string sourceMessageId, CancellationToken ct = default)
    {
        db.ProcessedInboundMessages.Add(new ProcessedInboundMessage
        {
            IdempotencyKey = idempotencyKey,
            SenderEmail = senderEmail,
            SourceMessageId = sourceMessageId,
            ProcessedAt = DateTimeOffset.UtcNow
        });

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            // Primary key collision — another delivery of this same email already claimed it.
            // Detach so the failed insert does not poison the next SaveChanges on this scope.
            foreach (var entry in db.ChangeTracker.Entries<ProcessedInboundMessage>().ToList())
                entry.State = EntityState.Detached;
            return false;
        }
    }

    public async Task ReleaseInboundClaimAsync(string idempotencyKey, CancellationToken ct = default)
    {
        await db.ProcessedInboundMessages
            .Where(p => p.IdempotencyKey == idempotencyKey)
            .ExecuteDeleteAsync(ct);
    }
}
