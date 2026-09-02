using Azure.Identity;
using CertFlow.Application.Interfaces;
using CertFlow.Infrastructure.Persistence;
using CertFlow.Infrastructure.Repositories;
using CertFlow.McpServer.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Graph;
using OpenTelemetry.Resources;
using Azure.Monitor.OpenTelemetry.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<CertFlowDbContext>(opt =>
    opt.UseSqlServer(builder.Configuration.GetConnectionString("CertFlow")));

builder.Services.AddScoped<IAppointmentRepository, AppointmentRepository>();
builder.Services.AddScoped<ISlotRepository, SlotRepository>();

var credential = new DefaultAzureCredential();
builder.Services.AddSingleton(new GraphServiceClient(credential,
    ["https://graph.microsoft.com/.default"]));

builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("CertFlow.McpServer"))
    .UseAzureMonitor();

var app = builder.Build();
app.MapMcp();
app.Run();
