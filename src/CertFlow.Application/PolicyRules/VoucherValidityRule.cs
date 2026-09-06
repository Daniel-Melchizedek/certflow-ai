using CertFlow.Domain.Entities;

namespace CertFlow.Application.PolicyRules;

public class VoucherValidityRule : PolicyRule
{
    public override string RuleName => "VoucherValidity";

    public override Task<(bool Pass, string? Reason)> EvaluateAsync(
        Appointment appointment, ReschedulePolicy policy, CancellationToken ct = default)
    {
        var voucher = appointment.Voucher;
        var targetDate = DateOnly.FromDateTime(appointment.Slot.StartUtc.UtcDateTime);

        // Voucher expiry only. There is no cap on how many times a booking may be moved, so
        // RescheduleCount is tracked for reporting but never gates a reschedule.
        if (!voucher.IsValid(targetDate))
            return Task.FromResult((false, $"Exam voucher expired on {voucher.ExpiryDate:dd MMM yyyy}."));

        return Task.FromResult<(bool, string?)>((true, null));
    }
}
