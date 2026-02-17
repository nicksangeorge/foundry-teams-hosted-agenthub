# Architecture Reference

> Detailed technical architecture for the Contoso Multi-Agent Teams Hub

---

## Table of Contents

- [System Overview](#system-overview)
- [Component Descriptions](#component-descriptions)
- [Data Flow](#data-flow)
- [Agent Routing Logic](#agent-routing-logic)
- [Streaming Architecture](#streaming-architecture)
- [Image Handling Pipeline](#image-handling-pipeline)
- [Adaptive Cards](#adaptive-cards)
- [Hosted Agent Architecture](#hosted-agent-architecture)
- [Authentication Model](#authentication-model)
- [Infrastructure Automation](#infrastructure-automation)

---

## System Overview

The Contoso Multi-Agent Teams Hub is a two-tier architecture:

1. **Frontend Tier** — A .NET 8 Custom Engine Agent running on Azure Container Apps, acting as a Teams bot and streaming relay.
2. **Backend Tier** — Python agents running as Azure AI Foundry Hosted Agents, invoked via the Responses API. The orchestrator and ops agent use LangGraph; the menu agent uses Microsoft Agent Framework. Both frameworks are wrapped with framework-specific adapters that expose the same Responses API contract.

```
┌─────────────────────────────────────────────────────────────────┐
│                        Microsoft Teams                          │
│                                                                 │
│  User ──▶ message ──▶ Bot Service ──▶ POST /api/messages        │
│  User ◀── streaming tokens ◀── QueueTextChunk                   │
│  User ◀── Adaptive Card ◀── follow-up message                   │
└────────────────────────────┬────────────────────────────────────┘
                             │
                    Azure Bot Service
                    (F0, SingleTenant)
                             │
                             ▼
              ┌──────────────────────────────┐
              │  Azure Container Apps         │
              │                              │
              │  ContosoAgent.cs             │
              │  ├── ParseCommand()          │
              │  ├── StreamFoundryResponse() │
              │  ├── FallbackDownloadImages()│
              │  └── OnReactionsAdded()      │
              │                              │
              │  Services/                   │
              │  ├── FoundryAgentService     │
              │  ├── AdaptiveCardService     │
              │  └── ImageAnalysisService    │
              └──────────────┬───────────────┘
                             │
                Responses API (SSE)
              DefaultAzureCredential Bearer
                             │
                             ▼
              ┌──────────────────────────────┐
              │  Azure AI Foundry             │
              │  Hosted Agent Service         │
              │                              │
              │  ┌─────────────────────────┐ │
              │  │ ContosoOpsAgent         │ │
              │  │ LangGraph + reasoning   │ │
              │  │ (Python 3.12 container) │ │
              │  └─────────────────────────┘ │
              │                              │
              │  ┌─────────────────────────┐ │
              │  │ ContosoMenuAgent        │ │
              │  │ Agent Framework          │ │
              │  │ (Python 3.12 container) │ │
              │  └─────────────────────────┘ │
              │              │               │
              │              ▼               │
              │     Azure OpenAI             │
              │     (gpt-4o-mini)            │
              └──────────────────────────────┘
```

## Component Descriptions

### ContosoAgent.cs — Bot Core

The main bot class, extending `AgentApplication` from the Microsoft 365 Agents SDK. Responsibilities:

| Handler | Purpose |
|---------|---------|
| `OnMembersAddedAsync` | Sends a welcome message with usage instructions on bot install |
| `OnMessageAsync` | Parses commands, handles images, routes to the correct agent, streams responses |
| `OnReactionsAddedAsync` | Responds contextually to emoji reactions (👍 👎 😄 etc.) |
| `StreamFoundryResponseAsync` | Opens an SSE stream to a Foundry agent and relays chunks to Teams |
| `FallbackDownloadImagesAsync` | Multi-layer image download when the SDK pipeline returns empty |
| `ParseCommand` | Prefix-based routing with orchestrator fallback for unmatched messages |

### FoundryAgentService.cs — Foundry API Client

Manages all communication with Azure AI Foundry Hosted Agents:

- **Token acquisition**: `DefaultAzureCredential` → Bearer token with scope `https://ai.azure.com/.default`
- **Streaming invocation**: `POST /openai/responses?api-version=2025-11-15-preview` with `stream: true`
- **SSE parsing**: Reads `data:` lines, handles `[DONE]`, parses multiple response formats (`output_text_delta`, `delta`, `choices`, `output` array)
- **Non-streaming invocation**: Same endpoint without `stream: true`, parses full JSON response

### AdaptiveCardService.cs — Rich Data Views

Builds Adaptive Cards from agent response text as follow-up messages:

- **Ops Agent**: Extracts KPI metrics using regex patterns (`**Label**: Value`), builds a `FactSet`-based dashboard card
- **Menu Agent**: Detects named concepts (emoji + bold name pattern), identifies brand, builds a creative brief card
- **Brand detection**: Keyword matching for Contoso Burger, Contoso Tacos, Contoso Pizza

### ImageAnalysisService.cs — Vision Processing

Processes uploaded images through GPT vision:

- Accepts pre-downloaded `InputFile` objects from the SDK pipeline or fallback downloader
- Converts image bytes to base64 data URIs
- Calls the Responses API with `input_image` content parts and a contextual system prompt
- Returns a 2–3 sentence factual description that gets appended to the user's message

### Hosted Agents (Python)

Three agents deployed as containers to Azure AI Foundry. The orchestrator and ops agent use LangGraph; the menu agent uses Microsoft Agent Framework. Foundry treats them identically — each container exposes the Responses API on port 8088 via a framework-specific adapter.

| Agent | File | Framework | Pattern | Specialization |
|-------|------|-----------|---------|----------------|
| **Orchestrator Agent** | `agents/orchestrator/main.py` | LangGraph | ReAct `create_react_agent` with `@tool` functions | Intent classification and routing to sub-agents via Foundry API |
| **Ops Agent** | `agents/ops-agent/main.py` | LangGraph | Single-node `reasoning_node` | Operational Q&A with simulated metrics |
| **Menu Agent** | `agents/menu-agent/main.py` | Agent Framework | `Agent` with `AzureOpenAIChatClient` | Marketing campaigns with brand voice matching |

LangGraph agents use:
- `AzureChatOpenAI` with `DefaultAzureCredential` token provider
- The `azure-ai-agentserver-langgraph` adapter (`from_langgraph()`) to expose a Responses API-compatible endpoint

The Agent Framework menu agent uses:
- `AzureOpenAIChatClient` with `DefaultAzureCredential`
- The `azure-ai-agentserver-agentframework` adapter (`from_agent_framework()`) to expose the same endpoint

All agents use structured system prompts with formatting rules optimized for Teams rendering.

---

## Data Flow

### Complete Message Lifecycle

```
1. USER types "/ops How are Southwest stores performing?"
   │
2. TEAMS sends Activity to Azure Bot Service
   │
3. BOT SERVICE forwards POST /api/messages to Container App
   │
4. ASP.NET adapter calls ContosoAgent.OnMessageAsync()
   │
5. IMAGE CHECK — Are there attachments?
   │  ├── Yes → SDK M365AttachmentDownloader (Layer 1)
   │  │         Fallback: authenticated download (Layer 2)
   │  │         Fallback: anonymous download (Layer 3)
   │  │         → GPT vision analysis → append description to message
   │  └── No  → continue
   │
6. ROUTING — ParseCommand("/ops How are Southwest stores performing?")
   │  ├── Prefix match: /ops → AgentType.Ops, message = "How are SW stores..."
   │  ├── Prefix match: /menu → AgentType.Menu
   │  ├── Prefix match: /help → send help text
   │  └── No prefix → AgentType.Orchestrator
   │        └── Orchestrator Agent uses LLM tool-calling to classify and route
   │
7. STREAMING
   │  a. QueueInformativeUpdateAsync("Contacting the Ops Agent…")
   │  b. FoundryAgentService.StreamOpsAgentAsync():
   │     i.   GetAccessTokenAsync() → DefaultAzureCredential
   │     ii.  POST {endpoint}/openai/responses?api-version=2025-11-15-preview
   │           Body: { input: [{role:"user", content:"..."}],
   │                   agent: {type:"agent_reference", name:"ContosoOpsAgent",
   │                           version:"1"},
   │                   stream: true }
   │     iii. Read SSE stream line by line
   │  c. For each chunk: QueueTextChunk(chunk) → Teams renders incrementally
   │  d. EndStreamAsync() → finalizes the message
   │
8. ADAPTIVE CARD — AdaptiveCardService.TryBuildCard()
   │  ├── Ops: ExtractMetrics() finds "**Revenue**: $3.2M" patterns → FactSet card
   │  └── Menu: Regex finds concept name → creative brief card
   │  → SendActivityAsync(cardAttachment) as follow-up message
   │
9. CONTEXT STORE — Save (activityId → query, agent) for reaction follow-ups
```

### SSE Stream Format

The Foundry Responses API streams events in Server-Sent Events format:

```
data: {"output_text_delta": "📈"}
data: {"output_text_delta": " **Southwest"}
data: {"output_text_delta": " Region"}
data: {"output_text_delta": " Performance**\n\n"}
...
data: [DONE]
```

The `FoundryAgentService.ParseStreamingChunk()` method handles multiple response formats:

1. `output_text_delta` (string) — Primary Responses API format
2. `delta.text` or `delta.content` (object) — Alternative delta format
3. `choices[0].delta.content` — OpenAI Chat Completions compatible format
4. `output[].content[].text` — Full output array with message items

---

## Agent Routing Logic

### Priority Order

1. **Prefix commands** — Exact match takes priority (bypass orchestrator):
   - `/ops <message>` → Ops Agent directly
   - `/menu <message>` → Menu Agent directly
   - `/help` → Help text

2. **Orchestrator (default)** — When no prefix is detected:
   - Message is sent to the **Orchestrator Agent** (LangGraph ReAct)
   - The orchestrator uses LLM-based intent classification with tool-calling
   - Two tools available: `query_ops_agent` and `query_menu_agent`
   - The LLM analyzes the user's message and calls the appropriate tool
   - For greetings or general queries, the orchestrator responds directly

### Orchestrator Tool-Calling Flow

```
User message (no prefix)
    │
    ▼
Orchestrator Agent (ReAct)
    │
    ├── LLM analyzes intent
    │
    ├── Calls query_ops_agent(question)     ──▶ Foundry API ──▶ Ops Agent
    │   └── Returns ops response
    │
    └── Calls query_menu_agent(question)    ──▶ Foundry API ──▶ Menu Agent
        └── Returns menu response
```

The orchestrator calls sub-agents through the Foundry Responses API using `agent_reference` routing — the same protocol the .NET bot uses. This means sub-agents don't need to be directly network-accessible to the orchestrator.

---

## Streaming Architecture

The bot uses the Microsoft 365 Agents SDK's built-in streaming support:

```
ContosoAgent                    FoundryAgentService              Foundry API
     │                                │                              │
     │ QueueInformativeUpdate          │                              │
     │ ("Contacting Ops Agent…")       │                              │
     │──────────────────────▶ Teams    │                              │
     │                                │                              │
     │ StreamOpsAgentAsync()          │                              │
     │───────────────────────────────▶│                              │
     │                                │ POST /openai/responses       │
     │                                │  stream: true                │
     │                                │─────────────────────────────▶│
     │                                │                              │
     │                                │◀─── data: {output_text_delta}│
     │                        yield chunk                            │
     │◀──────────────────────────────│                              │
     │ QueueTextChunk(chunk)          │                              │
     │──────────────────────▶ Teams   │◀─── data: {output_text_delta}│
     │                                │                              │
     │◀──────────────────────────────│                              │
     │ QueueTextChunk(chunk)          │                              │
     │──────────────────────▶ Teams   │◀─── data: [DONE]            │
     │                                │                              │
     │ EndStreamAsync()               │                              │
     │──────────────────────▶ Teams   │                              │
     │                                │                              │
     │ TryBuildCard()                 │                              │
     │ SendActivityAsync(card)        │                              │
     │──────────────────────▶ Teams   │                              │
```

Key implementation details:

- **`IAsyncEnumerable<string>`**: `StreamAgentAsync` uses `yield return` to produce chunks as they arrive from the SSE stream
- **`HttpCompletionOption.ResponseHeadersRead`**: Ensures streaming starts without waiting for the full response body
- **`QueueTextChunk`**: Does not `await` — chunks are queued and flushed by the SDK's internal timer
- **`EndStreamAsync`**: Finalizes the streamed message, making it a permanent chat message
- **Timeout**: HttpClient has a 2-minute timeout to handle cold-start latency on hosted agents

---

## Image Handling Pipeline

Image processing uses a 4-layer fallback strategy to handle the various ways Teams delivers attachments:

### Layer 1: SDK Pipeline (`M365AttachmentDownloader`)

The SDK automatically downloads Teams attachments and populates `turnState.Temp.InputFiles` before route handlers fire. This is registered in `Program.cs`:

```csharp
builder.Services.AddSingleton<IList<IInputFileDownloader>>(sp =>
    [new M365AttachmentDownloader(
        sp.GetRequiredService<IConnections>(),
        sp.GetRequiredService<IHttpClientFactory>())]);
```

### Layer 2: Authenticated Fallback Download

If the SDK returns empty `InputFiles`, the bot manually downloads from `Activity.Attachments`:

- **Inline images** (`ContentType=image/*`): Uses `ContentUrl` with Bearer token auth (Skype CDN requires bot auth)
- **File attachments** (`application/vnd.microsoft.teams.file.download.info`): Extracts pre-signed `downloadUrl` from `Content` JSON

### Layer 3: Anonymous Fallback

If authenticated download fails (HTTP error), retries the same URL without auth headers — works for pre-signed URLs.

### Layer 4: Text-Only Fallback

If all download attempts fail, proceeds with text-only processing — the bot doesn't crash.

### Vision Analysis

Successfully downloaded images are sent to the GPT vision model via the Responses API:

```json
{
  "model": "gpt-4o-mini",
  "input": [{
    "role": "user",
    "content": [
      { "type": "input_text", "text": "Describe this image in the context of Contoso Restaurants..." },
      { "type": "input_image", "image_url": "data:image/png;base64,..." }
    ]
  }],
  "max_output_tokens": 300
}
```

The resulting description is appended to the user's message text before routing to the appropriate agent.

---

## Adaptive Cards

### Detection & Generation Flow

```
Agent response text
       │
       ▼
TryBuildCard(response, agentType)
       │
       ├── agentType == "Ops"
       │     └── ExtractMetrics(response)
       │           ├── Regex: **Label**: Value
       │           ├── Regex: - Label: Value (bullets)
       │           ├── Regex: Label: $1,234 (numeric)
       │           └── Filter: label ≤ 50 chars, value ≤ 120 chars
       │           │
       │           ├── metrics.Count < 1 → null (no card)
       │           └── metrics found → Build FactSet card
       │                 ├── 📊 Header: "Operations Dashboard"
       │                 ├── Subtitle: "Contoso Restaurants · {date}"
       │                 ├── FactSet with label/value pairs
       │                 └── Footer: "Powered by Contoso Ops Agent"
       │
       └── agentType == "Menu & Marketing"
             └── Regex: emoji + **Name** — pitch
                   ├── No match → null (no card)
                   └── Match found → Build creative brief card
                         ├── DetectBrand() → brand emoji
                         ├── Header: concept name + brand
                         ├── Concept pitch (bold)
                         ├── ✅ Bullet points (up to 4)
                         └── Footer: "Powered by Contoso Menu & Marketing Agent"
```

### Card Schema

Both card types use Adaptive Card schema version 1.5 with:
- `ColumnSet` header with emoji + title
- Separator divider
- Content body (`FactSet` for Ops, `TextBlock` list for Menu)
- Subtle footer with agent attribution

Cards are sent as a **follow-up message** after `EndStreamAsync()`, not embedded in the streamed message.

---

## Hosted Agent Architecture

### Foundry Adapter Pattern

Hosted Agents are framework-agnostic containers. Each framework has a corresponding adapter package that wraps your agent in a Responses API-compatible HTTP server. The two patterns used in this template:

**LangGraph adapter** (orchestrator + ops agent):

```python
from azure.ai.agentserver.langgraph import from_langgraph

# 1. Define state and build graph
class AgentState(TypedDict):
    messages: Annotated[Sequence[BaseMessage], add_messages]
    reasoning_steps: list[str]

workflow = StateGraph(AgentState)
workflow.add_node("reasoning", reasoning_node)
workflow.set_entry_point("reasoning")
workflow.add_conditional_edges("reasoning", should_continue)
graph = workflow.compile()

# 2. Wrap with Foundry adapter and run
hosted_agent = from_langgraph(graph)
hosted_agent.run()
```

**Agent Framework adapter** (menu agent):

```python
from agent_framework import Agent
from agent_framework.azure import AzureOpenAIChatClient
from azure.identity import DefaultAzureCredential
from azure.ai.agentserver.agentframework import from_agent_framework

# 1. Create agent
client = AzureOpenAIChatClient(
    credential=DefaultAzureCredential(),
    endpoint=os.environ["AZURE_OPENAI_ENDPOINT"],
    deployment_name=os.environ["AZURE_AI_MODEL_DEPLOYMENT_NAME"],
)
agent = Agent(client=client, name="ContosoMenuAgent", instructions=SYSTEM_PROMPT)

# 2. Wrap with Foundry adapter and run
hosted_agent = from_agent_framework(agent)
hosted_agent.run()
```

Both adapters produce identical HTTP servers — the framework is an implementation detail invisible to callers.

### Container Structure

```
Python 3.12-slim container
├── main.py            # Agent logic + from_langgraph() or from_agent_framework()
├── requirements.txt   # Framework-specific dependencies
└── Exposed on :8088
    ├── POST /responses          # Responses API endpoint
    ├── GET  /readiness          # Health check
    └── GET  /liveness           # Liveness probe
```

### Agent Invocation Protocol

The bot calls hosted agents using the `agent_reference` pattern:

```json
POST {projectEndpoint}/openai/responses?api-version=2025-11-15-preview

{
  "input": [{ "role": "user", "content": "How are Southwest stores performing?" }],
  "agent": { "type": "agent_reference", "name": "ContosoOpsAgent", "version": "1" },
  "stream": true
}
```

Foundry routes this to the correct container based on the agent name and version.

---

## Authentication Model

Three distinct authentication chains operate in this system:

### Chain 1: Teams → Bot Service → Bot

```
Teams Client
  │
  ▼ (Azure AD JWT — automatic, tied to manifest botId)
Azure Bot Service
  │
  ▼ (JwtTokenValidation — Audiences + TenantId check)
ContosoAgent (ASP.NET)
```

Configuration in `appsettings.json`:
```json
{
  "TokenValidation": {
    "Audiences": ["<BOT_APP_ID>"],
    "TenantId": "<TENANT_ID>"
  },
  "Connections": {
    "ServiceConnection": {
      "Settings": {
        "AuthType": "ClientSecret",
        "ClientId": "<BOT_APP_ID>",
        "ClientSecret": "<BOT_APP_SECRET>"
      }
    }
  }
}
```

### Chain 2: Bot → Foundry API

```
ContosoAgent
  │
  ▼ DefaultAzureCredential
  │  ├── Development: Azure CLI credentials
  │  └── Production:  Managed Identity (Container Apps)
  │
  ▼ Bearer token (scope: https://ai.azure.com/.default)
Foundry Responses API
```

### Chain 3: Hosted Agents → Azure OpenAI

```
Foundry Hosted Agent (Container)
  │
  ▼ DefaultAzureCredential
  │  └── Managed Identity (Foundry-managed)
  │
  ▼ Token provider (scope: https://cognitiveservices.azure.com/.default)
Azure OpenAI (AzureChatOpenAI / AzureOpenAIChatClient)
```

---

## Infrastructure Automation

### azd Lifecycle

```
azd provision
  │
  ├── Deploy Bicep modules:
  │   ├── ai.bicep             → AI Services, Foundry project, gpt-4o-mini deployment
  │   ├── acr.bicep            → Container Registry (Basic SKU)
  │   ├── container-app.bicep  → Container Apps environment, app, Log Analytics
  │   └── bot-service.bicep    → Bot Service (F0), Teams channel registration
  │
  └── Post-provision hook (postprovision.ps1):
      └── Register Capability Host for Foundry project

azd deploy
  │
  ├── Build & push bot container to ACR
  ├── Update Container App with new image
  │
  └── Post-deploy hook (postdeploy.ps1):
      ├── docker build agents/ops-agent → push to ACR
      ├── docker build agents/menu-agent → push to ACR
      └── python deploy_hosted_agents.py
          ├── client.agents.create_version("ContosoOpsAgent", ...)
          └── client.agents.create_version("ContosoMenuAgent", ...)
```

### Bicep Module Responsibilities

| Module | Resources Created |
|--------|-------------------|
| `ai.bicep` | `Microsoft.CognitiveServices/accounts`, Foundry project, model deployment |
| `acr.bicep` | `Microsoft.ContainerRegistry/registries` (Basic SKU) |
| `container-app.bicep` | `Microsoft.App/managedEnvironments`, `Microsoft.App/containerApps`, `Microsoft.OperationalInsights/workspaces` |
| `bot-service.bicep` | `Microsoft.BotService/botServices` (F0 + SingleTenant), Teams channel |

### Naming Conventions

Resource names follow the abbreviations defined in `infra/abbreviations.json` combined with the azd environment name, ensuring uniqueness and consistency across deployments.

---

## Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| **Single bot, multi-agent** | One Bot Service registration handles both agents — reduces Teams app sprawl and simplifies identity management |
| **LLM orchestrator routing** | ReAct tool-calling agent replaces brittle keyword matching — handles ambiguous queries, greetings, and multi-domain questions naturally |
| **SSE streaming relay** | Native Teams streaming UX via the SDK, not polling — lower latency, progressive rendering |
| **Follow-up cards (not inline)** | Adaptive Cards can't be embedded in a streamed message — they are sent as a separate activity after `EndStreamAsync()` |
| **DefaultAzureCredential everywhere** | Works with `az login` during development and Managed Identity in production — no secret management needed for Foundry calls |
| **4-layer image download** | Teams delivers images via multiple mechanisms depending on how users attach them — the fallback chain handles all cases |
| **Framework-specific adapters** | `from_langgraph()` and `from_agent_framework()` wrap agents in the Responses API protocol without manual HTTP server code — proves the Hosted Agent abstraction is framework-agnostic |
| **Multi-framework demonstration** | Using LangGraph and Agent Framework side-by-side shows that the Responses API contract is the boundary, not the framework. Swap frameworks per-agent without changing infrastructure or bot code |
| **Responses API (not Assistants)** | Hosted Agents use the newer Responses API pattern with `agent_reference` routing, not the Assistants/Threads model |
