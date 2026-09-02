namespace CertFlow.Contracts.Models;

public record IntentResult(
    string EntraUserId,
    string DisplayName,
    Guid AppointmentId,
    string ExamCode,
    string? PreferredCity,
    string? PreferredDayRange,
    string? PreferredTimeOfDay,
    string? Reason,
    bool IsAmbiguous,
    string? ClarificationNeeded);
