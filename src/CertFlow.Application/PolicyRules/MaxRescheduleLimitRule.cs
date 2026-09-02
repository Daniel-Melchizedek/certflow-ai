using CertFlow.Application.Interfaces;
using CertFlow.Domain.Entities;

namespace CertFlow.Application.PolicyRules;

public class MaxRescheduleLimitRule(IRescheduleRequestRepository requests) : PolicyRule
{
    public override string RuleName => "MaxRescheduleLimit";

    public override async Task<(bool Pass, string? Reason)> EvaluateAsync(
        Appointment appointment, ReschedulePolicy policy, CancellationToken ct = default)
    {
        var existingRequests = await requests.GetByAppointmentAsync(appointment.Id, ct);
        var committed = existingRequests.Count(r => r.Status == RescheduleRequestStatus.Committed);

        if (committed >= policy.MaxReschedulesPerVoucher)
            return (false, $"This appointment has already been rescheduled {committed} time(s). Maximum is {policy.MaxReschedulesPerVoucher}.");

        return (true, null);
    }
}
