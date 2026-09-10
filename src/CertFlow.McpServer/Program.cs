using Azure.Identity;
using CertFlow.Application.Handlers;
using CertFlow.Application.Interfaces;
using CertFlow.Application.Services;
using CertFlow.Infrastructure.Email;
using CertFlow.Infrastructure.Persistence;
using CertFlow.Infrastructure.Repositories;
using CertFlow.McpServer;
using CertFlow.McpServer.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph;
using OpenTelemetry.Resources;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<CertFlowDbContext>(opt =>
    opt.UseSqlServer(builder.Configuration.GetConnectionString("CertFlow")));

builder.Services.AddScoped<IAppointmentRepository, AppointmentRepository>();
builder.Services.AddScoped<ISlotRepository, SlotRepository>();
builder.Services.AddScoped<IRescheduleRequestRepository, RescheduleRequestRepository>();
builder.Services.AddScoped<IBulkRescheduleRepository, BulkRescheduleRepository>();
builder.Services.AddScoped<IAuditRepository, AuditRepository>();

var credential = new DefaultAzureCredential();
var graphClient = new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);
builder.Services.AddSingleton(graphClient);

// The write tools commit through the same handler the portal uses, so the MCP server needs
// that handler's own dependencies. Its confirmation mail is always suppressed on this path —
// the sender is registered because the handler declares it, not because it is used here.
builder.Services.AddScoped<IEmailSender>(_ =>
    new GraphEmailSender(graphClient, builder.Configuration["MailboxEmail"]!));
builder.Services.AddScoped<CertFlow.Application.PolicyRules.PolicyRule,
    CertFlow.Application.PolicyRules.AppointmentReschedulableRule>();
builder.Services.AddScoped<CertFlow.Application.PolicyRules.PolicyRule,
    CertFlow.Application.PolicyRules.VoucherValidityRule>();
builder.Services.AddScoped<PolicyEngine>();
builder.Services.AddScoped<ConfirmRescheduleHandler>();
builder.Services.AddScoped<RescheduleEmailComposer>();
builder.Services.AddSingleton<CorrelationTokenService>();

builder.Services.AddScoped<CandidateTools>();
builder.Services.AddScoped<AppointmentTools>();

builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("CertFlow.McpServer"))
    // Without this the tool spans in McpTelemetry are created and dropped. Subscribing to the MCP
    // SDK's own source was tried first and produced nothing — see McpTelemetry for why we emit
    // our own rather than depend on a preview package's internals.
    .WithTracing(t => t.AddSource(McpTelemetry.SourceName))
    .UseAzureMonitor();

// Refuse to start rather than run unprotected. This server is reachable from the public internet
// so Foundry can call it, and two of its tools commit bookings — a missing key must fail loudly at
// deploy time, not silently leave the write tools open.
var mcpApiKey = builder.Configuration["McpApiKey"];
if (string.IsNullOrWhiteSpace(mcpApiKey))
    throw new InvalidOperationException(
        "McpApiKey is not configured. The MCP server refuses to start without it because its "
        + "ingress is public and it exposes write tools.");
var expectedKey = Encoding.UTF8.GetBytes(mcpApiKey);

var app = builder.Build();

// Sits ahead of MapMcp and the REST shim, so every caller — Foundry and our own Worker — must
// present the key. Fixed-time comparison keeps the check from leaking the key a byte at a time.
app.Use(async (ctx, next) =>
{
    var provided = ctx.Request.Headers["X-Api-Key"].ToString();
    if (string.IsNullOrEmpty(provided) ||
        !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), expectedKey))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await next();
});

app.MapMcp();

// REST shim so McpToolExecutor can POST to plain HTTP paths instead of MCP protocol.
// Each endpoint resolves the tool class from DI and calls the same method that the
// MCP transport would invoke, keeping the single implementation in the tool classes.

// Tool methods return pre-serialised JSON strings. Results.Content avoids the
// double-encoding that Results.Ok(string) would produce.
// Combined profile+appointments call — preferred; avoids LLM parallel-call placeholder issue.
app.MapPost("/mcp/candidate/context", async (CandidateProfileRequest req, CandidateTools tools, CancellationToken ct) =>
    Results.Content(await tools.GetCandidateContext(req.Email, ct), "application/json"));

app.MapPost("/mcp/candidate/profile", async (CandidateProfileRequest req, CandidateTools tools, CancellationToken ct) =>
    Results.Content(await tools.GetUserProfile(req.Email, ct), "application/json"));

app.MapPost("/mcp/appointments/upcoming", async (UpcomingRequest req, AppointmentTools tools, ILogger<Program> log, CancellationToken ct) =>
{
    log.LogInformation("get_upcoming_appointments: entraUserId={UserId}", req.EntraUserId);
    var result = await tools.GetUpcomingAppointments(req.EntraUserId, ct);
    log.LogInformation("get_upcoming_appointments result: {Result}", result);
    return Results.Content(result, "application/json");
});

app.MapPost("/mcp/policy", async (PolicyRequest req, CandidateTools tools, CancellationToken ct) =>
    Results.Content(await tools.GetExamPolicy(req.ExamCode, ct), "application/json"));

app.MapPost("/mcp/slots/search", async (SlotSearchRequest req, AppointmentTools tools, ILogger<Program> log, CancellationToken ct) =>
{
    log.LogInformation("search_available_slots: city={City} from={From} to={To} day={Day} time={Time}",
        req.City, req.FromDate, req.ToDate, req.PreferredDay, req.PreferredTime);
    var result = await tools.SearchAvailableSlots(req.City, req.FromDate, req.ToDate, req.PreferredDay, req.PreferredTime, ct);
    log.LogInformation("search_available_slots result: {Result}", result);
    return Results.Content(result, "application/json");
});

app.MapPost("/mcp/appointments/preview-reschedule", async (PreviewRescheduleRequest req, AppointmentTools tools, CancellationToken ct) =>
    Results.Content(await tools.PreviewReschedule(req.AppointmentId, req.SlotId, ct), "application/json"));

// Write tools. Logged on both sides: these are the only calls that change a booking, so a
// commit that is later disputed has to be reconstructable from the MCP server's own logs.
app.MapPost("/mcp/bulk/create-session", async (CreateBulkSessionRequest req, AppointmentTools tools, ILogger<Program> log, CancellationToken ct) =>
{
    log.LogInformation("create_bulk_reschedule_session: user={User} messageId={MessageId}",
        req.EntraUserId, req.SourceMessageId);
    var result = await tools.CreateBulkRescheduleSession(
        req.EntraUserId, req.DisplayName, req.SourceMessageId, req.ExamsJson, ct);
    log.LogInformation("create_bulk_reschedule_session result: {Result}", result);
    return Results.Content(result, "application/json");
});

app.MapPost("/mcp/appointments/confirm-reschedule", async (ConfirmSlotRequest req, AppointmentTools tools, ILogger<Program> log, CancellationToken ct) =>
{
    log.LogInformation("confirm_reschedule_slot: requestId={RequestId} slotId={SlotId}",
        req.RescheduleRequestId, req.SlotId);
    var result = await tools.ConfirmRescheduleSlot(req.RescheduleRequestId, req.SlotId, req.NotifyEmail, ct);
    log.LogInformation("confirm_reschedule_slot result: {Result}", result);
    return Results.Content(result, "application/json");
});

app.Run();

record CandidateProfileRequest(string Email);
record UpcomingRequest(string EntraUserId);
record PolicyRequest(string ExamCode);
record SlotSearchRequest(string City, string FromDate, string ToDate, string? PreferredDay, string? PreferredTime);
record PreviewRescheduleRequest(string AppointmentId, string SlotId);
record CreateBulkSessionRequest(string EntraUserId, string DisplayName, string SourceMessageId, string ExamsJson);
record ConfirmSlotRequest(string RescheduleRequestId, string SlotId, string? NotifyEmail);
