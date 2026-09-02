namespace CertFlow.Contracts.Dtos;

public record AppointmentDto(
    Guid Id,
    string ExamCode,
    string ExamName,
    DateTimeOffset StartUtc,
    int DurationMinutes,
    string TestCenterName,
    string TestCenterCity,
    string TestCenterCountry,
    string OrderNumber,
    string RegistrationId,
    string Status);
