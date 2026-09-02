using Azure.Identity;
using Azure.Messaging.ServiceBus;
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
builder.Services.AddScoped<IAuditRepository, AuditRepository>();

var credential = new DefaultAzureCredential();
var graphClient = new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);
builder.Services.AddSingleton(graphClient);

var mailboxEmail = builder.Configuration["MailboxEmail"]!;
builder.Services.AddScoped<IEmailSender>(_ => new GraphEmailSender(graphClient, mailboxEmail));

var sbClient = new ServiceBusClient(builder.Configuration["ServiceBusNamespace"], credential);
builder.Services.AddSingleton(sbClient);
builder.Services.AddSingleton<ServiceBusPublisher>();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("CertFlow.Api"))
    .UseAzureMonitor();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapGraphWebhookEndpoints();
app.MapAdminEndpoints();

// Support --migrate-and-seed arg for the Container Apps Job in deploy.ps1
if (args.Contains("--migrate-and-seed"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CertFlowDbContext>();
    await SeedData.ApplyAsync(db);
    return;
}

app.Run();
