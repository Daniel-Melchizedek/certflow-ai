using Azure.AI.Projects;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using CertFlow.Agent;
using CertFlow.Api.Endpoints;
using CertFlow.Application.Interfaces;
using CertFlow.Infrastructure.Persistence;
using CertFlow.Infrastructure.Repositories;
using CertFlow.Infrastructure.Email;
using CertFlow.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph;
using Scalar.AspNetCore;
using OpenTelemetry.Resources;
using Azure.Monitor.OpenTelemetry.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

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

var mailboxEmail = builder.Configuration["MailboxEmail"]!;
builder.Services.AddScoped<IEmailSender>(_ => new GraphEmailSender(graphClient, mailboxEmail));

var sbClient = new ServiceBusClient(builder.Configuration["ServiceBusNamespace"], credential);
builder.Services.AddSingleton(sbClient);
builder.Services.AddSingleton<ServiceBusPublisher>();

// Needed by POST /admin/agents/register, which verifies the Foundry project is
// reachable and has a gpt-4o deployment.
var projectEndpoint = builder.Configuration["AiFoundryProjectEndpoint"]!;
builder.Services.AddSingleton(new AIProjectClient(new Uri(projectEndpoint), credential));
builder.Services.AddSingleton<AgentRegistrationService>();

builder.Services.AddScoped<CertFlow.Application.Services.RescheduleEmailComposer>();

// Same commit-time guardrails the Worker enforces. The portal reschedule endpoint writes
// directly to the database without any agent in the loop, so it needs its own check.
builder.Services.AddScoped<CertFlow.Application.PolicyRules.PolicyRule,
    CertFlow.Application.PolicyRules.AppointmentReschedulableRule>();
builder.Services.AddScoped<CertFlow.Application.PolicyRules.PolicyRule,
    CertFlow.Application.PolicyRules.VoucherValidityRule>();
builder.Services.AddScoped<CertFlow.Application.Services.PolicyEngine>();

// Backs POST /admin/debug/simulate-inbound, which runs both agents against a supplied email
// body and renders the reply without sending it — the only way to exercise agent wording
// without a real candidate mailbox in the loop. Registered only when the MCP server is
// configured so the API still starts if that endpoint is not wired up.
var mcpBaseUrl = builder.Configuration["McpServerBaseUrl"];
if (!string.IsNullOrWhiteSpace(mcpBaseUrl))
{
    builder.Services.AddHttpClient<McpToolExecutor>(c =>
    {
        c.BaseAddress = new Uri(mcpBaseUrl);
        c.DefaultRequestHeaders.Add("X-Api-Key", builder.Configuration["McpApiKey"]!);
    });
    builder.Services.AddSingleton(new FoundryMcpToolOptions(
        ServerUrl:          mcpBaseUrl,
        ConnectionId:       builder.Configuration["FoundryMcpConnectionId"] ?? "certflow-mcp",
        ProjectEndpoint:    builder.Configuration["AiFoundryProjectEndpoint"],
        WorkIQConnectionId: builder.Configuration["WorkIQConnectionId"]));
    builder.Services.AddSingleton<AgentOrchestrator>();
}

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("CertFlow.Api"))
    .WithTracing(t => t.AddSource("OpenAI").AddSource("Azure.AI.*"))
    .UseAzureMonitor();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapGraphWebhookEndpoints();
app.MapAdminEndpoints();
app.MapAppointmentEndpoints();
app.MapSlotAdvisorEndpoints();

// Migrate + seed mode for the Container Apps Job in deploy.ps1.
// Accepts either the CLI arg or MIGRATE_AND_SEED=true — the env var is used by the
// job because `az containerapp job` does not split space-separated --args values.
var migrateAndSeed =
    args.Contains("--migrate-and-seed") ||
    string.Equals(Environment.GetEnvironmentVariable("MIGRATE_AND_SEED"), "true",
                  StringComparison.OrdinalIgnoreCase);

if (migrateAndSeed)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CertFlowDbContext>();
    await SeedData.ApplyAsync(db);   // runs MigrateAsync() then seeds

    // Enrol real tenant users as candidates. The email flow resolves a sender to an Entra
    // object id via Graph and looks appointments up by it, so demo candidates have to be
    // backed by actual Entra users to be reachable at all.
    var upns = (builder.Configuration["SeedCandidateUpns"] ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    if (upns.Length > 0)
    {
        var graph = scope.ServiceProvider.GetRequiredService<GraphServiceClient>();
        foreach (var upn in upns)
        {
            try
            {
                var user = await graph.Users[upn].GetAsync(r =>
                    r.QueryParameters.Select = ["id", "displayName", "mail", "userPrincipalName"]);
                if (user?.Id is null) { Console.WriteLine($"Skipped {upn}: not found in Entra."); continue; }

                await SeedData.EnrolEntraCandidateAsync(
                    db, user.Id, user.DisplayName ?? upn, user.Mail ?? user.UserPrincipalName ?? upn);
                Console.WriteLine($"Enrolled candidate {upn} ({user.Id}).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Skipped {upn}: {ex.Message}");
            }
        }
    }

    Console.WriteLine("Migration and seed completed successfully.");
    return;
}

app.Run();
