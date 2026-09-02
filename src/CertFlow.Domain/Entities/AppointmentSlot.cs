namespace CertFlow.Domain.Entities;

public class AppointmentSlot
{
    public Guid Id { get; set; }
    public Guid TestCenterId { get; set; }
    public TestCenter TestCenter { get; set; } = default!;
    public DateTimeOffset StartUtc { get; set; }
    public int DurationMinutes { get; set; } = 120;
    public bool IsAvailable { get; set; } = true;

    // Optimistic concurrency token — prevents double-booking
    public byte[] RowVersion { get; set; } = default!;

    public DateTimeOffset EndUtc => StartUtc.AddMinutes(DurationMinutes);
}
