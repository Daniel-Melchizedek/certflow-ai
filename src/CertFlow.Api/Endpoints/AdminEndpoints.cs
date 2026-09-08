using CertFlow.Application.Interfaces;
using CertFlow.Contracts.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace CertFlow.Api.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/graph-subscription/create", async (
            GraphServiceClient graph,
            IConfiguration config,
            CancellationToken ct) =>
        {
            var mailboxId = config["MailboxEmail"]!;
            var webhookUrl = $"{config["WebhookBaseUrl"]}/api/emails/graph-webhook";
            var lifecycleUrl = $"{config["WebhookBaseUrl"]}/api/emails/graph-lifecycle";

            // Check for existing subscription
            var existing = await graph.Subscriptions.GetAsync(cancellationToken: ct);
            var active = existing?.Value?.FirstOrDefault(s =>
                s.NotificationUrl == webhookUrl && s.ExpirationDateTime > DateTimeOffset.UtcNow);

            // Graph rejects adding lifecycleNotificationUrl via PATCH, so a subscription
            // created without one has to be torn down and rebuilt to gain it.
            if (active is not null && !string.IsNullOrEmpty(active.LifecycleNotificationUrl))
                return Results.Ok(new { subscriptionId = active.Id, message = "Already active" });

            if (active is not null)
                await graph.Subscriptions[active.Id].DeleteAsync(cancellationToken: ct);

            var sub = await graph.Subscriptions.PostAsync(new Subscription
            {
                ChangeType = "created",
                NotificationUrl = webhookUrl,
                LifecycleNotificationUrl = lifecycleUrl,
                Resource = $"users/{mailboxId}/mailFolders/Inbox/messages",
                ExpirationDateTime = DateTimeOffset.UtcNow.AddDays(3),
                ClientState = config["GraphClientState"] ?? "certflow-webhook-secret"
            }, cancellationToken: ct);

            return Results.Ok(new
            {
                subscriptionId = sub!.Id,
                message = active is not null ? "Recreated with lifecycle URL" : "Created",
                expiresAt = sub.ExpirationDateTime
            });
        });

        app.MapPost("/admin/agents/register", async (
            [FromServices] CertFlow.Agent.AgentRegistrationService agentReg,
            CancellationToken ct) =>
        {
            await agentReg.RegisterAllAsync(ct);
            return Results.Ok(new { message = "Agents registered" });
        });

        app.MapGet("/admin/audit", async (
            IAuditRepository audit,
            string? candidateId,
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? correlationId,
            CancellationToken ct) =>
        {
            var events = await audit.QueryAsync(candidateId, from, to, correlationId, ct);
            return Results.Ok(events.Select(e => new AuditEventDto(
                e.Id, e.CorrelationId, e.EventType, e.ActorType,
                e.CandidateEntraUserId, e.Payload, e.Timestamp)));
        });

        app.MapGet("/admin/requests", async (
            CertFlow.Infrastructure.Persistence.CertFlowDbContext db,
            CancellationToken ct) =>
        {
            // Materialize before the enum→string and string-truncation projections,
            // which EF Core cannot translate to SQL.
            var rows = await db.RescheduleRequests
                .OrderByDescending(r => r.CreatedAt)
                .Take(100)
                .ToListAsync(ct);

            return Results.Ok(rows.Select(r => new
            {
                r.Id,
                r.AppointmentId,
                r.CandidateEntraUserId,
                status = r.Status.ToString(),
                r.CorrelationToken,
                agentReasoning = r.AgentReasoning?.Length > 200
                    ? r.AgentReasoning[..200] + "…"
                    : r.AgentReasoning,
                r.CreatedAt
            }));
        });

        app.MapGet("/admin/debug/appointments/{entraUserId}", async (
            string entraUserId,
            IAppointmentRepository appointments,
            CancellationToken ct) =>
        {
            var appts = await appointments.GetUpcomingByCandidateAsync(entraUserId, ct);
            return Results.Ok(appts.Select(a => new
            {
                appointmentId = a.Id,
                candidateEntraUserId = a.CandidateEntraUserId,
                examCode = a.Voucher.ExamProgram.Code,
                startUtc = a.Slot.StartUtc,
                testCenter = a.Slot.TestCenter.Name,
                status = a.Status
            }));
        });

        app.MapGet("/admin/debug/all-candidates", async (
            CertFlow.Infrastructure.Persistence.CertFlowDbContext db,
            CancellationToken ct) =>
        {
            var candidates = await db.Appointments
                .Select(a => new
                {
                    a.CandidateEntraUserId,
                    a.Status,
                    slotStart = a.Slot.StartUtc
                })
                .ToListAsync(ct);
            return Results.Ok(candidates);
        });

        // Shows all reschedule requests for diagnostic purposes
        app.MapGet("/admin/debug/reschedule-requests", async (
            CertFlow.Infrastructure.Persistence.CertFlowDbContext db,
            CancellationToken ct) =>
        {
            var requests = await db.RescheduleRequests
                .Select(r => new
                {
                    r.Id,
                    r.AppointmentId,
                    r.CandidateEntraUserId,
                    r.Status,
                    r.CorrelationToken,
                    r.AgentReasoning,
                    r.ProposedSlotIds,
                    r.CreatedAt
                })
                .ToListAsync(ct);
            return Results.Ok(requests);
        });

        // Lists available slots by city (broad search for diagnostics)
        app.MapGet("/admin/debug/slots", async (
            string? city,
            string? from,
            string? to,
            CertFlow.Infrastructure.Persistence.CertFlowDbContext db,
            CancellationToken ct) =>
        {
            var query = db.AppointmentSlots.Include(s => s.TestCenter).Where(s => s.IsAvailable);
            if (city != null) query = query.Where(s => s.TestCenter.City == city);
            if (from != null)
            {
                var fromDto = new DateTimeOffset(DateOnly.Parse(from).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                query = query.Where(s => s.StartUtc >= fromDto);
            }
            if (to != null)
            {
                var toDto = new DateTimeOffset(DateOnly.Parse(to).ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);
                query = query.Where(s => s.StartUtc <= toDto);
            }
            var slots = await query
                .OrderBy(s => s.StartUtc)
                .Select(s => new { s.Id, s.StartUtc, city = s.TestCenter.City, center = s.TestCenter.Name })
                .Take(50)
                .ToListAsync(ct);
            return Results.Ok(new { count = slots.Count, slots });
        });

        // Inserts 3 guaranteed future slots in Ahmedabad (morning, afternoon, morning) for demo
        app.MapPost("/admin/debug/seed-demo-slots", async (
            CertFlow.Infrastructure.Persistence.CertFlowDbContext db,
            CancellationToken ct) =>
        {
            var center = await db.TestCenters.FirstAsync(tc => tc.City == "Ahmedabad", ct);
            var now = DateTimeOffset.UtcNow;

            // Three slots: 7 days, 10 days, 14 days from now (morning IST = ~3:30 AM UTC)
            var newSlots = new[]
            {
                new CertFlow.Domain.Entities.AppointmentSlot { Id = Guid.NewGuid(), TestCenterId = center.Id,
                    StartUtc = now.Date.AddDays(7).AddHours(3).AddMinutes(30), DurationMinutes = 120, IsAvailable = true },
                new CertFlow.Domain.Entities.AppointmentSlot { Id = Guid.NewGuid(), TestCenterId = center.Id,
                    StartUtc = now.Date.AddDays(10).AddHours(4).AddMinutes(0), DurationMinutes = 120, IsAvailable = true },
                new CertFlow.Domain.Entities.AppointmentSlot { Id = Guid.NewGuid(), TestCenterId = center.Id,
                    StartUtc = now.Date.AddDays(14).AddHours(3).AddMinutes(30), DurationMinutes = 120, IsAvailable = true }
            };

            db.AppointmentSlots.AddRange(newSlots);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { inserted = newSlots.Length, slots = newSlots.Select(s => new { s.Id, s.StartUtc }) });
        });

        // Sends a real email into the mailbox as a tenant user, so the Graph webhook path
        // (Exchange -> notification -> sender validation -> Service Bus) can be exercised
        // without knowing that user's password. The recipient is always the configured
        // mailbox — it is deliberately not caller-controlled.
        app.MapPost("/admin/debug/send-test-email", async (
            SendTestEmailRequest req,
            GraphServiceClient graph,
            IConfiguration config,
            CancellationToken ct) =>
        {
            var mailbox = config["MailboxEmail"]!;
            await graph.Users[req.FromUser].SendMail.PostAsync(new()
            {
                Message = new Message
                {
                    Subject = req.Subject,
                    Body = new ItemBody { ContentType = BodyType.Text, Content = req.Body },
                    ToRecipients =
                    [
                        new Recipient { EmailAddress = new EmailAddress { Address = mailbox } }
                    ]
                },
                SaveToSentItems = false
            }, cancellationToken: ct);

            return Results.Ok(new { sent = true, from = req.FromUser, to = mailbox, req.Subject });
        });

        // Replies AS a candidate to the newest CertFlow message in their inbox. Sending a fresh
        // mail cannot stand in for this: a real reply carries the conversation headers and the
        // quoted [REF:token], which is exactly what the routing and choice parsing depend on.
        app.MapPost("/admin/debug/reply-as-user", async (
            ReplyAsUserRequest req,
            GraphServiceClient graph,
            IConfiguration config,
            CancellationToken ct) =>
        {
            var mailbox = config["MailboxEmail"]!;

            // Filtering server-side on from/emailAddress/address alongside an orderby is
            // rejected as an inefficient filter, so take a small recent page and pick here.
            var page = await graph.Users[req.FromUser].MailFolders["Inbox"].Messages
                .GetAsync(r =>
                {
                    r.QueryParameters.Top = 15;
                    r.QueryParameters.Orderby = ["receivedDateTime desc"];
                    r.QueryParameters.Select = ["id", "subject", "receivedDateTime", "from"];
                }, ct);

            var target = page?.Value?.FirstOrDefault(m =>
                string.Equals(m.From?.EmailAddress?.Address, mailbox, StringComparison.OrdinalIgnoreCase));

            if (target is null)
                return Results.NotFound(new { error = $"No message from {mailbox} in {req.FromUser}'s inbox." });

            await graph.Users[req.FromUser].Messages[target.Id]
                .Reply
                .PostAsync(new Microsoft.Graph.Users.Item.Messages.Item.Reply.ReplyPostRequestBody
                {
                    Comment = req.Body
                }, cancellationToken: ct);

            return Results.Ok(new
            {
                replied = true,
                from = req.FromUser,
                repliedToSubject = target.Subject,
                repliedToReceived = target.ReceivedDateTime,
                body = req.Body
            });
        });

        // Returns the newest message in the shared mailbox with its raw body, so the reply
        // parser can be checked against what Outlook actually sends rather than a guess at it.
        app.MapGet("/admin/debug/last-mailbox-message", async (
            GraphServiceClient graph,
            IConfiguration config,
            CancellationToken ct,
            string? user = null,
            int top = 1) =>
        {
            // Defaults to the shared mailbox (inbound candidate mail); pass ?user= to read a
            // candidate's own inbox and see exactly what CertFlow delivered to them. ?top= lets
            // a batch of test emails be verified in one call instead of one round trip each.
            var mailbox = user ?? config["MailboxEmail"]!;
            var page = await graph.Users[mailbox].MailFolders["Inbox"].Messages
                .GetAsync(r =>
                {
                    r.QueryParameters.Top = Math.Clamp(top, 1, 25);
                    r.QueryParameters.Orderby = ["receivedDateTime desc"];
                    r.QueryParameters.Select = ["id", "subject", "receivedDateTime", "from", "body"];
                }, ct);

            var messages = page?.Value ?? [];
            if (messages.Count == 0) return Results.NotFound(new { error = "Inbox empty." });

            return Results.Ok(messages.Select(m => new
            {
                id = m.Id,
                subject = m.Subject,
                received = m.ReceivedDateTime,
                from = m.From?.EmailAddress?.Address,
                contentType = m.Body?.ContentType?.ToString(),
                body = m.Body?.Content
            }));
        });

        // Wipes every row and re-runs the seed. Needed because ApplyAsync short-circuits when
        // ExamPrograms already exist, so an existing environment never picks up catalogue
        // changes — a fresh deployment onto an empty database does this automatically.
        // Destructive: intended for demo environments only.
        app.MapPost("/admin/debug/reset-seed", async (
            CertFlow.Infrastructure.Persistence.CertFlowDbContext db,
            CancellationToken ct) =>
        {
            // Child-to-parent order; the FK graph will not tolerate anything else.
            await db.AuditEvents.ExecuteDeleteAsync(ct);
            await db.RescheduleRequests.ExecuteDeleteAsync(ct);
            // After their children, since a session owns the requests that point at it. The
            // claim log has no FKs but is cleared too: leaving it would make a replayed test
            // email look like a duplicate and get dropped without a reply.
            await db.BulkRescheduleSessions.ExecuteDeleteAsync(ct);
            await db.ProcessedInboundMessages.ExecuteDeleteAsync(ct);
            await db.Appointments.ExecuteDeleteAsync(ct);
            await db.ExamVouchers.ExecuteDeleteAsync(ct);
            await db.AppointmentSlots.ExecuteDeleteAsync(ct);
            await db.ReschedulePolicies.ExecuteDeleteAsync(ct);
            await db.ExamPrograms.ExecuteDeleteAsync(ct);
            await db.TestCenters.ExecuteDeleteAsync(ct);
            await db.Candidates.ExecuteDeleteAsync(ct);

            await CertFlow.Infrastructure.Persistence.SeedData.ApplyAsync(db);

            var programs = await db.ExamPrograms
                .Select(p => new { p.Code, p.Name, p.DurationMinutes })
                .ToListAsync(ct);

            return Results.Ok(new
            {
                reseeded = true,
                programs,
                appointments = await db.Appointments.CountAsync(ct),
                slots = await db.AppointmentSlots.CountAsync(ct)
            });
        });

        // Moves an appointment's current slot to N hours from now. The seed only ever creates
        // slots days out, so without this there is no way to land inside the 24-hour notice
        // window and prove the commit-time guardrail actually refuses. Test hook only.
        app.MapPost("/admin/debug/set-appointment-hours-out/{appointmentId:guid}", async (
            Guid appointmentId,
            int hours,
            CertFlow.Infrastructure.Persistence.CertFlowDbContext db,
            CancellationToken ct) =>
        {
            var appt = await db.Appointments
                .Include(a => a.Slot)
                .FirstOrDefaultAsync(a => a.Id == appointmentId, ct);
            if (appt is null) return Results.NotFound();

            appt.Slot.StartUtc = DateTimeOffset.UtcNow.AddHours(hours);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { appointmentId, slotId = appt.SlotId, newStartUtc = appt.Slot.StartUtc, hoursOut = hours });
        });

        // Clears the reschedule counter on a candidate's vouchers so the max-reschedules policy
        // can be exercised repeatedly during demos and testing. Test reset only.
        app.MapPost("/admin/debug/reset-reschedule-count/{entraUserId}", async (
            string entraUserId,
            CertFlow.Infrastructure.Persistence.CertFlowDbContext db,
            CancellationToken ct) =>
        {
            var updated = await db.ExamVouchers
                .Where(v => v.CandidateEntraUserId == entraUserId)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.RescheduleCount, 0), ct);

            return Results.Ok(new { vouchersReset = updated });
        });

        // Deletes all reschedule requests for a candidate (demo/test reset only)
        app.MapDelete("/admin/debug/reschedule-requests/{entraUserId}", async (
            string entraUserId,
            CertFlow.Infrastructure.Persistence.CertFlowDbContext db,
            CancellationToken ct) =>
        {
            var deleted = await db.RescheduleRequests
                .Where(r => r.CandidateEntraUserId == entraUserId)
                .ExecuteDeleteAsync(ct);
            return Results.Ok(new { deleted });
        });

        // Enrols a real Entra user as a candidate with a voucher + booked appointment.
        // Idempotent — safe to call multiple times; skips if candidate already exists.
        app.MapPost("/admin/debug/enrol-candidate", async (
            EnrolCandidateRequest req,
            CertFlow.Infrastructure.Persistence.CertFlowDbContext db,
            CancellationToken ct) =>
        {
            await CertFlow.Infrastructure.Persistence.SeedData.EnrolEntraCandidateAsync(
                db, req.EntraUserId, req.DisplayName, req.Email, ct);
            return Results.Ok(new { enrolled = true, req.EntraUserId, req.Email });
        });

        // Dry-run of the whole inbound pipeline: runs both agents against a supplied email body,
        // applies the same fallback-search and branch selection the Worker uses, and returns the
        // rendered HTML. Writes nothing and sends nothing, so agent wording can be iterated on
        // without a real mailbox round-trip.
        app.MapPost("/admin/debug/simulate-inbound", async (
            SimulateInboundRequest req,
            CertFlow.Agent.AgentOrchestrator orchestrator,
            CertFlow.Application.Services.RescheduleEmailComposer composer,
            CertFlow.Application.Interfaces.ISlotRepository slotRepo,
            CertFlow.Application.Interfaces.IAppointmentRepository apptRepo,
            CancellationToken ct) =>
        {
            var email = new CertFlow.Contracts.Models.EmailMessage(
                MessageId: "simulated",
                SenderEmail: req.SenderEmail,
                Subject: req.Subject ?? "reschedule my exam",
                Body: req.Body,
                ReceivedAt: DateTimeOffset.UtcNow,
                InReplyToMessageId: null);

            var intent = await orchestrator.RunIntentAgentAsync(email, ct);
            if (intent is null || intent.IsAmbiguous || (intent.AppointmentId is null && !intent.IsBulk))
                return Results.Ok(new
                {
                    branch = "need-more-info",
                    intent,
                    proposalCount = 0,
                    html = composer.NeedMoreInfo(intent?.DisplayName ?? "there", intent?.ClarificationNeeded)
                });

            // ── Bulk path ────────────────────────────────────────────────────────────
            if (intent.IsBulk)
            {
                var appointmentIds = intent.AppointmentIds ?? [];
                var examProposals = new List<CertFlow.Contracts.Models.BulkExamProposal>();
                var offeredSlotIds = new HashSet<Guid>();

                foreach (var appointmentId in appointmentIds)
                {
                    var appointment = await apptRepo.GetByIdAsync(appointmentId, ct);
                    if (appointment is null) continue;

                    var prog = appointment.Voucher.ExamProgram;
                    var single = intent with
                    {
                        AppointmentId = appointmentId,
                        ExamCode = prog.Code,
                        PreferredCity = string.IsNullOrWhiteSpace(intent.PreferredCity)
                            ? appointment.Slot.TestCenter.City
                            : intent.PreferredCity,
                        IsBulk = false,
                        AppointmentIds = null
                    };

                    var pol = await orchestrator.RunPolicyAgentAsync(single, ct);

                    // De-dup within the agent's own list, then filter against already-offered slots
                    IReadOnlyList<CertFlow.Contracts.Models.SlotProposal> props = pol?.ProposedSlots is { Count: > 0 }
                        ? [.. pol.ProposedSlots.DistinctBy(s => s.SlotId).OrderBy(s => s.Rank).Select((s, i) => s with { Rank = i + 1 })]
                        : [];

                    var kept = new List<CertFlow.Contracts.Models.SlotProposal>();
                    foreach (var s in props.OrderBy(x => x.Rank))
                        if (offeredSlotIds.Add(s.SlotId))
                            kept.Add(s with { Rank = kept.Count + 1 });
                    props = kept;

                    bool isExamFallback = false;
                    if (props.Count == 0 && pol?.IsEligible == true)
                    {
                        var city2 = string.IsNullOrWhiteSpace(intent.PreferredCity)
                            ? appointment.Slot.TestCenter.City : intent.PreferredCity;
                        var fromDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(1));
                        var found2 = await slotRepo.SearchAvailableAsync(city2, fromDate, fromDate.AddMonths(6), null, intent.PreferredTimeOfDay, ct);
                        if (found2.Count == 0)
                            found2 = await slotRepo.SearchAvailableAsync(city2, fromDate, fromDate.AddMonths(6), null, null, ct);

                        var backfillKept = new List<CertFlow.Contracts.Models.SlotProposal>();
                        foreach (var s in found2.Where(s => !offeredSlotIds.Contains(s.Id)).OrderBy(s => s.StartUtc).Take(3))
                            if (offeredSlotIds.Add(s.Id))
                                backfillKept.Add(new CertFlow.Contracts.Models.SlotProposal(
                                    s.Id, s.StartUtc, s.DurationMinutes, s.TestCenter.Name, s.TestCenter.City, backfillKept.Count + 1));

                        props = backfillKept;
                        isExamFallback = props.Count > 0;
                    }

                    examProposals.Add(new CertFlow.Contracts.Models.BulkExamProposal(
                        appointmentId, prog.Code, prog.Name,
                        appointment.Slot.StartUtc, appointment.Slot.TestCenter.IanaTimeZone,
                        appointment.Slot.TestCenter.City, props, isExamFallback));
                }

                var bulkHtml = await composer.BulkProposalAsync(intent.DisplayName, examProposals, "TESTSESSION", ct);
                return Results.Ok(new
                {
                    branch = "bulk-proposal",
                    intent,
                    examCount = examProposals.Count,
                    proposalCount = examProposals.Sum(e => e.Slots.Count),
                    exams = examProposals.Select(e => new { e.ExamCode, slotCount = e.Slots.Count, e.IsFallback }),
                    html = bulkHtml
                });
            }

            // ── Single-exam path ─────────────────────────────────────────────────────
            var policy = await orchestrator.RunPolicyAgentAsync(intent, ct);
            if (policy is null)
                return Results.Ok(new
                {
                    branch = "policy-null",
                    intent,
                    proposalCount = 0,
                    html = composer.NeedMoreInfo(intent.DisplayName, null)
                });

            var proposals = (IReadOnlyList<CertFlow.Contracts.Models.SlotProposal>)(policy.ProposedSlots ?? []);
            var isFallback = false;
            if (policy.IsEligible && proposals.Count == 0)
            {
                var appointment = await apptRepo.GetByIdAsync(intent.AppointmentId!.Value, ct);
                var city = !string.IsNullOrWhiteSpace(intent.PreferredCity)
                    ? intent.PreferredCity
                    : appointment?.Slot.TestCenter.City;

                if (!string.IsNullOrWhiteSpace(city))
                {
                    var from = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(1));
                    var to = from.AddMonths(6);
                    var found = await slotRepo.SearchAvailableAsync(city, from, to, null, intent.PreferredTimeOfDay, ct);
                    if (found.Count == 0)
                        found = await slotRepo.SearchAvailableAsync(city, from, to, null, null, ct);

                    proposals = [.. found.OrderBy(s => s.StartUtc).Take(3)
                        .Select((s, i) => new CertFlow.Contracts.Models.SlotProposal(
                            s.Id, s.StartUtc, s.DurationMinutes, s.TestCenter.Name, s.TestCenter.City, i + 1))];
                    isFallback = proposals.Count > 0;
                }
            }
            else if (proposals.Count > 0)
            {
                isFallback = CertFlow.Application.Services.RescheduleEmailComposer
                    .FallsOutsideRequestedWindow(proposals, intent.PreferredFromDate, intent.PreferredToDate);
            }

            string branch, html;
            if (proposals.Count > 0)
            {
                branch = isFallback ? "proposal-fallback" : "proposal";
                html = await composer.ProposalAsync(intent.DisplayName, proposals, "TESTTOKEN", isFallback, ct);
            }
            else if (!policy.IsEligible)
            {
                branch = "rejected";
                html = composer.Rejection(intent.DisplayName, policy.RejectionReason, "TESTTOKEN");
            }
            else
            {
                branch = "no-availability";
                html = composer.NoAvailability(intent.DisplayName, intent.ExamCode, intent.PreferredCity,
                    intent.PreferredFromDate, intent.PreferredToDate, "TESTTOKEN");
            }

            return Results.Ok(new { branch, intent, policy, proposalCount = proposals.Count, html });
        });

        // Proxy a single MCP tool call through the API so integration tests can reach the
        // internal-only MCP server without needing direct ingress. Debug/test endpoint only.
        app.MapPost("/admin/debug/mcp-call", async (
            McpToolCallRequest req,
            CertFlow.Agent.McpToolExecutor mcpTools,
            CancellationToken ct) =>
        {
            try
            {
                var result = await mcpTools.ExecuteAsync(req.Tool, req.ArgsJson, ct);
                return Results.Ok(new { tool = req.Tool, result });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { tool = req.Tool, error = ex.Message });
            }
        });
    }

    private record SendTestEmailRequest(string FromUser, string Subject, string Body);
    private record ReplyAsUserRequest(string FromUser, string Body);
    private record EnrolCandidateRequest(string EntraUserId, string DisplayName, string Email);
    private record SimulateInboundRequest(string SenderEmail, string Body, string? Subject);
    private record McpToolCallRequest(string Tool, string ArgsJson);
}
