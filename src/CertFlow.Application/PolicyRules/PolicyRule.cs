using CertFlow.Domain.Entities;

namespace CertFlow.Application.PolicyRules;

public abstract class PolicyRule
{
    public abstract string RuleName { get; }

    public abstract Task<(bool Pass, string? Reason)> EvaluateAsync(
        Appointment appointment,
        ReschedulePolicy policy,
        CancellationToken ct = default);
}
