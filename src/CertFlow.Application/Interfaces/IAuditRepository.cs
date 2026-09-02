using CertFlow.Domain.Entities;

namespace CertFlow.Application.Interfaces;

public interface IAuditRepository
{
    Task LogAsync(AuditEvent auditEvent, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEvent>> QueryAsync(
        string? candidateEntraUserId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? correlationId,
        CancellationToken ct = default);
}
