using CertFlow.Domain.Entities;

namespace CertFlow.Application.PolicyRules;

public class AppointmentReschedulableRule : PolicyRule
{
    public override string RuleName => "AppointmentReschedulable";

    public override Task<(bool Pass, string? Reason)> EvaluateAsync(
        Appointment appointment, ReschedulePolicy policy, CancellationToken ct = default)
    {
        if (appointment.Status is AppointmentStatus.Cancelled or AppointmentStatus.Completed)
            return Task.FromResult((false, $"Appointment is {appointment.Status} and cannot be rescheduled."));

        var hoursUntilExam = (appointment.Slot.StartUtc - DateTimeOffset.UtcNow).TotalHours;
        if (hoursUntilExam < policy.MinHoursBeforeExam)
            return Task.FromResult((false,
                $"Rescheduling must be requested at least {policy.MinHoursBeforeExam} hours before the exam. " +
                $"Your exam starts in {hoursUntilExam:F0} hours."));

        return Task.FromResult<(bool, string?)>((true, null));
    }
}
