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
}
