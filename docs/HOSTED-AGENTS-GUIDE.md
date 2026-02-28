# Microsoft Foundry Hosted Agents Guide

> Understanding and working with Microsoft Foundry Hosted Agents in the Contoso Multi-Agent Teams Hub

---

## Table of Contents

- [What Are Hosted Agents?](#what-are-hosted-agents)
- [How They Work With This Template](#how-they-work-with-this-template)
- [The LangGraph Adapter Pattern](#the-langgraph-adapter-pattern)
- [Creating a New Hosted Agent](#creating-a-new-hosted-agent)
- [Deployment Process](#deployment-process)
- [Debugging Agents Locally](#debugging-agents-locally)
- [Monitoring and Logs](#monitoring-and-logs)

---

## What Are Hosted Agents?

Microsoft Foundry **Hosted Agents** are containerized AI agents that run inside the Microsoft Foundry Agent Service. They expose a standardized API (the **Responses API**) so any client can invoke them with a consistent protocol, regardless of the agent's internal framework (LangGraph, Semantic Kernel, AutoGen, etc.).

### Key Characteristics

| Aspect | Details |
|--------|---------|
| **Packaging** | Docker container with your agent code |
| **Protocol** | Responses API — OpenAI-compatible `/responses` endpoint |
| **Hosting** | Managed by Microsoft Foundry — you don't manage VMs or Kubernetes |
| **Identity** | Runs with Managed Identity — automatic Azure resource access |
| **Invocation** | Via `agent_reference` in a Responses API call to your Microsoft Foundry project |
| **Scaling** | Configurable min/max replicas in `agent.yaml` |
| **Versioning** | Each deployment creates a numbered version — clients reference by name + version |

### How Hosted Agents Differ From Other Approaches

```
┌─────────────────────────────────────────────────────────────────┐
│              Ways to Build Agents in Microsoft Foundry           │
├──────────────────┬──────────────────┬───────────────────────────┤
│  Code-first      │  Managed Agent   │  Hosted Agent             │
│  (your server)   │  (Microsoft      │  (this template)          │
│                  │   Foundry UI)    │                            │
├──────────────────┼──────────────────┼───────────────────────────┤
│  You host the    │  Microsoft       │  Microsoft Foundry hosts  │
│  HTTP server     │  Foundry hosts   │  your Docker container    │
│  (App Service,   │  a managed agent │  with custom code          │
│  Container Apps) │  configured via  │                            │
│                  │  portal/SDK      │                            │
│  Full control    │  Easy setup,     │  Custom logic +            │
│  Maximum effort  │  limited custom  │  managed hosting           │
└──────────────────┴──────────────────┴───────────────────────────┘
```

Hosted Agents give you the best of both worlds: **custom agent logic** (any framework, any tools) with **managed hosting** (no infrastructure to maintain).

---

## How They Work With This Template

In this template, hosted agents serve as the **backend AI brains** behind the Teams bot:

```
Teams User
  │
  ▼
.NET Bot (ContosoAgent.cs)
  │  Routes message based on /ops, /menu, or keywords
  │
  ▼
FoundryAgentService.cs
  │  POST {project}/openai/responses
  │  Body: { agent: { type: "agent_reference", name: "ContosoOpsAgent", version: "1" },
  │          input: [{ role: "user", content: "..." }],
  │          stream: true }
  │
  ▼
Microsoft Foundry Responses API
  │  Looks up "ContosoOpsAgent" version "1"
  │  Routes to the corresponding container
  │
  ▼
Hosted Agent Container (Python)
  │  Receives the user message
  │  Runs LangGraph workflow
  │  Streams response back via SSE
  │
  ▼
FoundryAgentService.cs
  │  Reads SSE chunks, yields via IAsyncEnumerable
  │
  ▼
ContosoAgent.cs
  │  QueueTextChunk() → Teams renders progressively
  │  EndStreamAsync() → finalizes message
  │  TryBuildCard() → sends Adaptive Card follow-up
```

### Configuration

The bot references agents by name and version in `appsettings.json`:

```json
{
  "Foundry": {
    "ProjectEndpoint": "https://<account>.services.ai.azure.com/api/projects/<project>",
    "OpsAgentName": "ContosoOpsAgent",
    "OpsAgentVersion": "1",
    "MenuAgentName": "ContosoMenuAgent",
    "MenuAgentVersion": "1"
  }
}
```

---

## The LangGraph Adapter Pattern

The `azure-ai-agentserver-langgraph` package provides a bridge between LangGraph and the Microsoft Foundry Responses API protocol.

### How `from_langgraph()` Works

```python
from azure.ai.agentserver.langgraph import from_langgraph

# Your LangGraph graph
graph = build_graph()

# Wrap it — this creates an HTTP server that:
#   1. Accepts POST /responses with Responses API format
#   2. Translates input messages → LangGraph state
#   3. Runs the graph
#   4. Translates graph output → Responses API response
#   5. Supports streaming (SSE) if stream=true
hosted_agent = from_langgraph(graph)

# Start the server on port 8088
hosted_agent.run()
```

### What the Adapter Handles

| Concern | Handled By |
|---------|-----------|
| HTTP server | `uvicorn` (ASGI) via the adapter |
| Request parsing | Adapter translates Responses API → LangGraph messages |
| Response formatting | Adapter translates LangGraph output → Responses API JSON |
| Streaming | Adapter converts LangGraph streaming into SSE `data:` events |
| Health checks | Adapter exposes `/readiness` and `/liveness` endpoints |
| Protocol version | Declares `responses/v1` protocol compatibility |

### State Mapping

The adapter maps between Responses API and LangGraph:

```
Responses API Input                    LangGraph State
─────────────────                      ───────────────
{                                      {
  "input": [                             "messages": [
    { "role": "user",          →           HumanMessage(
      "content": "..." }                     content="..."
  ]                                        )
}                                        ]
                                       }

LangGraph State Output                 Responses API Response
──────────────────────                 ──────────────────────
{                                      {
  "messages": [                          "output": [
    AIMessage(                   →         { "type": "message",
      content="..."                          "content": [
    )                                          { "type": "output_text",
  ]                                              "text": "..." }
}                                            ] }
                                         ]
                                       }
```

---

## Creating a New Hosted Agent

### Step-by-Step

#### 1. Create Directory Structure

```bash
mkdir agents/my-agent
```

#### 2. Write `main.py`

```python
"""My Agent - LangGraph Hosted Agent"""

import os
import logging
from typing import TypedDict, Annotated, Sequence
from langchain_core.messages import BaseMessage, SystemMessage
from langgraph.graph import StateGraph, END
from langgraph.graph.message import add_messages
from azure.ai.agentserver.langgraph import from_langgraph
from langchain_openai import AzureChatOpenAI

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

# ── System prompt ──────────────────────────────────────────
SYSTEM_PROMPT = """You are a helpful assistant that..."""

# ── State ──────────────────────────────────────────────────
class AgentState(TypedDict):
    messages: Annotated[Sequence[BaseMessage], add_messages]

# ── Environment ────────────────────────────────────────────
AZURE_OPENAI_ENDPOINT = (
    os.environ.get("AZURE_OPENAI_ENDPOINT")
    or os.environ.get("AZURE_AI_ENDPOINT")
)
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
        streaming=True,
    )

# ── Graph node ─────────────────────────────────────────────
def respond(state: AgentState) -> AgentState:
    llm = create_llm()
    messages = [SystemMessage(content=SYSTEM_PROMPT)] + list(state["messages"])
    response = llm.invoke(messages)
    return {"messages": [response]}

# ── Build graph ────────────────────────────────────────────
workflow = StateGraph(AgentState)
workflow.add_node("respond", respond)
workflow.set_entry_point("respond")
workflow.add_edge("respond", END)
graph = workflow.compile()

# ── Hosted agent ───────────────────────────────────────────
hosted_agent = from_langgraph(graph)

if __name__ == "__main__":
    hosted_agent.run()
```

#### 3. Write `requirements.txt`

```
langgraph>=0.2.0
langchain>=0.3.0
langchain-core>=0.3.0
langchain-openai>=0.2.0
langchain-azure-ai>=1.0.0
azure-ai-agentserver-langgraph>=1.0.0b10
azure-ai-agentserver-core>=1.0.0b1
azure-identity>=1.15.0
uvicorn[standard]>=0.30.0
python-dotenv>=1.0.0
```

#### 4. Write `Dockerfile`

```dockerfile
FROM python:3.12-slim
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY main.py .

EXPOSE 8088

HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:8088/readiness || exit 1

CMD ["python", "main.py"]
```

#### 5. Write `agent.yaml`

```yaml
name: my-agent
displayName: My Custom Agent
description: A custom agent for ...

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

#### 6. Test Locally (see [Debugging](#debugging-agents-locally))

#### 7. Deploy (see [Deployment Process](#deployment-process))

#### 8. Wire into the Bot (see [CUSTOMIZATION.md — Adding a New Agent](CUSTOMIZATION.md#adding-a-new-agent))

---

## Deployment Process

### Overview

```
Source Code → Docker Build → ACR Push → SDK create_version → Capability Host
```

### Detailed Steps

#### 1. Docker Build

Build the agent container image:

```bash
cd agents/ops-agent
docker build -t contoso-ops-agent:v1 .
```

#### 2. ACR Push

Tag and push to your Azure Container Registry:

```bash
# Login to ACR
az acr login --name <your-acr-name>

# Tag
docker tag contoso-ops-agent:v1 <your-acr>.azurecr.io/contoso-ops-agent:v1

# Push
docker push <your-acr>.azurecr.io/contoso-ops-agent:v1
```

#### 3. SDK Version Create

Use the `azure-ai-projects` SDK to register the agent version:

```python
from azure.ai.projects import AIProjectClient
from azure.ai.projects.models import (
    ImageBasedHostedAgentDefinition,
    ProtocolVersionRecord,
    AgentProtocol,
)
from azure.identity import DefaultAzureCredential

client = AIProjectClient(
    endpoint="https://<account>.services.ai.azure.com/api/projects/<project>",
    credential=DefaultAzureCredential()
)

agent = client.agents.create_version(
    agent_name="ContosoOpsAgent",
    definition=ImageBasedHostedAgentDefinition(
        container_protocol_versions=[
            ProtocolVersionRecord(protocol=AgentProtocol.RESPONSES, version="v1")
        ],
        cpu="1",
        memory="2Gi",
        image="<your-acr>.azurecr.io/contoso-ops-agent:v1",
        environment_variables={
            "AZURE_AI_PROJECT_ENDPOINT": "...",
            "AZURE_AI_MODEL_DEPLOYMENT_NAME": "gpt-4o-mini",
            "AZURE_AI_ENDPOINT": "...",
        }
    )
)
print(f"Created version: {agent.version}")
```

This is automated by `agents/deploy_hosted_agents.py`.

#### 4. Capability Host Registration

The Microsoft Foundry project needs a Capability Host to route `agent_reference` requests. This is handled by `scripts/postprovision.ps1` during `azd provision`.

### Automated Deployment (`azd deploy`)

The `scripts/postdeploy.ps1` hook automates steps 1–3:
1. Builds both agent images
2. Pushes to ACR
3. Runs `deploy_hosted_agents.py` to create versions

---

## Debugging Agents Locally

### Run the Agent Standalone

```bash
cd agents/ops-agent

# Set required environment variables
export AZURE_OPENAI_ENDPOINT="https://<account>.services.ai.azure.com/"
export AZURE_AI_MODEL_DEPLOYMENT_NAME="gpt-4o-mini"
export OPENAI_API_VERSION="2025-03-01-preview"

# Ensure you're logged in (for DefaultAzureCredential)
az login

# Run
python main.py
```

The agent starts on `http://localhost:8088`.

### Test with curl

```bash
# Non-streaming
curl -X POST http://localhost:8088/responses \
  -H "Content-Type: application/json" \
  -d '{
    "input": [{"role": "user", "content": "What are today'\''s store metrics?"}]
  }'

# Streaming
curl -X POST http://localhost:8088/responses \
  -H "Content-Type: application/json" \
  -H "Accept: text/event-stream" \
  -d '{
    "input": [{"role": "user", "content": "What are today'\''s store metrics?"}],
    "stream": true
  }'
```

### Test with Docker

```bash
cd agents/ops-agent

docker build -t contoso-ops-agent:dev .

docker run -it --rm \
  -p 8088:8088 \
  -e AZURE_OPENAI_ENDPOINT="https://..." \
  -e AZURE_AI_MODEL_DEPLOYMENT_NAME="gpt-4o-mini" \
  -e OPENAI_API_VERSION="2025-03-01-preview" \
  contoso-ops-agent:dev
```

> **Note**: `DefaultAzureCredential` inside Docker won't have access to your `az login` session. For local Docker testing, you may need to mount Azure CLI credentials or use a service principal.

### Health Checks

```bash
# Readiness (is the agent ready to accept requests?)
curl http://localhost:8088/readiness

# Liveness (is the process alive?)
curl http://localhost:8088/liveness
```

---

## Monitoring and Logs

### Container Logs (Microsoft Foundry Portal)

1. Go to [Microsoft Foundry](https://ai.azure.com)
2. Navigate to your project → **Agents**
3. Select the agent → **Logs** tab

### Container Logs (Azure CLI)

If you know the underlying Container Apps resource:

```bash
az containerapp logs show \
  --name <container-app-name> \
  --resource-group <rg-name> \
  --follow
```

### Log Analytics

The bot's Container App is connected to a Log Analytics workspace. Query logs with KQL:

```kql
ContainerAppConsoleLogs_CL
| where ContainerName_s == "contoso-ops-agent"
| where TimeGenerated > ago(1h)
| project TimeGenerated, Log_s
| order by TimeGenerated desc
```

### Application-Level Logging

Both agents use Python's `logging` module at `INFO` level. Key log points:

| Log Message | Meaning |
|-------------|---------|
| `reasoning_node called` / `creative_node called` | Agent received a request |
| `LLM created successfully` | Model connection established |
| `Invoking LLM with N messages` | Request sent to Azure OpenAI |
| `LLM response received` | Model responded successfully |
| `Error in reasoning_node: ...` | Agent-level error (model failure, auth issue, etc.) |

### Bot Logging

The .NET bot logs at `Information` level with structured logging:

| Logger | Log Pattern | Meaning |
|--------|-------------|---------|
| `FoundryAgentService` | `Starting streaming invocation of agent {AgentName}` | SSE stream opened |
| `FoundryAgentService` | `Stream completed for agent {AgentName}` | SSE stream finished (`[DONE]`) |
| `ContosoAgent` | `Streamed {Length} chars from {Agent} agent` | Total response size |
| `ContosoAgent` | `Sent Adaptive Card for {Agent} response` | Card generated and sent |
| `ImageAnalysisService` | `Analyzing {Count} pre-downloaded image file(s)` | Vision pipeline active |

Adjust log levels in `appsettings.json`:
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "ContosoAgentBot": "Trace",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```
