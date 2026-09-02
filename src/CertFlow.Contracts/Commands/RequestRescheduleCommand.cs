namespace CertFlow.Contracts.Commands;

public record RequestRescheduleCommand(
    Guid AppointmentId,
    string CandidateEntraUserId,
    string? Reason,
    string Channel,               // "Email" | "Portal"
    string? PreferredCity,
    string? PreferredDayOfWeek,   // "Saturday", "Weekday", etc.
    string? PreferredTimeOfDay);  // "Morning", "Afternoon", "Evening"
