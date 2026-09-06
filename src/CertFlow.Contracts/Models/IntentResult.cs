namespace CertFlow.Contracts.Models;

public record IntentResult(
    string EntraUserId,
    string DisplayName,
    Guid? AppointmentId,
    string ExamCode,
    string? PreferredCity,
    string? PreferredDayRange,
    string? PreferredTimeOfDay,
    string? Reason,
    bool IsAmbiguous,
    string? ClarificationNeeded)
{
    /// <summary>
    /// Date window resolved to explicit ISO dates by the Intent Agent, which is told the
    /// current date. Carrying them forward keeps the Policy Agent from re-deriving a year
    /// from a bare "September 10" and landing in the past.
    /// </summary>
    public string? PreferredFromDate { get; init; }

    public string? PreferredToDate { get; init; }

    /// <summary>Weekday name, "Weekday", or "Weekend" — never a time of day.</summary>
    public string? PreferredDayOfWeek { get; init; }

    /// <summary>
    /// True when the candidate asked to move several exams at once ("all my exams"). The
    /// pipeline then proposes for every id in <see cref="AppointmentIds"/> and answers with
    /// one email instead of treating the request as ambiguous.
    /// </summary>
    public bool IsBulk { get; init; }

    /// <summary>Populated only when <see cref="IsBulk"/> is true; <see cref="AppointmentId"/>
    /// is left null in that case.</summary>
    public IReadOnlyList<Guid>? AppointmentIds { get; init; }
}
