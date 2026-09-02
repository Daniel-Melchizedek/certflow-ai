using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using OpenTelemetry.Resources;
using Azure.Monitor.OpenTelemetry.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ExamOps", p => p.RequireRole("ExamOps"));
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddControllersWithViews()
    .AddMicrosoftIdentityUI();

builder.Services.AddHttpClient("CertFlowApi",
    c => c.BaseAddress = new Uri(builder.Configuration["ApiBaseUrl"]!));

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("CertFlow.Portal"))
    .UseAzureMonitor();

var app = builder.Build();

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapRazorComponents<CertFlow.Portal.Blazor.Components.App>()
    .AddInteractiveServerRenderMode();
app.MapControllers();

app.Run();
