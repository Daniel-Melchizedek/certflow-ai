namespace CertFlow.Contracts.Dtos;

public record AuditEventDto(
    Guid Id,
    string CorrelationId,
    string EventType,
    string ActorType,
    string? CandidateEntraUserId,
    string? Payload,
    DateTimeOffset Timestamp);
