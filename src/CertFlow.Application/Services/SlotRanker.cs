using CertFlow.Contracts.Models;
using CertFlow.Domain.Entities;

namespace CertFlow.Application.Services;

public class SlotRanker
{
    public IReadOnlyList<SlotProposal> Rank(
        IEnumerable<AppointmentSlot> slots,
        string? preferredDayOfWeek,
        string? preferredTimeOfDay,
        int take = 3)
    {
        return slots
            .Select(s => new { Slot = s, Score = Score(s, preferredDayOfWeek, preferredTimeOfDay) })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Slot.StartUtc)
            .Take(take)
            .Select((x, i) => new SlotProposal(
                x.Slot.Id,
                x.Slot.StartUtc,
                x.Slot.DurationMinutes,
                x.Slot.TestCenter.Name,
                x.Slot.TestCenter.City,
                Rank: i + 1))
            .ToList();
    }

    private static int Score(AppointmentSlot slot, string? preferredDay, string? preferredTime)
    {
        int score = 0;
        var localHour = slot.StartUtc.Hour; // simplified — production uses IANA tz

        if (preferredDay is not null)
        {
            var dayName = slot.StartUtc.DayOfWeek.ToString();
            if (dayName.Equals(preferredDay, StringComparison.OrdinalIgnoreCase)) score += 10;
            if (preferredDay.Equals("weekday", StringComparison.OrdinalIgnoreCase)
                && slot.StartUtc.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
                score += 8;
        }

        if (preferredTime is not null)
        {
            score += preferredTime.ToLower() switch
            {
                "morning" when localHour is >= 8 and < 12 => 5,
                "afternoon" when localHour is >= 12 and < 17 => 5,
                "evening" when localHour is >= 17 and < 20 => 5,
                _ => 0
            };
        }

        return score;
    }
}
