namespace CertFlow.Domain.Entities;

public class AuditEvent
{
    public Guid Id { get; set; }
    public string CorrelationId { get; set; } = default!;
    public string EventType { get; set; } = default!;
    public string ActorType { get; set; } = default!;   // "Candidate" | "Agent" | "System" | "OpsStaff"
    public string? CandidateEntraUserId { get; set; }
    public string? Payload { get; set; }                // JSON
    public DateTimeOffset Timestamp { get; set; }
}
