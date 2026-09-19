using Microsoft.Graph;
using Microsoft.Graph.Models;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace CertFlow.McpServer.Tools;

[McpServerToolType]
public class CalendarTools(GraphServiceClient graphClient)
{
    [McpServerTool(Name = "check_calendar_conflicts"),
     Description("Check whether a candidate's Outlook calendar has meetings overlapping a proposed "
               + "exam slot. Call once per slot you intend to propose, passing that slot's own start and end.")]
    public async Task<string> CheckCalendarConflicts(
        [Description("Candidate's Entra user id or email address")] string entraUserId,
        [Description("Slot start, ISO 8601 UTC, e.g. 2026-10-14T09:00:00Z")] string startUtc,
        [Description("Slot end, ISO 8601 UTC, e.g. 2026-10-14T11:00:00Z")] string endUtc,
        CancellationToken ct = default)
    {
        using var span = McpTelemetry.StartTool("check_calendar_conflicts");
        try
        {
            var events = await graphClient.Users[entraUserId].CalendarView.GetAsync(req =>
            {
                req.QueryParameters.StartDateTime = startUtc;
                req.QueryParameters.EndDateTime   = endUtc;
                req.QueryParameters.Select        = ["subject", "start", "end", "showAs", "sensitivity", "isAllDay"];
                req.QueryParameters.Top           = 25;
            }, ct);

            // Busy/Oof/Tentative always block. All-day Free events (holidays, blocked days, conference
            // days) also block — the day is occupied even if not marked busy. Timed Free events and
            // WorkingElsewhere are genuinely available time and are excluded.
            var conflicts = (events?.Value ?? [])
                .Where(e => e.ShowAs is FreeBusyStatus.Busy
                                     or FreeBusyStatus.Oof
                                     or FreeBusyStatus.Tentative
                            || (e.IsAllDay == true && e.ShowAs is FreeBusyStatus.Free))
                .Select(e => new
                {
                    subject = e.Subject ?? "(No subject)",
                    start   = e.Start?.DateTime,
                    end     = e.End?.DateTime
                })
                .ToList();

            return JsonSerializer.Serialize(new
            {
                hasConflicts = conflicts.Count > 0,
                conflicts
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }
}
