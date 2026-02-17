// Copyright (c) Contoso Restaurants. All rights reserved.
// Adaptive Card builder for rich data views

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.Core.Models;

namespace ContosoAgentBot.Services;

/// <summary>
/// Builds Adaptive Cards from agent responses to show structured data
/// (KPI dashboards for Ops, creative briefs for Menu).
/// Sent as a follow-up message after the streamed text response.
/// </summary>
public class AdaptiveCardService
{
    private readonly ILogger<AdaptiveCardService> _logger;

    public AdaptiveCardService(ILogger<AdaptiveCardService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Detect whether the agent response warrants an Adaptive Card,
    /// and if so, build one. Returns null if no card is appropriate.
    /// </summary>
    public Attachment? TryBuildCard(string agentResponse, string agentType)
    {
        if (string.IsNullOrWhiteSpace(agentResponse))
            return null;

        return agentType switch
        {
            "Ops" => TryBuildOpsCard(agentResponse),
            "Menu & Marketing" => TryBuildMenuCard(agentResponse),
            _ => null
        };
    }

    // ═══════════════════════════════════════════════════════════════
    //  OPS AGENT — KPI Dashboard Card
    // ═══════════════════════════════════════════════════════════════

    private Attachment? TryBuildOpsCard(string response)
    {
        // Extract KPI-like metrics from the response
        var metrics = ExtractMetrics(response);

        if (metrics.Count < 1)
        {
            _logger.LogDebug("Ops response has no extractable metrics — skipping card");
            return null;
        }

        // Build the card JSON
        var facts = metrics.Select(m => new { title = m.Label, value = m.Value }).ToList();

        var cardJson = new
        {
            type = "AdaptiveCard",
            version = "1.5",
            body = new object[]
            {
                // Header with branding
                new
                {
                    type = "ColumnSet",
                    columns = new object[]
                    {
                        new
                        {
                            type = "Column",
                            width = "auto",
                            items = new object[]
                            {
                                new
                                {
                                    type = "TextBlock",
                                    text = "📊",
                                    size = "Large"
                                }
                            }
                        },
                        new
                        {
                            type = "Column",
                            width = "stretch",
                            items = new object[]
                            {
                                new
                                {
                                    type = "TextBlock",
                                    text = "Operations Dashboard",
                                    weight = "Bolder",
                                    size = "Medium",
                                    wrap = true
                                },
                                new
                                {
                                    type = "TextBlock",
                                    text = $"Contoso Restaurants · {DateTime.UtcNow:MMM dd, yyyy}",
                                    isSubtle = true,
                                    spacing = "None",
                                    size = "Small"
                                }
                            }
                        }
                    }
                },
                // Divider
                new
                {
                    type = "TextBlock",
                    text = " ",
                    separator = true
                },
                // Key Metrics as FactSet
                new
                {
                    type = "FactSet",
                    facts = facts
                },
                // Powered-by footer
                new
                {
                    type = "TextBlock",
                    text = "🤖 *Powered by Contoso Ops Agent*",
                    isSubtle = true,
                    size = "Small",
                    horizontalAlignment = "Right",
                    spacing = "Medium"
                }
            },
            schema = "http://adaptivecards.io/schemas/adaptive-card.json"
        };

        return CreateAttachment(cardJson);
    }

    // ═══════════════════════════════════════════════════════════════
    //  MENU AGENT — Creative Brief Card
    // ═══════════════════════════════════════════════════════════════

    private Attachment? TryBuildMenuCard(string response)
    {
        // Detect if response has a named concept (emoji + bold name pattern)
        var conceptMatch = Regex.Match(response, @"[\p{So}\p{Cs}]+\s*\*{0,2}(.+?)\*{0,2}\s*[—–-]\s*(.+?)(?:\.|$)", RegexOptions.Multiline);

        if (!conceptMatch.Success)
        {
            _logger.LogDebug("Menu response has no named concept — skipping card");
            return null;
        }

        var conceptName = conceptMatch.Groups[1].Value.Trim().Trim('*');
        var conceptPitch = conceptMatch.Groups[2].Value.Trim();

        // Detect brand
        var brand = DetectBrand(response);
        var brandEmoji = brand switch
        {
            "Contoso Burger" => "🍔",
            "Contoso Tacos" => "🌮",
            "Contoso Pizza" => "🍕",
            _ => "🍽️"
        };

        // Extract bullet points
        var bullets = Regex.Matches(response, @"[-•]\s*\*{0,2}(.+?)\*{0,2}\s*$", RegexOptions.Multiline)
            .Cast<Match>()
            .Select(m => m.Groups[1].Value.Trim())
            .Take(4)
            .ToList();

        var bodyItems = new List<object>
        {
            // Header
            new
            {
                type = "ColumnSet",
                columns = new object[]
                {
                    new
                    {
                        type = "Column",
                        width = "auto",
                        items = new object[]
                        {
                            new
                            {
                                type = "TextBlock",
                                text = brandEmoji,
                                size = "Large"
                            }
                        }
                    },
                    new
                    {
                        type = "Column",
                        width = "stretch",
                        items = new object[]
                        {
                            new
                            {
                                type = "TextBlock",
                                text = conceptName,
                                weight = "Bolder",
                                size = "Medium",
                                wrap = true,
                                color = "Accent"
                            },
                            new
                            {
                                type = "TextBlock",
                                text = brand ?? "Contoso Restaurants",
                                isSubtle = true,
                                spacing = "None",
                                size = "Small"
                            }
                        }
                    }
                }
            },
            // Divider
            new
            {
                type = "TextBlock",
                text = " ",
                separator = true
            },
            // Concept pitch
            new
            {
                type = "TextBlock",
                text = conceptPitch,
                wrap = true,
                weight = "Bolder"
            }
        };

        // Add bullet points
        foreach (var bullet in bullets)
        {
            bodyItems.Add(new
            {
                type = "TextBlock",
                text = $"✅ {bullet}",
                wrap = true,
                spacing = "Small"
            });
        }

        // Footer
        bodyItems.Add(new
        {
            type = "TextBlock",
            text = "🤖 *Powered by Contoso Menu & Marketing Agent*",
            isSubtle = true,
            size = "Small",
            horizontalAlignment = "Right",
            spacing = "Medium"
        });

        var cardJson = new
        {
            type = "AdaptiveCard",
            version = "1.5",
            body = bodyItems,
            schema = "http://adaptivecards.io/schemas/adaptive-card.json"
        };

        return CreateAttachment(cardJson);
    }

    // ═══════════════════════════════════════════════════════════════
    //  SHARED HELPERS
    // ═══════════════════════════════════════════════════════════════

    private static Attachment CreateAttachment(object cardJson)
    {
        var json = JsonSerializer.Serialize(cardJson, new JsonSerializerOptions { WriteIndented = false });

        return new Attachment
        {
            ContentType = "application/vnd.microsoft.card.adaptive",
            Content = JsonSerializer.Deserialize<JsonElement>(json)
        };
    }

    /// <summary>
    /// Extract metric-like key-value pairs from ops agent text output.
    /// Looks for patterns like "**Label**: Value", "Label: Value", "Label — Value".
    /// </summary>
    private static List<(string Label, string Value)> ExtractMetrics(string text)
    {
        var metrics = new List<(string Label, string Value)>();

        // Pattern: **Label**: Value  or  **Label** — Value  or  Label: Value
        var patterns = new[]
        {
            @"\*{2}(.+?)\*{2}\s*[:—–-]\s*(.+?)(?:\n|$)",   // **bold label**: value
            @"^[-•]\s*(.+?)\s*[:—–]\s*(.+?)(?:\n|$)",       // - label: value (bullet list)
            @"^[-•]\s*\*{0,2}(.+?)\*{0,2}\s*[:—–-]\s*(.+?)$", // - label: value (relaxed)
            @"(?:^|\n)(.{3,35}):\s+(\$?[\d,.]+[%]?\s*\w{0,20})(?:\n|$)", // Label: $1,234 or Label: 4.2 minutes
        };

        foreach (var pattern in patterns)
        {
            foreach (Match match in Regex.Matches(text, pattern, RegexOptions.Multiline))
            {
                var label = match.Groups[1].Value.Trim().Trim('*');
                var value = match.Groups[2].Value.Trim();

                // Skip very long values (likely sentences, not metrics)
                if (value.Length <= 120 && label.Length <= 50)
                {
                    metrics.Add((label, value));
                }
            }
        }

        return metrics.DistinctBy(m => m.Label).ToList();
    }

    private static string? DetectBrand(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("burger") || lower.Contains("grill") || lower.Contains("flame"))
            return "Contoso Burger";
        if (lower.Contains("taco") || lower.Contains("mexican") || lower.Contains("burrito"))
            return "Contoso Tacos";
        if (lower.Contains("pizza") || lower.Contains("slice") || lower.Contains("pepperoni"))
            return "Contoso Pizza";
        return null;
    }
}
