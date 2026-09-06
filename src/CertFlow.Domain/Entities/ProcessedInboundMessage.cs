namespace CertFlow.Domain.Entities;

/// <summary>
/// Claim check for an inbound email. Service Bus delivers at least once, and the old code
/// answered before writing anything, so a redelivery sent the candidate a second identical
/// reply. The consumer now inserts this row before doing any work: the primary key collides
/// on a redelivery and the duplicate is dropped without running an agent or sending mail.
/// The row is removed again if processing throws, so a genuine failure can still be retried.
/// </summary>
public class ProcessedInboundMessage
{
    /// <summary>SHA-256 of (senderEmail + graphMessageId).</summary>
    public string IdempotencyKey { get; set; } = default!;

    public string SenderEmail { get; set; } = default!;
    public string SourceMessageId { get; set; } = default!;
    public DateTimeOffset ProcessedAt { get; set; }
}
