namespace CertFlow.Contracts.Models;

/// <summary>
/// One exam's worth of a multi-exam proposal: what the candidate currently holds, and the
/// options being offered instead. The current booking is shown alongside the options because
/// a candidate moving three exams at once needs to tell the blocks apart at a glance.
/// </summary>
public record BulkExamProposal(
    Guid AppointmentId,
    string ExamCode,
    string ExamName,
    DateTimeOffset CurrentStartUtc,
    string? CurrentIanaTimeZone,
    string CurrentTestCenterCity,
    IReadOnlyList<SlotProposal> Slots,

    /// <summary>True when the options fall outside the window the candidate asked for, so the
    /// email says so rather than claiming they match.</summary>
    bool IsFallback);
