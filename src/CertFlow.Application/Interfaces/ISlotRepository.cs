using CertFlow.Domain.Entities;

namespace CertFlow.Application.Interfaces;

public interface ISlotRepository
{
    Task<IReadOnlyList<AppointmentSlot>> SearchAvailableAsync(
        string city,
        DateOnly from,
        DateOnly to,
        string? preferredDayOfWeek,
        string? preferredTimeOfDay,
        CancellationToken ct = default);

    Task<AppointmentSlot?> GetByIdAsync(Guid slotId, CancellationToken ct = default);
    Task<bool> TryReserveAsync(Guid slotId, CancellationToken ct = default);
    Task ReleaseAsync(Guid slotId, CancellationToken ct = default);
}
