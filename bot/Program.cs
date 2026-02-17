// Copyright (c) Contoso Restaurants. All rights reserved.
// Microsoft 365 Agents SDK — ASP.NET Core host for the Contoso multi-agent bot.

using Microsoft.Agents.Authentication;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Threading;
using ContosoAgentBot;
using ContosoAgentBot.Services;

var builder = WebApplication.CreateBuilder(args);

// ── HttpClient for outbound Foundry API calls ────────────────────
builder.Services.AddHttpClient<IFoundryAgentService, FoundryAgentService>(client =>
{
    // Hosted agents may take up to 2 minutes to respond (especially on cold start)
    client.Timeout = TimeSpan.FromMinutes(2);
});

// ── Agents SDK wiring ────────────────────────────────────────────
// Reads the "AgentApplication" section from appsettings.json.
builder.AddAgentApplicationOptions();

// Register our agent.  The DI container will resolve AgentApplicationOptions
// and IFoundryAgentService into the ContosoAgent constructor.
builder.AddAgent<ContosoAgent>();

// In-memory state storage (swap for CosmosDB / Blob in production).
builder.Services.AddSingleton<IStorage, MemoryStorage>();

// ── Adaptive Cards service for rich data views (Ask 3c) ─────────
builder.Services.AddSingleton<AdaptiveCardService>();

// ── Image analysis service for attachment handling (Ask 3a) ───
builder.Services.AddHttpClient<ImageAnalysisService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

// ── Non-typed HttpClient for M365AttachmentDownloader ────────────
builder.Services.AddHttpClient();

// ── SDK file-download pipeline: M365AttachmentDownloader downloads
//    Teams attachments BEFORE route handlers fire, populating
//    turnState.Temp.InputFiles automatically ──────────────────────
builder.Services.AddSingleton<IList<IInputFileDownloader>>(sp =>
    [new M365AttachmentDownloader(
        sp.GetRequiredService<IConnections>(),
        sp.GetRequiredService<IHttpClientFactory>())]);

// ── ASP.NET authentication ──────────────────────────────────────
// The SDK's CloudAdapter handles inbound JWT validation internally
// (JwtTokenValidation.AuthenticateRequest) — no ASP.NET auth middleware needed.

// ─────────────────────────────────────────────────────────────────
WebApplication app = builder.Build();

// Health-check / landing page
app.MapGet("/", () => "Contoso Restaurants Agent Hub — Microsoft 365 Agents SDK");

// ── Incoming messages from Azure Bot Service / other SDK agents ──
app.MapPost(
    "/api/messages",
    async (HttpRequest request,
           HttpResponse response,
           IAgentHttpAdapter adapter,
           IAgent agent,
           CancellationToken cancellationToken) =>
    {
        await adapter.ProcessAsync(request, response, agent, cancellationToken);
    });

if (app.Environment.IsDevelopment())
{
    app.Urls.Add("http://localhost:3978");
}

app.Run();
