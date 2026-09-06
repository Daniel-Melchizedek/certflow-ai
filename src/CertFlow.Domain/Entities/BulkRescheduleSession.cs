namespace CertFlow.Domain.Entities;

/// <summary>
/// Groups the exams a candidate asked to move in a single email, so the pipeline answers
/// once and commits every exam from one reply. Only this token is embedded as [REF:token];
/// the child requests carry their own tokens purely to satisfy the unique index and are
/// never used for reply routing.
/// </summary>
public class BulkRescheduleSession
{
    public Guid Id { get; set; }

    /// <summary>The token embedded as [REF:token] in the bulk proposal email.</summary>
    public string CorrelationToken { get; set; } = default!;

    /// <summary>SHA-256 of (candidateId + sourceMessageId) — stops a redelivered inbound
    /// email from producing a second proposal.</summary>
    public string IdempotencyKey { get; set; } = default!;

    public string CandidateEntraUserId { get; set; } = default!;
    public string DisplayName { get; set; } = default!;

    /// <summary>Graph message id of the candidate's original email, so the proposal can be
    /// sent as a threaded reply rather than a new conversation.</summary>
    public string SourceMessageId { get; set; } = default!;

    public BulkSessionStatus Status { get; set; } = BulkSessionStatus.ProposalSent;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    public List<RescheduleRequest> ChildRequests { get; set; } = [];
}

public enum BulkSessionStatus
{
    ProposalSent,

    /// <summary>Some exams committed, others failed — usually a slot taken between proposal
    /// and reply. The candidate is told which ones still need choosing.</summary>
    PartiallyCommitted,

    Committed
}
