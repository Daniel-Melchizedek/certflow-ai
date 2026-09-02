namespace CertFlow.Contracts.Dtos;

public record RescheduleRequestDto(
    Guid Id,
    Guid AppointmentId,
    string Status,
    string Channel,
    string? Reason,
    string? AgentReasoning,
    List<SlotDto> ProposedSlots,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);
