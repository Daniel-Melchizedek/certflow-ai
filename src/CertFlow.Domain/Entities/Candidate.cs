namespace CertFlow.Domain.Entities;

public class Candidate
{
    public string EntraUserId { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public string Email { get; set; } = default!;
    public string? Department { get; set; }
    public ICollection<ExamVoucher> Vouchers { get; set; } = [];
    public ICollection<Appointment> Appointments { get; set; } = [];
}
