namespace CertFlow.Domain.Entities;

public class Appointment
{
    public Guid Id { get; set; }
    public string CandidateEntraUserId { get; set; } = default!;
    public Candidate Candidate { get; set; } = default!;
    public Guid SlotId { get; set; }
    public AppointmentSlot Slot { get; set; } = default!;
    public Guid VoucherId { get; set; }
    public ExamVoucher Voucher { get; set; } = default!;
    public AppointmentStatus Status { get; set; } = AppointmentStatus.Scheduled;
    public bool HasAccommodations { get; set; }
    public string OrderNumber { get; set; } = default!;
    public string RegistrationId { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum AppointmentStatus
{
    Scheduled,
    Rescheduled,
    Cancelled,
    Completed
}
