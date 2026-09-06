using CertFlow.Domain.Entities;

namespace CertFlow.Application.Interfaces;

public interface IBulkRescheduleRepository
{
    /// <summary>Loads a session with its child requests and each child's appointment, exam
    /// programme and current slot — everything the confirmation agent and the confirmation
    /// email need in one round trip.</summary>
    Task<BulkRescheduleSession?> GetByTokenAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// Same graph, but untracked and forced to hit the database. The commits happen inside the
    /// MCP server — a different process with its own context — so anything already tracked here
    /// still shows the pre-commit state and would report every exam as unchanged.
    /// </summary>
    Task<BulkRescheduleSession?> GetByTokenFreshAsync(string token, CancellationToken ct = default);

    Task<BulkRescheduleSession?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default);

    Task AddAsync(BulkRescheduleSession session, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
