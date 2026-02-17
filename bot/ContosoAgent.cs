// Copyright (c) Contoso Restaurants. All rights reserved.
// Microsoft 365 Agents SDK - Multi-Agent Streaming Bot

using Microsoft.Agents.Authentication;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ContosoAgentBot.Services;

namespace ContosoAgentBot;

/// <summary>
/// Contoso Restaurants multi-agent bot using the Microsoft 365 Agents SDK.
/// Routes user messages to Foundry Hosted Agents (Ops / Menu) via SSE streaming,
/// and feeds each chunk back to Teams through the SDK's built-in streaming support.
/// </summary>
public class ContosoAgent : AgentApplication
{
    private readonly IFoundryAgentService _foundryService;
    private readonly AdaptiveCardService _cardService;
    private readonly ImageAnalysisService _imageService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConnections _connections;
    private readonly ILogger<ContosoAgent> _logger;

    // Diagnostic mode — set to true to embed debug info in bot responses
    private const bool DiagnosticMode = false;

    // ── Per-message context for reaction follow-ups ──────────────────
    private static readonly ConcurrentDictionary<string, (string Query, string Agent)> _messageContext = new();

    // ── Prefix commands ──────────────────────────────────────────────
    private const string OpsPrefix   = "/ops";
    private const string MenuPrefix  = "/menu";
    private const string HelpPrefix  = "/help";

    public ContosoAgent(
        AgentApplicationOptions options,
        IFoundryAgentService foundryService,
        AdaptiveCardService cardService,
        ImageAnalysisService imageService,
        IHttpClientFactory httpClientFactory,
        IConnections connections,
        ILogger<ContosoAgent> logger) : base(options)
    {
        _foundryService = foundryService;
        _cardService = cardService;
        _imageService = imageService;
        _httpClientFactory = httpClientFactory;
        _connections = connections;
        _logger = logger;

        // ── Register activity handlers ───────────────────────────
        OnConversationUpdate(
            ConversationUpdateEvents.MembersAdded,
            OnMembersAddedAsync);

        OnActivity(
            ActivityTypes.MessageReaction,
            OnReactionsAddedAsync);

        // Message handler registered last so more-specific routes win
        OnActivity(
            ActivityTypes.Message,
            OnMessageAsync,
            rank: RouteRank.Last);
    }

    // ══════════════════════════════════════════════════════════════════
    //  WELCOME
    // ══════════════════════════════════════════════════════════════════

    private async Task OnMembersAddedAsync(
        ITurnContext turnContext,
        ITurnState turnState,
        CancellationToken cancellationToken)
    {
        foreach (ChannelAccount member in turnContext.Activity.MembersAdded)
        {
            if (member.Id == turnContext.Activity.Recipient.Id)
                continue;

            var welcome = """
                👋 **Welcome to the Contoso Restaurants Agent Hub!**

                Just type your question and I'll automatically route it to the right agent:

                🔧 **Ops Agent** — store operations, KPIs, sales data, food safety
                🍔 **Menu & Marketing Agent** — campaigns, menu innovation, creative content

                You can also use direct commands:
                  → `/ops <question>` — go directly to the Ops Agent
                  → `/menu <question>` — go directly to the Menu & Marketing Agent
                  → `/help` — show this message again

                💡 Or just ask anything — our orchestrator will figure out the rest!
                """;

            await turnContext.SendActivityAsync(
                MessageFactory.Text(welcome),
                cancellationToken);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  MESSAGE  (prefix routing → Foundry SSE → Teams streaming)
    // ══════════════════════════════════════════════════════════════════

    private async Task OnMessageAsync(
        ITurnContext turnContext,
        ITurnState turnState,
        CancellationToken cancellationToken)
    {
        var rawText = turnContext.Activity.Text?.Trim() ?? string.Empty;
        var diag = new StringBuilder(); // diagnostic log visible in Teams

        // ════════════════════════════════════════════════════════════
        //  IMAGE HANDLING — four-layer approach with diagnostics:
        //    1. SDK pipeline (M365AttachmentDownloader → InputFiles)
        //    2. Fallback: authenticated download from Activity.Attachments
        //    3. Fallback: anonymous download (for pre-signed URLs)
        //    4. If all fail, proceed with text only
        // ════════════════════════════════════════════════════════════

        diag.AppendLine($"Text='{rawText}', Channel={turnContext.Activity.ChannelId}");

        // ── Layer 1: Check SDK-downloaded InputFiles ────────────
        var inputFiles = turnState.Temp.InputFiles;
        var imageFiles = inputFiles
            .Where(f => f.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        diag.AppendLine($"SDK InputFiles={inputFiles.Count}, ImageFiles={imageFiles.Count}");

        // Log raw Activity.Attachments for diagnostics
        var rawAttachments = turnContext.Activity.Attachments;
        diag.AppendLine($"Activity.Attachments={rawAttachments?.Count ?? 0}");
        if (rawAttachments != null)
        {
            for (int i = 0; i < rawAttachments.Count; i++)
            {
                var att = rawAttachments[i];
                var urlSnippet = att.ContentUrl != null
                    ? att.ContentUrl.Substring(0, Math.Min(att.ContentUrl.Length, 80))
                    : "(null)";
                diag.AppendLine($"  [{i}] Type={att.ContentType ?? "(null)"}, Name={att.Name ?? "(null)"}, Url={urlSnippet}, HasContent={att.Content != null}");
            }
        }

        // ── Layer 2: Fallback authenticated download if SDK returned nothing ──
        if (imageFiles.Count == 0 && rawAttachments != null && rawAttachments.Count > 0)
        {
            diag.AppendLine("SDK empty — trying fallback download...");
            imageFiles = await FallbackDownloadImagesAsync(rawAttachments, turnContext, diag, cancellationToken);
            diag.AppendLine($"Fallback result: {imageFiles.Count} image(s)");
        }

        _logger.LogCritical(
            "[ContosoAgent] Text len={TextLen}, InputFiles={InCount}, ImageFiles={ImgCount}, Attachments={AttCount}",
            rawText.Length, inputFiles.Count, imageFiles.Count, rawAttachments?.Count ?? 0);

        // ── Analyze images if any were found ────────────────────
        if (imageFiles.Count > 0)
        {
            try
            {
                diag.AppendLine($"[vision] Analyzing {imageFiles.Count} image(s) with vision model...");
                var imageDescription = await _imageService.AnalyzeDownloadedImagesAsync(
                    imageFiles,
                    rawText,
                    cancellationToken,
                    diag);

                diag.AppendLine($"[vision] Result: {(imageDescription != null ? $"{imageDescription.Length} chars" : "null")}");

                if (!string.IsNullOrEmpty(imageDescription))
                {
                    rawText = string.IsNullOrEmpty(rawText)
                        ? $"[Image uploaded by user — analysis: {imageDescription}]"
                        : $"{rawText}\n\n[Attached image analysis: {imageDescription}]";
                }
            }
            catch (Exception imgEx)
            {
                diag.AppendLine($"[vision] EXCEPTION: {imgEx.Message}");
                _logger.LogError(imgEx, "Image analysis failed");
            }
        }

        // Send diagnostic info as a separate message BEFORE routing
        if (DiagnosticMode && rawAttachments != null && rawAttachments.Count > 0)
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text($"```\n{diag}\n```"),
                cancellationToken);
        }

        if (string.IsNullOrEmpty(rawText))
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("I didn't catch that — try `/help` for usage info."),
                cancellationToken);
            return;
        }

        // ── Determine route ──────────────────────────────────────
        var (agentType, userMessage) = ParseCommand(rawText);

        switch (agentType)
        {
            case AgentType.Help:
                await SendHelpCardAsync(turnContext, cancellationToken);
                return;

            case AgentType.Ops:
                await StreamFoundryResponseAsync(
                    turnContext, userMessage, AgentType.Ops, cancellationToken);
                return;

            case AgentType.Menu:
                await StreamFoundryResponseAsync(
                    turnContext, userMessage, AgentType.Menu, cancellationToken);
                return;

            case AgentType.Orchestrator:
            default:
                await StreamFoundryResponseAsync(
                    turnContext, userMessage, AgentType.Orchestrator, cancellationToken);
                return;
        }
    }

    /// <summary>
    /// Fallback: when the SDK's M365AttachmentDownloader returns nothing,
    /// manually download images from Activity.Attachments using proper bot auth.
    /// 
    /// Teams inline images (pasted/dropped) use ContentType=image/* with
    /// ContentUrl at us-api.asm.skype.com — these REQUIRE Bearer token auth.
    /// 
    /// Teams file attachments use ContentType=application/vnd.microsoft.teams.file.download.info
    /// with a pre-signed downloadUrl in Content — these work WITHOUT auth.
    /// </summary>
    private async Task<List<InputFile>> FallbackDownloadImagesAsync(
        IList<Attachment> attachments,
        ITurnContext turnContext,
        StringBuilder diag,
        CancellationToken cancellationToken)
    {
        var results = new List<InputFile>();

        // Get bot token for authenticated downloads (same token M365AttachmentDownloader would use)
        string? botToken = null;
        try
        {
            var tokenProvider = _connections.GetTokenProvider(turnContext.Identity, turnContext.Activity);
            botToken = await tokenProvider.GetAccessTokenAsync(
                AgentClaims.GetTokenAudience(turnContext.Identity),
                scopes: null).ConfigureAwait(false);
            diag.AppendLine($"Bot token acquired: {(botToken?.Length > 20 ? botToken[..20] + "..." : "SHORT/EMPTY")}");
        }
        catch (Exception tokenEx)
        {
            diag.AppendLine($"Bot token FAILED: {tokenEx.Message}");
            _logger.LogError(tokenEx, "Failed to acquire bot token for fallback image download");
        }

        foreach (var attachment in attachments)
        {
            try
            {
                var ct = attachment.ContentType ?? "(null)";

                // Skip HTML/adaptive card attachments
                if (ct.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ||
                    ct.Contains("adaptive", StringComparison.OrdinalIgnoreCase))
                {
                    diag.AppendLine($"Skip: {ct}");
                    continue;
                }

                string? downloadUrl = null;
                bool needsAuth = true;

                // ── Inline image: ContentType=image/*, use ContentUrl with Bearer auth ──
                if (ct.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = attachment.ContentUrl;
                    needsAuth = true;
                    diag.AppendLine($"Inline image: {ct}, needsAuth=true");
                }
                // ── File attachment: ContentType=application/vnd.microsoft.teams.file.download.info ──
                else if (ct.Contains("download", StringComparison.OrdinalIgnoreCase) ||
                         ct.Contains("vnd.microsoft.teams", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract pre-signed downloadUrl from Content JSON
                    if (attachment.Content != null)
                    {
                        try
                        {
                            var contentJson = JsonSerializer.Serialize(attachment.Content);
                            using var doc = JsonDocument.Parse(contentJson);
                            if (doc.RootElement.TryGetProperty("downloadUrl", out var urlProp))
                            {
                                downloadUrl = urlProp.GetString();
                                needsAuth = false; // pre-signed URL
                            }
                        }
                        catch { /* Content isn't parseable JSON */ }
                    }

                    // Fallback to ContentUrl
                    if (string.IsNullOrEmpty(downloadUrl))
                    {
                        downloadUrl = attachment.ContentUrl;
                        needsAuth = true;
                    }
                    diag.AppendLine($"File attachment: needsAuth={needsAuth}");
                }
                else
                {
                    diag.AppendLine($"Unknown type: {ct} — skipping");
                    continue;
                }

                if (string.IsNullOrEmpty(downloadUrl) || !downloadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    diag.AppendLine($"No valid URL for: {attachment.Name ?? ct}");
                    continue;
                }

                diag.AppendLine($"Downloading: {downloadUrl[..Math.Min(downloadUrl.Length, 80)]}...");

                // ── Download with appropriate auth ──
                using var httpClient = _httpClientFactory.CreateClient("FallbackDownloader");
                httpClient.Timeout = TimeSpan.FromSeconds(15);

                using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
                if (needsAuth && !string.IsNullOrEmpty(botToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);
                    diag.AppendLine("Using Bearer auth");
                }
                else if (needsAuth && string.IsNullOrEmpty(botToken))
                {
                    diag.AppendLine("WARNING: needs auth but no token — trying anonymous");
                }

                using var response = await httpClient.SendAsync(request, cancellationToken);
                diag.AppendLine($"Response: {(int)response.StatusCode} {response.StatusCode}");

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    diag.AppendLine($"FAILED: {errorBody[..Math.Min(errorBody.Length, 200)]}");

                    // If authenticated request failed, try anonymous (some URLs are pre-signed)
                    if (needsAuth)
                    {
                        diag.AppendLine("Retrying anonymous...");
                        using var anonRequest = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
                        using var anonResponse = await httpClient.SendAsync(anonRequest, cancellationToken);
                        diag.AppendLine($"Anon response: {(int)anonResponse.StatusCode}");

                        if (anonResponse.IsSuccessStatusCode)
                        {
                            var anonBytes = await anonResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                            var anonCt = anonResponse.Content.Headers.ContentType?.MediaType ?? "image/png";
                            diag.AppendLine($"Anon download OK: {anonBytes.Length} bytes, {anonCt}");
                            results.Add(new InputFile(new BinaryData(anonBytes), anonCt)
                            {
                                ContentUrl = downloadUrl,
                                Filename = attachment.Name ?? "image.png"
                            });
                        }
                    }
                    continue;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                var contentType = response.Content.Headers.ContentType?.MediaType ?? ct;

                // Normalize content type
                if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    if (ct.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                        contentType = ct;
                    else
                        contentType = "image/png";
                }

                diag.AppendLine($"Downloaded: {bytes.Length} bytes, type={contentType}");

                results.Add(new InputFile(new BinaryData(bytes), contentType)
                {
                    ContentUrl = downloadUrl,
                    Filename = attachment.Name ?? "image.png"
                });
            }
            catch (Exception ex)
            {
                diag.AppendLine($"ERROR: {ex.Message}");
                _logger.LogError(ex, "Fallback download error for {Name}", attachment.Name);
            }
        }

        return results;
    }

    /// <summary>
    /// Streams a Foundry Hosted Agent response back to Teams using the SDK's
    /// built-in streaming: QueueInformativeUpdateAsync → QueueTextChunk → EndStreamAsync.
    /// </summary>
    private async Task StreamFoundryResponseAsync(
        ITurnContext turnContext,
        string userMessage,
        AgentType agentType,
        CancellationToken cancellationToken)
    {
        var conversationId = turnContext.Activity.Conversation.Id;
        var agentLabel = agentType switch
        {
            AgentType.Ops => "Ops",
            AgentType.Menu => "Menu & Marketing",
            AgentType.Orchestrator => "Orchestrator",
            _ => "Orchestrator"
        };
        var responseBuilder = new StringBuilder();

        try
        {
            // ── Informative "thinking" indicator ─────────────────
            await turnContext.StreamingResponse.QueueInformativeUpdateAsync(
                $"Contacting the {agentLabel} Agent…",
                cancellationToken);

            // ── Pick the correct streaming method ────────────────
            IAsyncEnumerable<string> chunks = agentType switch
            {
                AgentType.Ops => _foundryService.StreamOpsAgentAsync(userMessage, conversationId, cancellationToken),
                AgentType.Menu => _foundryService.StreamMenuAgentAsync(userMessage, conversationId, cancellationToken),
                _ => _foundryService.StreamOrchestratorAgentAsync(userMessage, conversationId, cancellationToken)
            };

            var totalLength = 0;

            await foreach (var chunk in chunks.WithCancellation(cancellationToken))
            {
                if (!string.IsNullOrEmpty(chunk))
                {
                    turnContext.StreamingResponse.QueueTextChunk(chunk);
                    responseBuilder.Append(chunk);
                    totalLength += chunk.Length;
                }
            }

            // If nothing came back, send a fallback message
            if (totalLength == 0)
            {
                turnContext.StreamingResponse.QueueTextChunk(
                    $"The {agentLabel} Agent didn't return a response. Please try again.");
            }

            _logger.LogInformation(
                "Streamed {Length} chars from {Agent} agent for conversation {ConvId}",
                totalLength, agentLabel, conversationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error streaming from {Agent} agent for conversation {ConvId}",
                agentLabel, conversationId);

            turnContext.StreamingResponse.QueueTextChunk(
                $"⚠️ Sorry, the {agentLabel} Agent encountered an error: {ex.Message}");
        }
        finally
        {
            // ── Always close the stream ──────────────────────────
            await turnContext.StreamingResponse.EndStreamAsync(cancellationToken);

            // ── Store context for reaction-based follow-ups ──────
            // Primary: try the final streamed activity ID (matches ReplyToId on reactions)
            // Fallback: conversationId (works for sequential single-threaded demos)
            var activityId = turnContext.StreamingResponse.FinalMessage?.Id;
            if (!string.IsNullOrEmpty(activityId))
                _messageContext[activityId] = (userMessage, agentLabel);
            // Always store latest per-conversation for fallback lookup
            _messageContext[conversationId] = (userMessage, agentLabel);

            // ── Send Adaptive Card as follow-up if metrics/concept detected ──
            try
            {
                var fullResponse = responseBuilder.ToString();
                var card = _cardService.TryBuildCard(fullResponse, agentLabel);
                if (card != null)
                {
                    var cardActivity = MessageFactory.Attachment(card);
                    await turnContext.SendActivityAsync(cardActivity, cancellationToken);
                    _logger.LogInformation("Sent Adaptive Card for {Agent} response", agentLabel);
                }
            }
            catch (Exception cardEx)
            {
                _logger.LogWarning(cardEx, "Failed to send Adaptive Card — text response was still delivered");
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  REACTIONS  (👍 / 👎)
    // ══════════════════════════════════════════════════════════════════

    private async Task OnReactionsAddedAsync(
        ITurnContext turnContext,
        ITurnState turnState,
        CancellationToken cancellationToken)
    {
        var reactions = turnContext.Activity.ReactionsAdded;
        if (reactions is null || reactions.Count == 0)
            return;

        // ── Look up original query context ────────────────────
        var replyToId = turnContext.Activity.ReplyToId;
        var convId = turnContext.Activity.Conversation.Id;
        _messageContext.TryGetValue(replyToId ?? string.Empty, out var ctx);
        if (ctx == default)
            _messageContext.TryGetValue(convId, out ctx);

        var hasContext = ctx != default;
        var querySnippet = hasContext && ctx.Query.Length > 60
            ? ctx.Query[..57] + "…"
            : ctx.Query;

        foreach (var reaction in reactions)
        {
            string reply = reaction.Type switch
            {
                "like" or "heart" when hasContext =>
                    $"👍 Thanks! Glad the {ctx.Agent} Agent answer about '{querySnippet}' was helpful!",
                "like"      => "👍 Thanks for the positive feedback! Glad I could help.",
                "heart"     => "❤️ Appreciate the love! Let me know if you need anything else.",
                "laugh"     => "😄 Happy to entertain! Anything else I can help with?",
                "surprised" => "😮 Hope that was a good surprise!",
                "sad" or "angry" when hasContext =>
                    "😢 Sorry that wasn't useful. Try rephrasing your question or type /help for guidance.",
                "sad"       => "😢 Sorry to hear that. Want me to try a different approach? Just ask again.",
                "angry"     => "😠 I'm sorry the response wasn't helpful. Please rephrase your question and I'll do better.",
                _ when hasContext =>
                    $"Thanks for your reaction ({reaction.Type}) on the {ctx.Agent} Agent response!",
                _           => $"Thanks for your reaction ({reaction.Type})!"
            };

            _logger.LogInformation(
                "Reaction '{Reaction}' received on activity {ActivityId} (context: {HasContext})",
                reaction.Type,
                replyToId,
                hasContext);

            await turnContext.SendActivityAsync(
                MessageFactory.Text(reply),
                cancellationToken);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  HELPERS
    // ══════════════════════════════════════════════════════════════════

    private enum AgentType { Ops, Menu, Orchestrator, Help, Unknown }

    /// <summary>
    /// Parses the user text for a prefix command.  Falls back to orchestrator routing.
    /// </summary>
    private (AgentType type, string message) ParseCommand(string text)
    {
        if (text.StartsWith(OpsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var msg = text[OpsPrefix.Length..].Trim();
            return (AgentType.Ops, string.IsNullOrEmpty(msg) ? "What can I help you with regarding store operations?" : msg);
        }

        if (text.StartsWith(MenuPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var msg = text[MenuPrefix.Length..].Trim();
            return (AgentType.Menu, string.IsNullOrEmpty(msg) ? "What can I help you with regarding menus and marketing?" : msg);
        }

        if (text.StartsWith(HelpPrefix, StringComparison.OrdinalIgnoreCase))
            return (AgentType.Help, string.Empty);

        // No prefix — route to the orchestrator for LLM-based intent classification
        return (AgentType.Orchestrator, text);
    }

    private static async Task SendHelpCardAsync(
        ITurnContext turnContext,
        CancellationToken cancellationToken)
    {
        var help = """
            **Contoso Restaurants Agent Hub — Commands**

            💡 **Just type your question** — the orchestrator agent will automatically route it to the right specialist.

            Or use direct commands to bypass the orchestrator:
            - `/ops <question>` — Ask the **Ops Agent** about store performance, KPIs, food safety, etc.
            - `/menu <question>` — Ask the **Menu & Marketing Agent** about campaigns, menu ideas, creative content.
            - `/help` — Show this help message.
            """;

        await turnContext.SendActivityAsync(
            MessageFactory.Text(help),
            cancellationToken);
    }
}
