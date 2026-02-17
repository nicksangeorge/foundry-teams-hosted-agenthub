# Customization Guide

> How to adapt the Contoso Multi-Agent Teams Hub template for your own use case

---

## Table of Contents

- [Changing Brands](#changing-brands)
- [Adding a New Agent](#adding-a-new-agent)
- [Modifying Adaptive Cards](#modifying-adaptive-cards)
- [Adjusting Image Analysis](#adjusting-image-analysis)
- [Environment-Specific Configuration](#environment-specific-configuration)
- [Scaling Considerations](#scaling-considerations)

---

## Changing Brands

The template uses three demo brands: **Contoso Burger**, **Contoso Tacos**, and **Contoso Pizza**. To replace them with your own brands:

### 1. Update Agent System Prompts

Edit the `SYSTEM_PROMPT` in each agent's `main.py`:

**`agents/ops-agent/main.py`**:
```python
SYSTEM_PROMPT = """You are the **Ops Agent** for [Your Company] — a data-driven assistant
for [Brand A], [Brand B], and [Brand C] restaurant operations.
...
**Demo data**
- Sales: ... (update with your ranges)
- Stores: ... (update with your store numbers)
- Regions: ... (update with your regions)
"""
```

**`agents/menu-agent/main.py`**:
```python
SYSTEM_PROMPT = """You are the **Menu & Marketing Agent** for [Your Company] — a creative
partner for [Brand A], [Brand B], and [Brand C] campaigns.
...
**Brand voice cheat-sheet**
- **[Brand A]** — Tone: ... — Cue words: ...
- **[Brand B]** — Tone: ... — Cue words: ...
- **[Brand C]** — Tone: ... — Cue words: ...
"""
```

### 2. Update Brand Detection

**`agents/menu-agent/main.py`** — `detect_brand()`:
```python
def detect_brand(user_message: str) -> Optional[str]:
    message_lower = user_message.lower()
    if "brand_a_keyword" in message_lower:
        return "Brand A"
    elif "brand_b_keyword" in message_lower:
        return "Brand B"
    elif "brand_c_keyword" in message_lower:
        return "Brand C"
    return None
```

**`bot/Services/AdaptiveCardService.cs`** — `DetectBrand()`:
```csharp
private static string? DetectBrand(string text)
{
    var lower = text.ToLowerInvariant();
    if (lower.Contains("brand_a_keyword"))
        return "Brand A";
    if (lower.Contains("brand_b_keyword"))
        return "Brand B";
    if (lower.Contains("brand_c_keyword"))
        return "Brand C";
    return null;
}
```

Also update the brand emoji mapping in `TryBuildMenuCard()`:
```csharp
var brandEmoji = brand switch
{
    "Brand A" => "🍔",
    "Brand B" => "🌮",
    "Brand C" => "🍕",
    _ => "🍽️"
};
```

### 3. Update Bot Welcome Message

In `bot/ContosoAgent.cs`, update the welcome text in `OnMembersAddedAsync`:
```csharp
var welcome = """
    👋 **Welcome to the [Your Company] Agent Hub!**

    Just type your question and I'll automatically route it to the right agent:

    🔧 **Ops Agent** — ...
    🍔 **Menu & Marketing Agent** — ...

    Or use direct commands: `/ops`, `/menu`, `/help`
    """;
```

### 4. Update Teams Manifest

In `appPackage/manifest.json`, update:
- `developer.name`
- `name.short` and `name.full`
- `description.short` and `description.full`

---

## Adding a New Agent

To add a third agent (e.g., a "Training Agent"):

### Step 1: Create the Agent Directory

```
agents/
  training-agent/
    main.py
    agent.yaml
    Dockerfile
    requirements.txt
```

### Step 2: Write the Agent (`main.py`)

Follow the pattern from the existing agents:

```python
"""Training Agent - LangGraph Hosted Agent"""

import os
import logging
from typing import TypedDict, Annotated, Sequence
from langchain_core.messages import BaseMessage, HumanMessage, AIMessage, SystemMessage
from langgraph.graph import StateGraph, END
from langgraph.graph.message import add_messages
from azure.ai.agentserver.langgraph import from_langgraph
from langchain_openai import AzureChatOpenAI

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

SYSTEM_PROMPT = """You are the Training Agent for [Your Company]..."""

class AgentState(TypedDict):
    messages: Annotated[Sequence[BaseMessage], add_messages]
    reasoning_steps: list[str]

AZURE_OPENAI_ENDPOINT = os.environ.get("AZURE_OPENAI_ENDPOINT") or os.environ.get("AZURE_AI_ENDPOINT")
MODEL_DEPLOYMENT = os.environ.get("AZURE_AI_MODEL_DEPLOYMENT_NAME", "gpt-4o-mini")
API_VERSION = os.environ.get("OPENAI_API_VERSION", "2025-03-01-preview")

def create_llm():
    from azure.identity import DefaultAzureCredential, get_bearer_token_provider
    token_provider = get_bearer_token_provider(
        DefaultAzureCredential(),
        "https://cognitiveservices.azure.com/.default"
    )
    return AzureChatOpenAI(
        azure_endpoint=AZURE_OPENAI_ENDPOINT,
        azure_deployment=MODEL_DEPLOYMENT,
        api_version=API_VERSION,
        azure_ad_token_provider=token_provider,
        streaming=True
    )

def training_node(state: AgentState) -> AgentState:
    llm = create_llm()
    messages = [SystemMessage(content=SYSTEM_PROMPT)] + list(state["messages"])
    response = llm.invoke(messages)
    return {"messages": [response], "reasoning_steps": []}

def should_continue(state: AgentState) -> str:
    return END

def build_graph():
    workflow = StateGraph(AgentState)
    workflow.add_node("training", training_node)
    workflow.set_entry_point("training")
    workflow.add_conditional_edges("training", should_continue)
    return workflow.compile()

graph = build_graph()
hosted_agent = from_langgraph(graph)

if __name__ == "__main__":
    hosted_agent.run()
```

### Step 3: Create `agent.yaml`

```yaml
name: training-agent
displayName: Training Agent
description: |
  Training and onboarding agent for new employees.

container:
  dockerfile: Dockerfile
  context: .

resources:
  cpu: "1"
  memory: "2Gi"

environmentVariables:
  AZURE_AI_PROJECT_ENDPOINT: ${AZURE_AI_PROJECT_ENDPOINT}
  AZURE_AI_MODEL_DEPLOYMENT_NAME: ${AZURE_AI_MODEL_DEPLOYMENT_NAME:-gpt-4o-mini}
  AZURE_AI_ENDPOINT: ${AZURE_AI_ENDPOINT}

protocols:
  - name: responses
    version: v1

scaling:
  minReplicas: 1
  maxReplicas: 2
```

### Step 4: Create `Dockerfile` and `requirements.txt`

Copy from an existing agent — they are identical for most LangGraph agents.

### Step 5: Add Routing via the Orchestrator

The bot now uses an **orchestrator agent** for intent classification. To add a new agent as a routing target:

1. **Add a `@tool` function in the orchestrator** (`agents/orchestrator/main.py`):
```python
@tool
def query_training_agent(question: str) -> str:
    """Route training, onboarding, certification, and learning questions
    to the Training Agent."""
    return _call_sub_agent(question, "ContosoTrainingAgent", "1")
```

2. **Add the tool to the ReAct agent** in `build_graph()`:
```python
tools = [query_ops_agent, query_menu_agent, query_training_agent]
```

3. **Update the orchestrator system prompt** to mention the new tool.

4. **(Optional) Add a prefix bypass** in `bot/ContosoAgent.cs`:
```csharp
private const string TrainingPrefix = "/train";
// Add to ParseCommand():
if (text.StartsWith(TrainingPrefix, StringComparison.OrdinalIgnoreCase))
{
    var msg = text[TrainingPrefix.Length..].Trim();
    return (AgentType.Training,
        string.IsNullOrEmpty(msg) ? "How can I help with training?" : msg);
}
```

5. Add to the `AgentType` enum and add a case in the routing switch.

### Step 6: Add Configuration

In `appsettings.template.json`, add:
```json
{
  "Foundry": {
    "TrainingAgentName": "ContosoTrainingAgent",
    "TrainingAgentVersion": "1"
  }
}
```

In `FoundryAgentService.cs`, add the corresponding streaming/invoke methods and constructor initialization.

### Step 7: Add to Deployment Script

In `agents/deploy_hosted_agents.py`, add:
```python
training = deploy_agent(
    client,
    agent_name="ContosoTrainingAgent",
    image=f"{ACR}/contoso-training-agent:v1",
    display_name="Contoso Training Agent"
)
```

### Step 8: Update the Teams Manifest

In `appPackage/manifest.json`, add the new command:
```json
{
  "title": "/train",
  "description": "Ask the Training Agent about onboarding and certifications"
}
```

---

## Modifying Adaptive Cards

### Ops Card (KPI Dashboard)

The `TryBuildOpsCard` method in `AdaptiveCardService.cs` builds cards by extracting metrics from text. To customize:

**Change the metric extraction patterns** — edit the regex patterns in `ExtractMetrics()`:
```csharp
var patterns = new[]
{
    @"\*{2}(.+?)\*{2}\s*[:—–-]\s*(.+?)(?:\n|$)",         // **bold**: value
    @"^[-•]\s*(.+?)\s*[:—–]\s*(.+?)(?:\n|$)",             // bullet: value
    @"(?:^|\n)(.{3,35}):\s+(\$?[\d,.]+[%]?\s*\w{0,20})", // Label: $number
};
```

**Change the card layout** — modify the anonymous object in `TryBuildOpsCard()`. The card uses Adaptive Card schema 1.5. Key elements:
- `ColumnSet` for header layout
- `FactSet` for key-value metrics
- `TextBlock` for titles and footers

**Add action buttons**:
```csharp
new
{
    type = "Action.OpenUrl",
    title = "View Full Report",
    url = "https://your-dashboard.com"
}
```

### Menu Card (Creative Brief)

The `TryBuildMenuCard` method detects named concepts and builds creative brief cards. To customize:

**Adjust concept detection** — the regex in `TryBuildMenuCard`:
```csharp
var conceptMatch = Regex.Match(response,
    @"[\p{So}\p{Cs}]+\s*\*{0,2}(.+?)\*{0,2}\s*[—–-]\s*(.+?)(?:\.|$)",
    RegexOptions.Multiline);
```

**Add new card types** — create a new method like `TryBuildTrainingCard()` and add it to `TryBuildCard()`:
```csharp
public Attachment? TryBuildCard(string agentResponse, string agentType)
{
    return agentType switch
    {
        "Ops" => TryBuildOpsCard(agentResponse),
        "Menu & Marketing" => TryBuildMenuCard(agentResponse),
        "Training" => TryBuildTrainingCard(agentResponse),
        _ => null
    };
}
```

---

## Adjusting Image Analysis

### Vision Prompt

The image analysis prompt is in `ImageAnalysisService.cs` in `CallVisionModelAsync()`:

```csharp
new
{
    type = "input_text",
    text = "Describe this image in the context of Contoso Restaurants operations. " +
           $"The user said: \"{userText}\". " +
           "Provide a concise factual description (2-3 sentences) that can be used " +
           "as context for answering their question."
}
```

Customize this prompt for your domain. For example, for a retail company:
```
"Describe this image in the context of retail store operations and merchandising. ..."
```

### Vision Model

The vision model is configured in `appsettings.json`:
```json
{
  "Foundry": {
    "VisionDeployment": "gpt-4o-mini"
  }
}
```

Change this to any vision-capable model deployment in your Foundry project.

### Max Tokens

Adjust `max_output_tokens` in the request body (default: 300) to control description length.

---

## Environment-Specific Configuration

### Configuration Hierarchy

The bot reads configuration from (in priority order):
1. Environment variables (highest priority)
2. `appsettings.json` (production)
3. `appsettings.Development.json` (development)

### Template Flow

```
sample.env                    # Template — tracked in git
    ↓ cp sample.env .env
.env                          # Your values — gitignored
    ↓ azd provision/deploy
appsettings.json              # Generated from template — gitignored
```

### Key Environment Variables

| Variable | Purpose | Example |
|----------|---------|---------|
| `AZURE_SUBSCRIPTION_ID` | Target subscription | `12345678-...` |
| `AZURE_LOCATION` | Deployment region | `eastus2` |
| `BOT_APP_ID` | Entra ID app client ID | `abcdef01-...` |
| `BOT_APP_SECRET` | Entra ID app client secret | `***` |
| `TENANT_ID` | Entra ID tenant | `98765432-...` |
| `FOUNDRY_PROJECT_ENDPOINT` | Foundry project URL | `https://....ai.azure.com` |
| `AI_ENDPOINT` | Azure AI endpoint | `https://....cognitiveservices.azure.com` |
| `FOUNDRY_ACR` | Container registry | `myacr.azurecr.io` |
| `OPS_AGENT_NAME` | Ops agent name in Foundry | `ContosoOpsAgent` |
| `MENU_AGENT_NAME` | Menu agent name in Foundry | `ContosoMenuAgent` |
| `ORCHESTRATOR_AGENT_NAME` | Orchestrator agent name in Foundry | `ContosoOrchestratorAgent` |
| `MODEL_DEPLOYMENT` | LLM model deployment | `gpt-4o-mini` |
| `VISION_DEPLOYMENT` | Vision model deployment | `gpt-4o-mini` |

### Local Development

For local development:
1. Run `az login` (provides `DefaultAzureCredential` for Foundry calls)
2. Set `appsettings.Development.json` with your bot credentials
3. Run `dotnet run` — listens on `http://localhost:3978`
4. Use [Dev Tunnel](https://learn.microsoft.com/azure/developer/dev-tunnels/) or ngrok to expose the endpoint to Bot Service

---

## Scaling Considerations

### Bot (Container Apps)

- **Minimum replicas**: Set to 1 for development, 2+ for production
- **Scaling rule**: Scale on HTTP concurrent requests (default: 10 per replica)
- **CPU/Memory**: Start with 0.5 vCPU / 1 GiB, increase for high-traffic deployments
- **State**: The bot uses `MemoryStorage` by default — swap to `CosmosDbPartitionedStorage` or `BlobsStorage` for multi-replica deployments

```csharp
// In Program.cs, replace:
builder.Services.AddSingleton<IStorage, MemoryStorage>();

// With:
builder.Services.AddSingleton<IStorage>(sp =>
    new CosmosDbPartitionedStorage(new CosmosDbPartitionedStorageOptions
    {
        CosmosDbEndpoint = "https://...",
        DatabaseId = "botstate",
        ContainerId = "state"
    }));
```

### Hosted Agents (Foundry)

- **Scaling**: Configured in `agent.yaml` (`minReplicas` / `maxReplicas`)
- **Cold start**: First request after idle may take 10–30 seconds — the bot's 2-minute timeout accommodates this
- **Resources**: Each agent container runs with 1 CPU / 2 GiB memory by default

### Azure OpenAI

- **Rate limits**: Monitor TPM (tokens per minute) usage in Azure Portal
- **Model selection**: `gpt-4o-mini` is cost-effective for most use cases; upgrade to `gpt-4o` for complex reasoning
- **Deployment region**: Place the model deployment in the same region as your Foundry project for lowest latency
