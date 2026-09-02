using Azure.Identity;
using Azure.Messaging.ServiceBus;
using CertFlow.Agent;
using CertFlow.Application.Handlers;
using CertFlow.Application.Interfaces;
using CertFlow.Application.Services;
using CertFlow.Infrastructure.Email;
using CertFlow.Infrastructure.Messaging;
using CertFlow.Infrastructure.Persistence;
using CertFlow.Infrastructure.Repositories;
using CertFlow.Worker.Consumers;
using CertFlow.Worker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph;
using OpenTelemetry.Resources;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Azure.AI.Projects;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbContext<CertFlowDbContext>(opt =>
    opt.UseSqlServer(builder.Configuration.GetConnectionString("CertFlow")));

builder.Services.AddScoped<IAppointmentRepository, AppointmentRepository>();
builder.Services.AddScoped<ISlotRepository, SlotRepository>();
builder.Services.AddScoped<IRescheduleRequestRepository, RescheduleRequestRepository>();
builder.Services.AddScoped<IAuditRepository, AuditRepository>();
builder.Services.AddScoped<ConfirmRescheduleHandler>();
builder.Services.AddSingleton<CorrelationTokenService>();
builder.Services.AddSingleton<SlotRanker>();

var credential = new DefaultAzureCredential();
var graphClient = new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);
builder.Services.AddSingleton(graphClient);

var mailboxEmail = builder.Configuration["MailboxEmail"]!;
builder.Services.AddScoped<IEmailSender>(_ => new GraphEmailSender(graphClient, mailboxEmail));

var sbClient = new ServiceBusClient(builder.Configuration["ServiceBusNamespace"], credential);
builder.Services.AddSingleton(sbClient);
builder.Services.AddSingleton<ServiceBusPublisher>();

var projectEndpoint = builder.Configuration["AiFoundryProjectEndpoint"]!;
var aiProjectClient = new AIProjectClient(new Uri(projectEndpoint), credential);
builder.Services.AddSingleton(aiProjectClient);
builder.Services.AddSingleton<AgentRegistrationService>();

var mcpBaseUrl = builder.Configuration["McpServerBaseUrl"]!;
builder.Services.AddHttpClient<McpToolExecutor>(c => c.BaseAddress = new Uri(mcpBaseUrl));
builder.Services.AddSingleton<AgentOrchestrator>();

builder.Services.AddHostedService<InboundEmailConsumer>();
builder.Services.AddHostedService<EmailReplyConsumer>();
builder.Services.AddHostedService<GraphSubscriptionRenewalService>();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("CertFlow.Worker"))
    .UseAzureMonitor();

var host = builder.Build();
host.Run();
