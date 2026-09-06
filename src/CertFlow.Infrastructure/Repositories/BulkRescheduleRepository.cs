using CertFlow.Application.Interfaces;
using CertFlow.Domain.Entities;
using CertFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CertFlow.Infrastructure.Repositories;

public class BulkRescheduleRepository(CertFlowDbContext db) : IBulkRescheduleRepository
{
    public Task<BulkRescheduleSession?> GetByTokenAsync(string token, CancellationToken ct = default) =>
        WithGraph().FirstOrDefaultAsync(b => b.CorrelationToken == token, ct);

    public Task<BulkRescheduleSession?> GetByTokenFreshAsync(string token, CancellationToken ct = default) =>
        WithGraph().AsNoTracking().FirstOrDefaultAsync(b => b.CorrelationToken == token, ct);

    public Task<BulkRescheduleSession?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default) =>
        WithGraph().FirstOrDefaultAsync(b => b.IdempotencyKey == idempotencyKey, ct);

    public async Task AddAsync(BulkRescheduleSession session, CancellationToken ct = default)
    {
        db.BulkRescheduleSessions.Add(session);
        await db.SaveChangesAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);

    // The confirmation agent needs an exam code per child to match the candidate's wording,
    // and the confirmation email needs the centre's zone to print a local time. Loading them
    // here keeps both callers off lazy loading, which is not enabled on this context.
    private IQueryable<BulkRescheduleSession> WithGraph() =>
        db.BulkRescheduleSessions
            .Include(b => b.ChildRequests)
                .ThenInclude(r => r.Appointment)
                    .ThenInclude(a => a.Voucher)
                        .ThenInclude(v => v.ExamProgram)
            .Include(b => b.ChildRequests)
                .ThenInclude(r => r.Appointment)
                    .ThenInclude(a => a.Slot)
                        .ThenInclude(s => s.TestCenter);
}
