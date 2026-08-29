using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Pat.Aca.LinuxLab.Api.Hubs;
using Pat.Aca.LinuxLab.Api.Models;
using Pat.Aca.LinuxLab.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<LabSessionOptions>(builder.Configuration.GetSection("LabSession"));
builder.Services.AddSignalR();

builder.Services.AddSingleton<TokenCredential>(new DefaultAzureCredential());
builder.Services.AddSingleton(sp => new ArmClient(sp.GetRequiredService<TokenCredential>()));
builder.Services.AddSingleton<IContainerConsoleClient, ContainerConsoleClient>();

// LabSessionManager is both the session-lifecycle service and the idle-
// timeout sweep (a BackgroundService) — registered once, exposed under two
// interfaces so both callers get the same instance.
builder.Services.AddSingleton<LabSessionManager>();
builder.Services.AddSingleton<ILabSessionManager>(sp => sp.GetRequiredService<LabSessionManager>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<LabSessionManager>());

var app = builder.Build();

app.MapHub<LabHub>("/hubs/lab");

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// Called by the simulator shims running inside a session's container (see
// simulator/lib.sh's lab::progress) — never called from the browser.
app.MapPost("/internal/progress", async (
    ProgressEvent evt,
    HttpRequest request,
    ILabSessionManager sessions) =>
{
    var sessionId = request.Headers["X-Lab-Session"].ToString();
    if (string.IsNullOrWhiteSpace(sessionId))
    {
        return Results.BadRequest("missing X-Lab-Session header");
    }

    await sessions.ReportProgressAsync(sessionId, evt);
    return Results.Ok();
});

app.Run();
