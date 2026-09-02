namespace CertFlow.Domain.Entities;

public class RescheduleRequest
{
    public Guid Id { get; set; }
    public Guid AppointmentId { get; set; }
    public Appointment Appointment { get; set; } = default!;
    public string CandidateEntraUserId { get; set; } = default!;
    public string? Reason { get; set; }

    public RescheduleRequestStatus Status { get; set; } = RescheduleRequestStatus.Pending;

    /// <summary>Proposed slots from Agent 2 — up to 3, ranked.</summary>
    public List<Guid> ProposedSlotIds { get; set; } = [];

    /// <summary>Slot the candidate confirmed (set after email reply or portal confirm).</summary>
    public Guid? ConfirmedSlotId { get; set; }

    /// <summary>Short token embedded in email subject as [REF:token] for reply matching.</summary>
    public string CorrelationToken { get; set; } = default!;

    /// <summary>SHA-256 of (CandidateId + AppointmentId + date) — prevents duplicate processing.</summary>
    public string IdempotencyKey { get; set; } = default!;

    public string? AgentReasoning { get; set; }
    public string Channel { get; set; } = "Email";   // "Email" | "Portal"

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public enum RescheduleRequestStatus
{
    Pending,
    ProposalSent,
    ConfirmedByUser,
    Committed,
    Rejected,
    Expired
}
