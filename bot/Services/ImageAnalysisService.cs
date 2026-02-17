// Copyright (c) Contoso Restaurants. All rights reserved.
// Image analysis service for Teams attachment handling
// Uses M365AttachmentDownloader SDK pipeline — files are pre-downloaded into InputFiles.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.Builder.App;

namespace ContosoAgentBot.Services;

/// <summary>
/// Analyzes pre-downloaded image files using GPT vision.
/// The SDK's M365AttachmentDownloader pipeline downloads Teams attachments
/// and populates turnState.Temp.InputFiles BEFORE route handlers fire.
/// This service only needs to call the vision model with the already-downloaded bytes.
/// </summary>
public class ImageAnalysisService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ImageAnalysisService> _logger;
    private readonly DefaultAzureCredential _credential;

    public ImageAnalysisService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<ImageAnalysisService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
        _credential = new DefaultAzureCredential();
    }

    /// <summary>
    /// Analyze pre-downloaded image files from the SDK pipeline (turnState.Temp.InputFiles).
    /// Each InputFile has Content (BinaryData) and ContentType already populated.
    /// </summary>
    public async Task<string?> AnalyzeDownloadedImagesAsync(
        List<InputFile> imageFiles,
        string userText,
        CancellationToken cancellationToken,
        StringBuilder? diag = null)
    {
        if (imageFiles.Count == 0)
            return null;

        diag?.AppendLine($"[vision] Analyzing {imageFiles.Count} image file(s)");
        _logger.LogInformation("Analyzing {Count} pre-downloaded image file(s)", imageFiles.Count);

        var imageContents = new List<object>();

        foreach (var file in imageFiles)
        {
            try
            {
                var imageBytes = file.Content.ToArray();
                _logger.LogDebug("Processing file: {Filename}, ContentType={ContentType}, Bytes={Length}", file.Filename, file.ContentType, imageBytes.Length);
                diag?.AppendLine($"[vision] File: {file.Filename}, ContentType={file.ContentType}, Bytes={imageBytes.Length}");
                if (imageBytes.Length == 0)
                {
                    _logger.LogWarning("Empty image content for file: {Name}", file.Filename);
                    continue;
                }

                var mimeType = file.ContentType ?? "image/png";
                var base64 = Convert.ToBase64String(imageBytes);
                var dataUri = $"data:{mimeType};base64,{base64}";

                // Responses API format: input_image with flat image_url string
                imageContents.Add(new
                {
                    type = "input_image",
                    image_url = dataUri
                });

                _logger.LogDebug("Prepared image: {Filename} ({Length} bytes, {MimeType})", file.Filename, imageBytes.Length, mimeType);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing downloaded image {Name}", file.Filename);
            }
        }

        if (imageContents.Count == 0)
        {
            _logger.LogWarning("No valid images to analyze after processing");
            diag?.AppendLine("[vision] No valid images after processing — all empty/skipped");
            return null;
        }

        _logger.LogInformation("Calling vision model with {Count} image(s)", imageContents.Count);
        diag?.AppendLine($"[vision] Calling vision model with {imageContents.Count} image(s)…");
        return await CallVisionModelAsync(imageContents, userText, cancellationToken, diag);
    }

    /// <summary>
    /// Call the vision model via the Responses API (same endpoint pattern as FoundryAgentService).
    /// The Foundry project endpoint does NOT support the classic Azure OpenAI
    /// /openai/deployments/{deployment}/chat/completions path — it only supports
    /// the Responses API at /openai/responses with a "model" parameter.
    /// </summary>
    private async Task<string?> CallVisionModelAsync(
        List<object> imageContents,
        string userText,
        CancellationToken cancellationToken,
        StringBuilder? diag = null)
    {
        try
        {
            var endpoint = _configuration["Foundry:ProjectEndpoint"]
                ?? throw new InvalidOperationException("Foundry:ProjectEndpoint configuration is required. Set it in appsettings.json or environment variables.");

            var visionDeployment = _configuration["Foundry:VisionDeployment"] ?? "gpt-4o-mini";

            // Use the Responses API — same pattern as FoundryAgentService
            var apiUrl = $"{endpoint}/openai/responses?api-version=2025-11-15-preview";

            _logger.LogDebug("Vision API URL: {Url}", apiUrl);
            diag?.AppendLine($"[vision] Endpoint={endpoint}");
            diag?.AppendLine($"[vision] Model={visionDeployment}");
            diag?.AppendLine($"[vision] API URL={apiUrl}");

            // Build multimodal input using Responses API format:
            //   input_text for text, input_image for image data URIs
            //   imageContents already built in correct input_image format
            var inputParts = new List<object>
            {
                new
                {
                    type = "input_text",
                    text = $"Describe this image in the context of Contoso Restaurants operations. The user said: \"{userText}\". Provide a concise factual description (2-3 sentences) that can be used as context for answering their question."
                }
            };
            inputParts.AddRange(imageContents);

            var requestBody = new
            {
                model = visionDeployment,
                input = new[]
                {
                    new
                    {
                        role = "user",
                        content = inputParts
                    }
                },
                max_output_tokens = 300
                // Note: temperature omitted for vision requests
            };

            var tokenScope = "https://ai.azure.com/.default";
            diag?.AppendLine($"[vision] Acquiring token scope={tokenScope}...");
            AccessToken token;
            try
            {
                var tokenContext = new TokenRequestContext(new[] { tokenScope });
                token = await _credential.GetTokenAsync(tokenContext, cancellationToken);
                _logger.LogDebug("Azure token acquired for vision model");
                diag?.AppendLine($"[vision] Token OK: {token.Token[..20]}...");
            }
            catch (Exception credEx)
            {
                diag?.AppendLine($"[vision] TOKEN FAILED: {credEx.GetType().Name}: {credEx.Message}");
                _logger.LogCritical(credEx, "DefaultAzureCredential failed for AI Foundry");
                return null;
            }

            var serialized = JsonSerializer.Serialize(requestBody);
            diag?.AppendLine($"[vision] Request body length: {serialized.Length} chars");

            var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
            {
                Content = new StringContent(serialized, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            diag?.AppendLine("[vision] Sending request to Responses API...");
            var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            _logger.LogDebug("Vision API response: {StatusCode}, body length={Length}", (int)response.StatusCode, responseContent.Length);
            diag?.AppendLine($"[vision] Response: {(int)response.StatusCode} {response.StatusCode}, body={responseContent.Length} chars");

            if (!response.IsSuccessStatusCode)
            {
                var errorSnippet = responseContent.Length > 500 ? responseContent[..500] : responseContent;
                diag?.AppendLine($"[vision] ERROR BODY: {errorSnippet}");
                _logger.LogWarning("Vision API returned {Status}: {Content}",
                    response.StatusCode, responseContent);
                return null;
            }

            // Parse the Responses API response
            // Format: { "output": [ { "type": "message", "content": [ { "type": "output_text", "text": "..." } ] } ] }
            using var doc = JsonDocument.Parse(responseContent);
            var root = doc.RootElement;

            // Try Responses API format first
            if (root.TryGetProperty("output", out var output) && output.GetArrayLength() > 0)
            {
                foreach (var item in output.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var itemType) &&
                        itemType.GetString() == "message" &&
                        item.TryGetProperty("content", out var content))
                    {
                        foreach (var part in content.EnumerateArray())
                        {
                            if (part.TryGetProperty("type", out var partType) &&
                                partType.GetString() == "output_text" &&
                                part.TryGetProperty("text", out var textVal))
                            {
                                var result = textVal.GetString();
                                diag?.AppendLine($"[vision] Result: {result?.Length ?? 0} chars");
                                _logger.LogInformation("Vision analysis complete: {Length} chars", result?.Length ?? 0);
                                return result;
                            }
                        }
                    }
                }
            }

            // Fallback: try Chat Completions format (in case API returns that)
            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var message = choices[0].GetProperty("message").GetProperty("content").GetString();
                diag?.AppendLine($"[vision] Result (choices format): {message?.Length ?? 0} chars");
                return message;
            }

            var respSnippet = responseContent.Length > 300 ? responseContent[..300] : responseContent;
            diag?.AppendLine($"[vision] No output found in response: {respSnippet}");
            _logger.LogWarning("Vision API returned no output");
            return null;
        }
        catch (Exception ex)
        {
            diag?.AppendLine($"[vision] EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            _logger.LogCritical(ex, "Failed to call vision model — will proceed without image analysis");
            return null;
        }
    }
}
