namespace CertFlow.Contracts.Models;

public record PolicyResult(
    bool IsEligible,
    string? RejectionReason,
    List<SlotProposal> ProposedSlots,
    string? AgentReasoning);

public record SlotProposal(
    Guid SlotId,
    DateTimeOffset StartUtc,
    int DurationMinutes,
    string TestCenterName,
    string TestCenterCity,
    int Rank);
