namespace CertFlow.Contracts.Dtos;

public record SlotDto(
    Guid Id,
    DateTimeOffset StartUtc,
    int DurationMinutes,
    string TestCenterName,
    string TestCenterCity,
    string TestCenterId);
