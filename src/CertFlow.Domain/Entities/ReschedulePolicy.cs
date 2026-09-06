namespace CertFlow.Domain.Entities;

public class ReschedulePolicy
{
    public Guid Id { get; set; }
    public Guid ExamProgramId { get; set; }
    public ExamProgram ExamProgram { get; set; } = default!;

    /// <summary>
    /// Minimum hours before exam start that rescheduling is allowed. This is the only hard
    /// limit on rescheduling — there is deliberately no cap on how many times a candidate may
    /// move a booking, so a per-voucher counter is not part of the policy.
    /// </summary>
    public int MinHoursBeforeExam { get; set; } = 24;

    public bool AllowDeliveryModeChange { get; set; } = false;
    public bool RequiresAccommodationReview { get; set; } = false;

    // Rescheduling is always free — no fee field.
}
