using CertFlow.Application.Interfaces;
using CertFlow.Application.PolicyRules;
using CertFlow.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CertFlow.Application.Services;

public class PolicyEngine(
    IEnumerable<PolicyRule> rules,
    ILogger<PolicyEngine> logger)
{
    public async Task<(bool Eligible, string? RejectionReason)> EvaluateAsync(
        Appointment appointment,
        ReschedulePolicy policy,
        CancellationToken ct = default)
    {
        foreach (var rule in rules)
        {
            var (pass, reason) = await rule.EvaluateAsync(appointment, policy, ct);
            if (!pass)
            {
                logger.LogInformation("Policy rule {Rule} rejected appointment {AppointmentId}: {Reason}",
                    rule.RuleName, appointment.Id, reason);
                return (false, reason);
            }
        }
        return (true, null);
    }
}
