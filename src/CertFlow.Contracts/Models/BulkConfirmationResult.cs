namespace CertFlow.Contracts.Models;

/// <summary>
/// What the Confirmation Agent made of a candidate's reply to a multi-exam proposal. Each
/// outcome mirrors one <c>confirm_reschedule_slot</c> tool call, so a partial success — two
/// exams moved, one slot taken in the meantime — is representable rather than collapsing to
/// a single pass/fail.
/// </summary>
public record BulkConfirmationResult(
    IReadOnlyList<ExamOutcome>? Outcomes,

    /// <summary>Set when the reply could not be matched to options at all; the candidate is
    /// asked to restate their choices and nothing is committed.</summary>
    string? ClarificationNeeded);

public record ExamOutcome(
    string ExamCode,
    bool Committed,
    string? NewStartLocal,
    string? TimeZone,
    string? TestCenter,
    string? City,
    string? OrderNumber,
    string? FailReason);
