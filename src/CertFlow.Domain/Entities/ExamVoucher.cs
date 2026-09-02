namespace CertFlow.Domain.Entities;

public class ExamVoucher
{
    public Guid Id { get; set; }
    public string CandidateEntraUserId { get; set; } = default!;
    public Candidate Candidate { get; set; } = default!;
    public Guid ExamProgramId { get; set; }
    public ExamProgram ExamProgram { get; set; } = default!;
    public DateOnly ExpiryDate { get; set; }
    public bool IsUsed { get; set; }
    public int RescheduleCount { get; set; }

    public bool IsValid(DateOnly targetDate) =>
        !IsUsed && targetDate <= ExpiryDate;
}
