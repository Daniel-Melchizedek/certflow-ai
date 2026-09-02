namespace CertFlow.Domain.Entities;

public class ExamProgram
{
    public Guid Id { get; set; }
    public string Code { get; set; } = default!;          // e.g. "CF-204"
    public string Name { get; set; } = default!;
    public string Language { get; set; } = "English";
    public int DurationMinutes { get; set; } = 120;
    public ReschedulePolicy Policy { get; set; } = default!;
    public ICollection<ExamVoucher> Vouchers { get; set; } = [];
}
