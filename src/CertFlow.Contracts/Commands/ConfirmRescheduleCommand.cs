namespace CertFlow.Contracts.Commands;

public record ConfirmRescheduleCommand(
    Guid RescheduleRequestId,
    Guid SelectedSlotId,
    string ConfirmedBy,          // "Email" | "Portal"
    string? NotifyEmail = null,  // address that confirmed; falls back to the candidate record
    // Set when this commit is one exam of a multi-exam request. The caller sends a single
    // consolidated confirmation covering every exam, so the per-exam receipt would arrive as
    // two or three near-identical mails for one reply.
    bool SuppressConfirmationEmail = false);
