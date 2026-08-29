using System.Threading.RateLimiting;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Pat.Aca.LinuxLab.Api.Hubs;
using Pat.Aca.LinuxLab.Api.Models;
using Pat.Aca.LinuxLab.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<LabSessionOptions>(builder.Configuration.GetSection("LabSession"));
builder.Services.Configure<CloudflareAccessOptions>(builder.Configuration.GetSection("CloudflareAccess"));
builder.Services.AddSignalR();

// Verifies Cf-Access-Jwt-Assertion cryptographically against Cloudflare's
// own JWKS, rather than trusting the header's contents as plain text (the
// gap the scaffold's first pass left as a TODO). There's deliberately no
// separate API key/shared secret in front of the hub — see LabHub's remarks
// for why that wouldn't reach this connection anyway.
var cfAccess = builder.Configuration.GetSection("CloudflareAccess").Get<CloudflareAccessOptions>() ?? new();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Cloudflare's certs endpoint is a bare JWKS document, not a
        // standard OIDC discovery document — Authority/MetadataAddress's
        // built-in retriever can't parse either the issuer or the signing
        // keys from it correctly (confirmed via two real failures in a
        // row: an invalid-issuer 401, then a no-signing-keys-found 401).
        // Skip that mechanism entirely: validate issuer/audience
        // explicitly, and fetch signing keys directly via
        // CloudflareJwksProvider instead of relying on any built-in
        // discovery.
        // Without this, short JWT claim names (like "email") can get
        // silently remapped to long legacy XML-namespace claim URIs on the
        // way in — meaning the token really does carry "email", but
        // Context.User ends up with it under a completely different claim
        // type, and FindFirst("email") comes back null despite that.
        options.MapInboundClaims = false;

        var certsUrl = $"{cfAccess.TeamDomain}/cdn-cgi/access/certs";
        var jwksProvider = new CloudflareJwksProvider(new HttpClient());
        options.TokenValidationParameters.ValidIssuer = cfAccess.TeamDomain;
        options.TokenValidationParameters.ValidAudience = cfAccess.Audience;
        options.TokenValidationParameters.IssuerSigningKeyResolver = (_, _, _, _) =>
            jwksProvider.GetSigningKeys(certsUrl);
        options.Events = new JwtBearerEvents
        {
            // Cloudflare puts the token in a custom header, not the standard
            // Authorization: Bearer header — pull it from there instead.
            OnMessageReceived = context =>
            {
                var token = context.Request.Headers["Cf-Access-Jwt-Assertion"].ToString();
                if (!string.IsNullOrEmpty(token))
                {
                    context.Token = token;
                }
                return Task.CompletedTask;
            },
            // Diagnostic logging — appsettings.json's Microsoft.AspNetCore:
            // Warning suppresses the framework's own detailed failure
            // reasons by default. This category isn't under that prefix, so
            // it logs at the Default level regardless. Safe to leave in
            // permanently; it's a handful of log lines only on the failure
            // path, not per-request noise on success.
            OnAuthenticationFailed = context =>
            {
                context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("CloudflareAccessAuth")
                    .LogWarning(context.Exception, "JWT validation failed");
                return Task.CompletedTask;
            },
            OnChallenge = context =>
            {
                context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("CloudflareAccessAuth")
                    .LogWarning("JWT challenge issued: {Error} {ErrorDescription}", context.Error, context.ErrorDescription);
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization();

// lab.koorevaar.com and api.lab.koorevaar.com are different origins (a
// subdomain difference still counts) — the browser's SignalR client calls
// this hub cross-origin (negotiate is a plain fetch), and needs explicit
// CORS headers. AllowCredentials means the origin can't be "*" — it must
// be the exact configured origin.
var labSession = builder.Configuration.GetSection("LabSession").Get<LabSessionOptions>() ?? new();
builder.Services.AddCors(options =>
{
    options.AddPolicy("LabFrontend", policy =>
    {
        policy.WithOrigins(labSession.AllowedOrigin)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// /internal/progress is only ever called by the simulator shims running
// inside a session's own container (see simulator/lib.sh's lab::progress),
// but it's still an unauthenticated endpoint on this API's surface, so it
// gets a light rate limit as basic hygiene — mirrors the fixed-window
// pattern used in Pat.Aca.BlogServiceApi's ApiSecurity.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("progress", limiterOptions =>
    {
        limiterOptions.PermitLimit = 60;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueLimit = 0;
    });
});

builder.Services.AddSingleton<TokenCredential>(new DefaultAzureCredential());
builder.Services.AddSingleton(sp => new ArmClient(sp.GetRequiredService<TokenCredential>()));
builder.Services.AddSingleton<IContainerConsoleClient, ContainerConsoleClient>();

// LabSessionManager is both the session-lifecycle service and the idle-
// timeout/max-duration sweep (a BackgroundService) — registered once,
// exposed under two interfaces so both callers get the same instance.
builder.Services.AddSingleton<LabSessionManager>();
builder.Services.AddSingleton<ILabSessionManager>(sp => sp.GetRequiredService<LabSessionManager>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<LabSessionManager>());

var app = builder.Build();

app.UseCors("LabFrontend");
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHub<LabHub>("/hubs/lab").RequireAuthorization().RequireCors("LabFrontend");

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
}).RequireRateLimiting("progress");

app.Run();
