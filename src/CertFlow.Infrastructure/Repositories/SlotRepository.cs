using CertFlow.Application.Interfaces;
using CertFlow.Domain.Entities;
using CertFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CertFlow.Infrastructure.Repositories;

public class SlotRepository(CertFlowDbContext db) : ISlotRepository
{
    public async Task<IReadOnlyList<AppointmentSlot>> SearchAvailableAsync(
        string city, DateOnly from, DateOnly to,
        string? preferredDayOfWeek, string? preferredTimeOfDay, CancellationToken ct)
    {
        // `from`/`to` are dates as the candidate means them — local to the test centre —
        // so widen the UTC window by a day on each side and narrow it precisely below.
        // DateOnly cannot be translated by the EF Core SQL Server provider; compare via
        // DateTimeOffset bounds.
        var fromDto = new DateTimeOffset(from.AddDays(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var toDto   = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);

        var candidates = await db.AppointmentSlots
            .Include(s => s.TestCenter)
            .Where(s => s.IsAvailable
                     && s.TestCenter.City == city
                     && s.StartUtc >= fromDto
                     && s.StartUtc <= toDto)
            .OrderBy(s => s.StartUtc)
            .ToListAsync(ct);

        // Day-of-week and time-of-day only mean anything in the test centre's own
        // timezone — 03:30 UTC is a 09:00 morning slot in Ahmedabad. Filtering after
        // materialisation keeps this out of SQL, where extracting parts of a
        // DateTimeOffset is not reliably translatable.
        return candidates
            .Select(s => (Slot: s, Local: ToCenterLocalTime(s)))
            .Where(x => DateOnly.FromDateTime(x.Local) >= from
                     && DateOnly.FromDateTime(x.Local) <= to)
            .Where(x => MatchesDayOfWeek(x.Local, preferredDayOfWeek))
            .Where(x => MatchesTimeOfDay(x.Local, preferredTimeOfDay))
            .Select(x => x.Slot)
            .ToList();
    }

    private static DateTime ToCenterLocalTime(AppointmentSlot slot)
    {
        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(slot.TestCenter.IanaTimeZone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            tz = TimeZoneInfo.Utc;
        }
        return TimeZoneInfo.ConvertTime(slot.StartUtc, tz).DateTime;
    }

    // An unrecognised preference is ignored rather than filtering every slot out —
    // returning nothing would read to the agent as "no availability".
    private static bool MatchesDayOfWeek(DateTime local, string? preferred)
    {
        if (string.IsNullOrWhiteSpace(preferred)) return true;
        var day = local.DayOfWeek;
        return preferred.Trim().ToLowerInvariant() switch
        {
            "weekday" => day is >= DayOfWeek.Monday and <= DayOfWeek.Friday,
            "weekend" => day is DayOfWeek.Saturday or DayOfWeek.Sunday,
            var name  => !Enum.TryParse<DayOfWeek>(name, ignoreCase: true, out var wanted)
                         || day == wanted
        };
    }

    private static bool MatchesTimeOfDay(DateTime local, string? preferred)
    {
        if (string.IsNullOrWhiteSpace(preferred)) return true;
        return preferred.Trim().ToLowerInvariant() switch
        {
            "morning"   => local.Hour < 12,
            "afternoon" => local.Hour is >= 12 and < 17,
            "evening"   => local.Hour >= 17,
            _ => true
        };
    }

    public Task<AppointmentSlot?> GetByIdAsync(Guid slotId, CancellationToken ct) =>
        db.AppointmentSlots.Include(s => s.TestCenter)
            .FirstOrDefaultAsync(s => s.Id == slotId, ct);

    public async Task<bool> TryReserveAsync(Guid slotId, CancellationToken ct)
    {
        try
        {
            var rows = await db.AppointmentSlots
                .Where(s => s.Id == slotId && s.IsAvailable)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsAvailable, false), ct);
            return rows > 0;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    public Task ReleaseAsync(Guid slotId, CancellationToken ct) =>
        db.AppointmentSlots.Where(s => s.Id == slotId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsAvailable, true), ct);
}
