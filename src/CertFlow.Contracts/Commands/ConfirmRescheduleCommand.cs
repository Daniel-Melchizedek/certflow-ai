namespace CertFlow.Contracts.Commands;

public record ConfirmRescheduleCommand(
    Guid RescheduleRequestId,
    Guid SelectedSlotId,
    string ConfirmedBy);   // "Email" | "Portal"
