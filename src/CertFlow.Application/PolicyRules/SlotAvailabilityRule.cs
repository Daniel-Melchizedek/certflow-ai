using CertFlow.Application.Interfaces;
using CertFlow.Domain.Entities;

namespace CertFlow.Application.PolicyRules;

public class SlotAvailabilityRule(ISlotRepository slots) : PolicyRule
{
    public override string RuleName => "SlotAvailability";

    public override async Task<(bool Pass, string? Reason)> EvaluateAsync(
        Appointment appointment, ReschedulePolicy policy, CancellationToken ct = default)
    {
        var city = appointment.Slot.TestCenter.City;
        var from = DateOnly.FromDateTime(DateTimeOffset.UtcNow.AddHours(policy.MinHoursBeforeExam).UtcDateTime);
        var to = DateOnly.FromDateTime(appointment.Voucher.ExpiryDate.ToDateTime(TimeOnly.MinValue));

        var available = await slots.SearchAvailableAsync(city, from, to, null, null, ct);
        if (available.Count == 0)
            return (false, $"No available slots found in {city} before voucher expiry.");

        return (true, null);
    }
}
