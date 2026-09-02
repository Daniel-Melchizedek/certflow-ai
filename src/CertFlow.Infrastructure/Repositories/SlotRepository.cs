using CertFlow.Application.Interfaces;
using CertFlow.Domain.Entities;
using CertFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CertFlow.Infrastructure.Repositories;

public class SlotRepository(CertFlowDbContext db) : ISlotRepository
{
    public Task<IReadOnlyList<AppointmentSlot>> SearchAvailableAsync(
        string city, DateOnly from, DateOnly to,
        string? preferredDayOfWeek, string? preferredTimeOfDay, CancellationToken ct) =>
        db.AppointmentSlots
            .Include(s => s.TestCenter)
            .Where(s => s.IsAvailable
                     && s.TestCenter.City == city
                     && DateOnly.FromDateTime(s.StartUtc.UtcDateTime) >= from
                     && DateOnly.FromDateTime(s.StartUtc.UtcDateTime) <= to)
            .ToListAsync(ct)
            .ContinueWith(t => (IReadOnlyList<AppointmentSlot>)t.Result, ct);

    public Task<AppointmentSlot?> GetByIdAsync(Guid slotId, CancellationToken ct) =>
        db.AppointmentSlots.Include(s => s.TestCenter)
            .FirstOrDefaultAsync(s => s.Id == slotId, ct);

    public async Task<bool> TryReserveAsync(Guid slotId, CancellationToken ct)
    {
        try
        {
            var rows = await db.AppointmentSlots
                .Where(s => s.Id == slotId && s.IsAvailable)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsAvailable, false), ct);
            return rows > 0;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    public Task ReleaseAsync(Guid slotId, CancellationToken ct) =>
        db.AppointmentSlots.Where(s => s.Id == slotId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsAvailable, true), ct);
}
