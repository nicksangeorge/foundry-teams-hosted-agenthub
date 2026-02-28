namespace ContosoAgentBot.Services;

/// <summary>
/// Service interface for invoking Microsoft Foundry Hosted Agents
/// </summary>
public interface IFoundryAgentService
{
    /// <summary>
    /// Invokes the Ops Agent for operational queries
    /// </summary>
    Task<AgentResponse> InvokeOpsAgentAsync(string userMessage, string conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invokes the Menu/Marketing Agent for creative campaigns
    /// </summary>
    Task<AgentResponse> InvokeMenuAgentAsync(string userMessage, string conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invokes the Ops Agent with streaming, calling the callback for each text chunk
    /// </summary>
    IAsyncEnumerable<string> StreamOpsAgentAsync(string userMessage, string conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invokes the Menu/Marketing Agent with streaming, calling the callback for each text chunk
    /// </summary>
    IAsyncEnumerable<string> StreamMenuAgentAsync(string userMessage, string conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invokes the Orchestrator Agent with streaming for intent classification and routing
    /// </summary>
    IAsyncEnumerable<string> StreamOrchestratorAgentAsync(string userMessage, string conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invokes the Orchestrator Agent (non-streaming) for intent classification and routing
    /// </summary>
    Task<AgentResponse> InvokeOrchestratorAgentAsync(string userMessage, string conversationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Response from a Microsoft Foundry Hosted Agent
/// </summary>
public class AgentResponse
{
    public string Content { get; set; } = string.Empty;
    public string? ChainOfThought { get; set; }
    public List<string> Citations { get; set; } = new();
    public bool Success { get; set; }
    public string? Error { get; set; }
}
