using CertFlow.Domain.Entities;

namespace CertFlow.Application.Interfaces;

public interface IAppointmentRepository
{
    Task<Appointment?> GetByIdAsync(Guid appointmentId, CancellationToken ct = default);
    Task<IReadOnlyList<Appointment>> GetUpcomingByCandidateAsync(string entraUserId, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
