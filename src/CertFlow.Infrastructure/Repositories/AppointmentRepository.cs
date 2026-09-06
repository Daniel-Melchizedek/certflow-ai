using CertFlow.Application.Interfaces;
using CertFlow.Domain.Entities;
using CertFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CertFlow.Infrastructure.Repositories;

public class AppointmentRepository(CertFlowDbContext db) : IAppointmentRepository
{
    public Task<Appointment?> GetByIdAsync(Guid appointmentId, CancellationToken ct) =>
        db.Appointments
            .Include(a => a.Slot).ThenInclude(s => s.TestCenter)
            .Include(a => a.Voucher).ThenInclude(v => v.ExamProgram).ThenInclude(p => p.Policy)
            .Include(a => a.Candidate)
            .FirstOrDefaultAsync(a => a.Id == appointmentId, ct);

    public Task<IReadOnlyList<Appointment>> GetUpcomingByCandidateAsync(string entraUserId, CancellationToken ct) =>
        db.Appointments
            .Include(a => a.Slot).ThenInclude(s => s.TestCenter)
            .Include(a => a.Voucher).ThenInclude(v => v.ExamProgram)
            .Where(a => a.CandidateEntraUserId == entraUserId
                     && a.Status == AppointmentStatus.Scheduled
                     && a.Slot.StartUtc > DateTimeOffset.UtcNow)
            .OrderBy(a => a.Slot.StartUtc)
            .ToListAsync(ct)
            .ContinueWith(t => (IReadOnlyList<Appointment>)t.Result, ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
