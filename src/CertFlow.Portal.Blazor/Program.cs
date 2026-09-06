using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using OpenTelemetry.Resources;
using Azure.Monitor.OpenTelemetry.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Container Apps terminates TLS at the ingress and forwards over plain HTTP:8080.
// Without this the OIDC handler builds an http:// redirect_uri, which does not match
// the https:// URI registered in Entra and fails with AADSTS50011. The ingress IP is
// not stable, so the proxy allow-lists are cleared rather than enumerated.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

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

// Interactive components run on a circuit rather than a request, so the signed-in
// principal has to be cascaded in explicitly — without this AuthorizeView renders
// its NotAuthorized branch even for a signed-in user.
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddHttpClient("CertFlowApi",
    c => c.BaseAddress = new Uri(builder.Configuration["ApiBaseUrl"]!));

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("CertFlow.Portal"))
    .UseAzureMonitor();

var app = builder.Build();

// Must run before anything that reads the request scheme or client IP.
app.UseForwardedHeaders();

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapRazorComponents<CertFlow.Portal.Blazor.Components.App>()
    .AddInteractiveServerRenderMode();
app.MapControllers();

app.Run();
