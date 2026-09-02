using CertFlow.Application.Interfaces;
using CertFlow.Domain.Entities;
using CertFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CertFlow.Infrastructure.Repositories;

public class AuditRepository(CertFlowDbContext db) : IAuditRepository
{
    public async Task LogAsync(AuditEvent auditEvent, CancellationToken ct)
    {
        db.AuditEvents.Add(auditEvent);
        await db.SaveChangesAsync(ct);
    }

    public Task<IReadOnlyList<AuditEvent>> QueryAsync(
        string? candidateEntraUserId, DateTimeOffset? from, DateTimeOffset? to, string? correlationId, CancellationToken ct)
    {
        var q = db.AuditEvents.AsQueryable();
        if (candidateEntraUserId is not null) q = q.Where(a => a.CandidateEntraUserId == candidateEntraUserId);
        if (from is not null) q = q.Where(a => a.Timestamp >= from);
        if (to is not null) q = q.Where(a => a.Timestamp <= to);
        if (correlationId is not null) q = q.Where(a => a.CorrelationId == correlationId);
        return q.OrderByDescending(a => a.Timestamp).ToListAsync(ct)
            .ContinueWith(t => (IReadOnlyList<AuditEvent>)t.Result, ct);
    }
}
