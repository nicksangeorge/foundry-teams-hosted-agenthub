using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Azure.Core;

namespace ContosoAgentBot.Services;

/// <summary>
/// Service for invoking Microsoft Foundry Hosted Agents via the Responses API
/// </summary>
public class FoundryAgentService : IFoundryAgentService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FoundryAgentService> _logger;
    private readonly DefaultAzureCredential _credential;

    private readonly string _projectEndpoint;
    private readonly string _opsAgentName;
    private readonly string _opsAgentVersion;
    private readonly string _menuAgentName;
    private readonly string _menuAgentVersion;
    private readonly string _orchestratorAgentName;
    private readonly string _orchestratorAgentVersion;

    public FoundryAgentService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<FoundryAgentService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
        // DefaultAzureCredential: uses AzureCLI locally, ManagedIdentity in containers
        _credential = new DefaultAzureCredential();

        _projectEndpoint = configuration["Foundry:ProjectEndpoint"]
            ?? throw new InvalidOperationException("Foundry:ProjectEndpoint configuration is required. Set it in appsettings.json or environment variables.");
        _opsAgentName = configuration["Foundry:OpsAgentName"] ?? "ContosoOpsAgent";
        _opsAgentVersion = configuration["Foundry:OpsAgentVersion"] ?? "1";
        _menuAgentName = configuration["Foundry:MenuAgentName"] ?? "ContosoMenuAgent";
        _menuAgentVersion = configuration["Foundry:MenuAgentVersion"] ?? "1";
        _orchestratorAgentName = configuration["Foundry:OrchestratorAgentName"] ?? "ContosoOrchestratorAgent";
        _orchestratorAgentVersion = configuration["Foundry:OrchestratorAgentVersion"] ?? "1";

        _logger.LogInformation("FoundryAgentService initialized - Endpoint: {Endpoint}, OpsAgent: {Ops}, MenuAgent: {Menu}, OrchestratorAgent: {Orchestrator}",
            _projectEndpoint, _opsAgentName, _menuAgentName, _orchestratorAgentName);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Getting Azure access token...");
        var tokenRequestContext = new TokenRequestContext(new[] { "https://ai.azure.com/.default" });
        var token = await _credential.GetTokenAsync(tokenRequestContext, cancellationToken);
        _logger.LogInformation("Got access token (expires: {Expires})", token.ExpiresOn);
        return token.Token;
    }

    public async Task<AgentResponse> InvokeOpsAgentAsync(string userMessage, string conversationId, CancellationToken cancellationToken = default)
    {
        return await InvokeAgentAsync(_opsAgentName, _opsAgentVersion, userMessage, conversationId, cancellationToken);
    }

    public async Task<AgentResponse> InvokeMenuAgentAsync(string userMessage, string conversationId, CancellationToken cancellationToken = default)
    {
        return await InvokeAgentAsync(_menuAgentName, _menuAgentVersion, userMessage, conversationId, cancellationToken);
    }

    public IAsyncEnumerable<string> StreamOpsAgentAsync(string userMessage, string conversationId, CancellationToken cancellationToken = default)
    {
        return StreamAgentAsync(_opsAgentName, _opsAgentVersion, userMessage, conversationId, cancellationToken);
    }

    public IAsyncEnumerable<string> StreamMenuAgentAsync(string userMessage, string conversationId, CancellationToken cancellationToken = default)
    {
        return StreamAgentAsync(_menuAgentName, _menuAgentVersion, userMessage, conversationId, cancellationToken);
    }

    public IAsyncEnumerable<string> StreamOrchestratorAgentAsync(string userMessage, string conversationId, CancellationToken cancellationToken = default)
    {
        return StreamAgentAsync(_orchestratorAgentName, _orchestratorAgentVersion, userMessage, conversationId, cancellationToken);
    }

    public async Task<AgentResponse> InvokeOrchestratorAgentAsync(string userMessage, string conversationId, CancellationToken cancellationToken = default)
    {
        return await InvokeAgentAsync(_orchestratorAgentName, _orchestratorAgentVersion, userMessage, conversationId, cancellationToken);
    }

    private async IAsyncEnumerable<string> StreamAgentAsync(
        string agentName,
        string agentVersion,
        string userMessage,
        string conversationId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting streaming invocation of agent {AgentName} v{Version}", agentName, agentVersion);

        // Get access token for Microsoft Foundry
        var accessToken = await GetAccessTokenAsync(cancellationToken);

        // Hosted Agents use the agent_reference pattern with /openai/responses endpoint
        var responsesUrl = $"{_projectEndpoint}/openai/responses?api-version=2025-11-15-preview";

        // Build request with agent_reference format and streaming enabled
        var requestBody = new
        {
            input = new[] { new { role = "user", content = userMessage } },
            agent = new { type = "agent_reference", name = agentName, version = agentVersion },
            stream = true // Enable streaming
        };

        var jsonBody = JsonSerializer.Serialize(requestBody);
        _logger.LogInformation("Streaming request body: {Body}", jsonBody);

        var request = new HttpRequestMessage(HttpMethod.Post, responsesUrl)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        // Send with ResponseHeadersRead to start streaming immediately
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Streaming agent {AgentName} returned error: {StatusCode} - {Content}",
                agentName, response.StatusCode, errorContent);
            yield return $"[Error: {response.StatusCode}]";
            yield break;
        }

        // Read the SSE stream
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);

            if (string.IsNullOrEmpty(line))
                continue;

            // SSE format: "data: {json}" or "data: [DONE]"
            if (!line.StartsWith("data: "))
                continue;

            var data = line.Substring(6); // Remove "data: " prefix

            if (data == "[DONE]")
            {
                _logger.LogInformation("Stream completed for agent {AgentName}", agentName);
                break;
            }

            // Parse the JSON chunk
            var chunk = ParseStreamingChunk(data);
            if (!string.IsNullOrEmpty(chunk))
            {
                yield return chunk;
            }
        }
    }

    private string? ParseStreamingChunk(string jsonData)
    {
        try
        {
            // Log first chunk for debugging the format
            _logger.LogInformation("Streaming chunk received: {Data}", jsonData.Length > 500 ? jsonData.Substring(0, 500) + "..." : jsonData);

            using var doc = JsonDocument.Parse(jsonData);
            var root = doc.RootElement;

            // Responses API streaming format - look for delta content
            // Format varies: could be output_text, delta, choices[].delta.content, etc.

            // Try output_text_delta (Responses API) - might be string directly
            if (root.TryGetProperty("output_text_delta", out var outputTextDelta))
            {
                if (outputTextDelta.ValueKind == JsonValueKind.String)
                {
                    return outputTextDelta.GetString();
                }
            }

            // Try delta - could be string or object
            if (root.TryGetProperty("delta", out var delta))
            {
                // Delta might be a direct string
                if (delta.ValueKind == JsonValueKind.String)
                {
                    return delta.GetString();
                }
                // Or it might be an object with text/content
                if (delta.ValueKind == JsonValueKind.Object)
                {
                    if (delta.TryGetProperty("text", out var deltaText))
                    {
                        return deltaText.GetString();
                    }
                    if (delta.TryGetProperty("content", out var deltaContent))
                    {
                        return deltaContent.GetString();
                    }
                }
            }

            // Try choices format (OpenAI compatible)
            if (root.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];
                if (firstChoice.TryGetProperty("delta", out var choiceDelta) &&
                    choiceDelta.ValueKind == JsonValueKind.Object)
                {
                    if (choiceDelta.TryGetProperty("content", out var content))
                    {
                        return content.GetString();
                    }
                }
            }

            // Try output array for incremental message content
            if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in output.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;

                    if (item.TryGetProperty("type", out var itemType) &&
                        itemType.GetString() == "message")
                    {
                        if (item.TryGetProperty("content", out var contentArray) &&
                            contentArray.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var contentItem in contentArray.EnumerateArray())
                            {
                                if (contentItem.TryGetProperty("text", out var text))
                                {
                                    return text.GetString();
                                }
                            }
                        }
                    }
                }
            }

            // Log unrecognized format for debugging
            _logger.LogDebug("Unrecognized streaming chunk format: {Data}", jsonData);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse streaming chunk: {Data}", jsonData);
            return null;
        }
    }

    private async Task<AgentResponse> InvokeAgentAsync(string agentName, string agentVersion, string userMessage, string conversationId, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Invoking hosted agent {AgentName} v{Version} for conversation {ConversationId}", agentName, agentVersion, conversationId);

            // Get access token for Microsoft Foundry
            var accessToken = await GetAccessTokenAsync(cancellationToken);

            // Hosted Agents use the agent_reference pattern with /openai/responses endpoint
            var responsesUrl = $"{_projectEndpoint}/openai/responses?api-version=2025-11-15-preview";
            _logger.LogInformation("Calling Microsoft Foundry API: {Url}", responsesUrl);

            // Build request with agent_reference format
            var requestBody = new
            {
                input = new[] { new { role = "user", content = userMessage } },
                agent = new { type = "agent_reference", name = agentName, version = agentVersion }
            };

            var jsonBody = JsonSerializer.Serialize(requestBody);
            _logger.LogInformation("Request body: {Body}", jsonBody);

            var request = new HttpRequestMessage(HttpMethod.Post, responsesUrl)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };

            // Use Bearer token authentication for Microsoft Foundry
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            _logger.LogInformation("Sending request to Microsoft Foundry...");
            var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogInformation("Response status: {Status}, content length: {Length}", response.StatusCode, responseContent.Length);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Hosted agent {AgentName} returned error: {StatusCode} - {Content}", 
                    agentName, response.StatusCode, responseContent);
                    
                return new AgentResponse
                {
                    Success = false,
                    Error = $"Agent returned {response.StatusCode}: {responseContent}"
                };
            }

            // Parse the response - Hosted Agents use Responses API format
            var agentResponse = ParseAgentResponse(responseContent);
            agentResponse.Success = true;
            
            _logger.LogInformation("Agent {AgentName} responded successfully", agentName);
            return agentResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error invoking agent {AgentName}", agentName);
            return new AgentResponse
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    private AgentResponse ParseAgentResponse(string responseContent)
    {
        var result = new AgentResponse();
        
        try
        {
            using var doc = JsonDocument.Parse(responseContent);
            var root = doc.RootElement;

            // Responses API format: look for output_text or output array
            if (root.TryGetProperty("output_text", out var outputText))
            {
                result.Content = outputText.GetString() ?? string.Empty;
            }
            else if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            {
                // Parse output array - look for message items
                var contentBuilder = new StringBuilder();
                foreach (var item in output.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var itemType) && 
                        itemType.GetString() == "message")
                    {
                        if (item.TryGetProperty("content", out var contentArray))
                        {
                            foreach (var contentItem in contentArray.EnumerateArray())
                            {
                                if (contentItem.TryGetProperty("text", out var text))
                                {
                                    contentBuilder.AppendLine(text.GetString());
                                }
                            }
                        }
                    }
                }
                result.Content = contentBuilder.ToString().Trim();
            }
            // Fallback: check for choices format (OpenAI compatible)
            else if (root.TryGetProperty("choices", out var choices) && 
                choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];
                if (firstChoice.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("content", out var content))
                {
                    result.Content = content.GetString() ?? string.Empty;
                }
            }
            
            // Extract chain-of-thought if present (from agent metadata or reasoning)
            if (root.TryGetProperty("metadata", out var metadata))
            {
                if (metadata.TryGetProperty("chain_of_thought", out var cot))
                {
                    result.ChainOfThought = cot.GetString();
                }
                
                if (metadata.TryGetProperty("citations", out var citations))
                {
                    foreach (var citation in citations.EnumerateArray())
                    {
                        result.Citations.Add(citation.GetString() ?? string.Empty);
                    }
                }
            }
            
            // Check for reasoning in the output
            if (root.TryGetProperty("output", out var outputForReasoning) && 
                outputForReasoning.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in outputForReasoning.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var itemType) && 
                        itemType.GetString() == "reasoning")
                    {
                        if (item.TryGetProperty("content", out var reasoningContent))
                        {
                            var reasoningBuilder = new StringBuilder();
                            foreach (var r in reasoningContent.EnumerateArray())
                            {
                                if (r.TryGetProperty("text", out var text))
                                {
                                    reasoningBuilder.AppendLine(text.GetString());
                                }
                            }
                            result.ChainOfThought = reasoningBuilder.ToString().Trim();
                        }
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            // If parsing fails, return raw content
            result.Content = responseContent;
            _logger.LogWarning(ex, "Failed to parse agent response as JSON, using raw content");
        }

        return result;
    }
}
