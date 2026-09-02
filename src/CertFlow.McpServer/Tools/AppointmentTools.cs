using CertFlow.Application.Interfaces;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace CertFlow.McpServer.Tools;

[McpServerToolType]
public class AppointmentTools(IAppointmentRepository appointments, ISlotRepository slots)
{
    [McpServerTool, Description("Get all upcoming appointments for an Entra user.")]
    public async Task<string> GetUpcomingAppointments(
        [Description("Entra user ID or email")] string entraUserId,
        CancellationToken ct)
    {
        var appts = await appointments.GetUpcomingByCandidateAsync(entraUserId, ct);
        return JsonSerializer.Serialize(appts.Select(a => new
        {
            appointmentId = a.Id,
            examCode = a.Voucher.ExamProgram.Code,
            examName = a.Voucher.ExamProgram.Name,
            startUtc = a.Slot.StartUtc,
            durationMinutes = a.Slot.DurationMinutes,
            testCenter = a.Slot.TestCenter.Name,
            city = a.Slot.TestCenter.City,
            orderNumber = a.OrderNumber,
            registrationId = a.RegistrationId
        }));
    }

    [McpServerTool, Description("Search available slots by city and date range.")]
    public async Task<string> SearchAvailableSlots(
        [Description("Test center city")] string city,
        [Description("Start date (yyyy-MM-dd)")] string fromDate,
        [Description("End date (yyyy-MM-dd)")] string toDate,
        [Description("Preferred day: Monday/Tuesday/.../Saturday/Sunday/Weekday/Weekend")] string? preferredDay,
        [Description("Preferred time: Morning/Afternoon/Evening")] string? preferredTime,
        CancellationToken ct)
    {
        var from = DateOnly.Parse(fromDate);
        var to = DateOnly.Parse(toDate);
        var available = await slots.SearchAvailableAsync(city, from, to, preferredDay, preferredTime, ct);
        return JsonSerializer.Serialize(available.Take(10).Select(s => new
        {
            slotId = s.Id,
            startUtc = s.StartUtc,
            durationMinutes = s.DurationMinutes,
            testCenter = s.TestCenter.Name,
            city = s.TestCenter.City
        }));
    }

    [McpServerTool, Description("Preview the impact of rescheduling an appointment to a given slot.")]
    public async Task<string> PreviewReschedule(
        [Description("Appointment ID")] string appointmentId,
        [Description("Proposed slot ID")] string slotId,
        CancellationToken ct)
    {
        var appt = await appointments.GetByIdAsync(Guid.Parse(appointmentId), ct);
        var slot = await slots.GetByIdAsync(Guid.Parse(slotId), ct);
        if (appt is null || slot is null)
            return JsonSerializer.Serialize(new { error = "Appointment or slot not found" });

        return JsonSerializer.Serialize(new
        {
            currentStart = appt.Slot.StartUtc,
            currentCenter = appt.Slot.TestCenter.Name,
            proposedStart = slot.StartUtc,
            proposedCenter = slot.TestCenter.Name,
            rescheduleFee = 0,          // always free
            voucherExpiry = appt.Voucher.ExpiryDate,
            reschedulesUsed = appt.Voucher.RescheduleCount,
            reschedulesAllowed = appt.Voucher.ExamProgram.Policy.MaxReschedulesPerVoucher
        });
    }
}
