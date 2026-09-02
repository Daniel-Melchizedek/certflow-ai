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

        if (!voucher.IsValid(targetDate))
            return Task.FromResult((false, $"Exam voucher expired on {voucher.ExpiryDate:dd MMM yyyy}."));

        if (voucher.RescheduleCount >= policy.MaxReschedulesPerVoucher)
            return Task.FromResult((false,
                $"Maximum reschedules ({policy.MaxReschedulesPerVoucher}) for this voucher have been used."));

        return Task.FromResult<(bool, string?)>((true, null));
    }
}
