namespace CertFlow.Contracts.Models;

public record EmailMessage(
    string MessageId,
    string SenderEmail,
    string Subject,
    string Body,
    DateTimeOffset ReceivedAt,
    string? InReplyToMessageId);
