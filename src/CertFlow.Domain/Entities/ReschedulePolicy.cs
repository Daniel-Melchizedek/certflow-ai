namespace CertFlow.Domain.Entities;

public class ReschedulePolicy
{
    public Guid Id { get; set; }
    public Guid ExamProgramId { get; set; }
    public ExamProgram ExamProgram { get; set; } = default!;

    /// <summary>Minimum hours before exam start that rescheduling is allowed.</summary>
    public int MinHoursBeforeExam { get; set; } = 24;

    public int MaxReschedulesPerVoucher { get; set; } = 3;
    public bool AllowDeliveryModeChange { get; set; } = false;
    public bool RequiresAccommodationReview { get; set; } = false;

    // Rescheduling is always free — no fee field.
}
