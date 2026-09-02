using CertFlow.Domain.Entities;

namespace CertFlow.Application.Interfaces;

public interface IRescheduleRequestRepository
{
    Task<RescheduleRequest?> GetByCorrelationTokenAsync(string token, CancellationToken ct = default);
    Task<RescheduleRequest?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<RescheduleRequest>> GetByAppointmentAsync(Guid appointmentId, CancellationToken ct = default);
    Task AddAsync(RescheduleRequest request, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
