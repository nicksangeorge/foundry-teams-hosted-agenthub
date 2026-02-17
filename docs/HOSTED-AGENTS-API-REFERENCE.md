# Azure AI Foundry Hosted Agents — Complete API Reference & Fix Plan

Date: 2026-02-17
Sources: Microsoft Learn docs (fetched 2026-02-17), workspace learnings, exhaustive REST API testing

---

## 1. ALL REST API ENDPOINTS (Data-Plane)

Base URL: `https://{account}.services.ai.azure.com/api/projects/{project}`
Auth: Bearer token with audience `https://ai.azure.com`
API version query param: `api-version=2025-11-15-preview`

### Agent CRUD

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/agents?api-version=2025-11-15-preview` | List all agents in the project |
| `GET` | `/agents/{agentName}?api-version=2025-11-15-preview` | Get agent details (includes `versions.latest`) |
| `POST` | `/agents/{agentName}?api-version=2025-11-15-preview` | Create a new agent version (body = `ImageBasedHostedAgentDefinition`) |
| `DELETE` | `/agents/{agentName}?api-version=2025-11-15-preview` | Delete agent (all versions). Fails if active containers exist. |
| `DELETE` | `/agents/{agentName}/versions/{version}?api-version=2025-11-15-preview` | Delete specific version. Fails if deployment is running. |

### Agent Version Management

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/agents/{agentName}/versions?api-version=2025-11-15-preview` | List all versions of an agent |
| `GET` | `/agents/{agentName}/versions/{version}?api-version=2025-11-15-preview` | Get specific version details |

### Container Log Streaming

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/agents/{agentName}/versions/{version}/containers/default:logstream?kind=console&tail=500&api-version=2025-11-15-preview` | Stream stdout/stderr |
| `GET` | `/agents/{agentName}/versions/{version}/containers/default:logstream?kind=system&tail=500&api-version=2025-11-15-preview` | Stream container app system events |

Query params for logstream:
- `kind`: `console` (stdout/stderr) or `system` (container app events). Default: `console`
- `replica_name`: omit for first replica, specify for a specific one
- `tail`: 1-300 trailing lines. Default: 20
- Timeouts: max connection 10 min, idle 1 min

### Agent Invocation

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/openai/responses?api-version=2025-11-15-preview` | Invoke a hosted agent via Responses API |

Body:
```json
{
  "input": [{"role": "user", "content": "..."}],
  "agent": {"type": "agent_reference", "name": "AgentName", "version": "1"},
  "stream": true
}
```

### Start/Stop — DATA-PLANE STATUS (NOT WORKING)

These endpoints were documented conceptually but **do not function** on the `2025-11-15-preview` data-plane:

| Pattern Tested | Result |
|----------------|--------|
| `POST /agents/{name}:start` | 400 — colon treated as part of agent name |
| `POST /agents/{name}/versions/{ver}:start` | 405 Method Not Allowed |
| `POST /agents/{name}/versions/{ver}/start` | 404 Not Found |

**Start/stop is CLI-only or portal-only as of Feb 2026.**

---

## 2. ALL CLI COMMANDS (az cognitiveservices agent)

Requires: Azure CLI >= 2.80, `cognitiveservices` extension.

### Start
```bash
az cognitiveservices agent start \
  --account-name <account> --project-name <project> \
  --name <agentName> --agent-version <ver> \
  [--min-replicas 1] [--max-replicas 2]
```
State: Stopped -> Starting -> Started (or Failed)

### Stop
```bash
az cognitiveservices agent stop \
  --account-name <account> --project-name <project> \
  --name <agentName> --agent-version <ver>
```
State: Running -> Stopping -> Stopped (or Running if failed)

### Update (non-versioned, no new version)
```bash
az cognitiveservices agent update \
  --account-name <account> --project-name <project> \
  --name <agentName> --agent-version <ver> \
  [--min-replicas N] [--max-replicas M] [--description "..."] [--tags key=value]
```

### Delete deployment only (keeps version definition)
```bash
az cognitiveservices agent delete-deployment \
  --account-name <account> --project-name <project> \
  --name <agentName> --agent-version <ver>
```

### Delete agent version
```bash
az cognitiveservices agent delete \
  --account-name <account> --project-name <project> \
  --name <agentName> --agent-version <ver>
```
**Fails if deployment is running. Stop first.**

### Delete agent (all versions)
```bash
az cognitiveservices agent delete \
  --account-name <account> --project-name <project> \
  --name <agentName>
```

### List versions
```bash
az cognitiveservices agent list-versions \
  --account-name <account> --project-name <project> \
  --name <agentName>
```

### Show details
```bash
az cognitiveservices agent show \
  --account-name <account> --project-name <project> \
  --name <agentName>
```

### Create (versioned, via CLI)
```bash
az cognitiveservices agent create \
  --account-name <account> --project-name <project> \
  --name <agentName> ...
```
See `az cognitiveservices agent create --help` for full params.

---

## 3. ALL SDK METHODS (azure-ai-projects >= 2.0.0b3)

```python
from azure.ai.projects import AIProjectClient
from azure.ai.projects.models import (
    ImageBasedHostedAgentDefinition,
    ProtocolVersionRecord,
    AgentProtocol,
    AgentReference,
)
from azure.identity import DefaultAzureCredential

client = AIProjectClient(endpoint=PROJECT_ENDPOINT, credential=DefaultAzureCredential())

# Create version (POST /agents/{name})
agent = client.agents.create_version(
    agent_name="AgentName",
    definition=ImageBasedHostedAgentDefinition(
        container_protocol_versions=[ProtocolVersionRecord(protocol=AgentProtocol.RESPONSES, version="v1")],
        cpu="1", memory="2Gi",
        image="acr.azurecr.io/image:tag",
        environment_variables={...}
    )
)

# Get agent (GET /agents/{name})
agent = client.agents.get(agent_name="AgentName")

# List agents (GET /agents)
agents = client.agents.list()

# Delete version (DELETE /agents/{name}/versions/{ver})
client.agents.delete_version(agent_name="AgentName", agent_version="1")

# Invoke via OpenAI client
openai_client = client.get_openai_client()
response = openai_client.responses.create(
    input=[{"role": "user", "content": "Hello"}],
    extra_body={"agent": AgentReference(name="AgentName", version="1").as_dict()}
)
```

### SDK does NOT have:
- `start()` / `stop()` / `update()` / `delete_deployment()` methods
- `min_replicas` / `max_replicas` in `ImageBasedHostedAgentDefinition` constructor

### Workaround for min/max replicas:
```python
defn = ImageBasedHostedAgentDefinition(...)
defn["min_replicas"] = 1  # MutableMapping injection
defn["max_replicas"] = 2
agent = client.agents.create_version(agent_name="...", definition=defn)
```

---

## 4. ARM MANAGEMENT PLANE ENDPOINTS

Base URL: `https://management.azure.com`

### Capability Host
```
PUT /subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.CognitiveServices/accounts/{account}/capabilityHosts/{name}?api-version=2025-10-01-preview
GET /subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.CognitiveServices/accounts/{account}/capabilityHosts/{name}?api-version=2025-10-01-preview
DELETE /subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.CognitiveServices/accounts/{account}/capabilityHosts/{name}?api-version=2025-10-01-preview
```

Body for PUT:
```json
{
  "properties": {
    "capabilityHostKind": "Agents",
    "enablePublicHostingEnvironment": true
  }
}
```

Capability hosts CANNOT be updated. Delete and recreate to change properties.

### Project (for querying agent identity)
```
GET /subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.CognitiveServices/accounts/{account}/projects/{project}?api-version=2025-04-01-preview
```
Response path: `properties.agentIdentity.agentIdentityId` -> service principal object ID for RBAC.

---

## 5. CURRENT STATE (from workspace docs, as of 2026-02-17)

### Environment: `rg-contoso-agents-e2e` (re-provisioned as `contoso-agents-v2`)
- AI Account: `ai-g3ry5s6ib3wye`
- Project: `project-g3ry5s6ib3wye`
- ACR: `crg3ry5s6ib3wye.azurecr.io`

### Agent State
| Agent | Working Version | Container Status | Issue |
|-------|----------------|-----------------|-------|
| ContosoOpsAgent | v1 | Running/Started | **WORKS** — responds correctly |
| ContosoMenuAgent | v1 | Running/Started | **BROKEN** — times out at 100s. `agent-framework` + `AzureOpenAIChatClient` hangs. Old code still running. |
| ContosoOrchestratorAgent | v1 | Running/Started | **BROKEN** — cascading timeout because it calls MenuAgent as sub-agent |

### Root Issues
1. **ContosoMenuAgent v1 container runs stale code** — the `agent-framework` library has a compatibility issue with `DefaultAzureCredential` inside managed containers. Image was rebuilt in ACR under the same `:v1` tag, but the running container still uses the old image.
2. **Creating new versions (v2, v3) does not provision containers** — `create_version()` registers the definition but does not auto-start. The `:start` REST endpoint doesn't work on the data-plane API.
3. **Can't delete old versions** — 409 Conflict: "active associated hosted containers". The deployment must be stopped first.
4. **Can't stop via REST** — `:stop` endpoint not functional on data-plane.
5. **CLI `az cognitiveservices agent start/stop` requires CLI >= 2.80** — current install is 2.77.0.

---

## 6. RECOMMENDED FIX — RANKED BY LIKELIHOOD OF SUCCESS

### Option A: Upgrade Azure CLI to >= 2.80 (BEST PATH)

This unlocks all lifecycle commands. Then:

```bash
# 1. Upgrade CLI
az upgrade

# 2. Add/update the extension
az extension add --name cognitiveservices --upgrade

# 3. Stop the broken v1 deployments
az cognitiveservices agent stop \
  --account-name ai-g3ry5s6ib3wye --project-name project-g3ry5s6ib3wye \
  --name ContosoMenuAgent --agent-version 1

az cognitiveservices agent stop \
  --account-name ai-g3ry5s6ib3wye --project-name project-g3ry5s6ib3wye \
  --name ContosoOrchestratorAgent --agent-version 1

# 4. Delete old deployments (optional, keeps versions)
az cognitiveservices agent delete-deployment \
  --account-name ai-g3ry5s6ib3wye --project-name project-g3ry5s6ib3wye \
  --name ContosoMenuAgent --agent-version 1

az cognitiveservices agent delete-deployment \
  --account-name ai-g3ry5s6ib3wye --project-name project-g3ry5s6ib3wye \
  --name ContosoOrchestratorAgent --agent-version 1

# 5. Create new versions (v2) with updated images
python agents/deploy_hosted_agents.py  # or SDK directly

# 6. Start the new versions
az cognitiveservices agent start \
  --account-name ai-g3ry5s6ib3wye --project-name project-g3ry5s6ib3wye \
  --name ContosoMenuAgent --agent-version 2 \
  --min-replicas 1 --max-replicas 2

az cognitiveservices agent start \
  --account-name ai-g3ry5s6ib3wye --project-name project-g3ry5s6ib3wye \
  --name ContosoOrchestratorAgent --agent-version 2 \
  --min-replicas 1 --max-replicas 2

# 7. Update bot Container App env vars to point to v2
az containerapp update --name <bot-app> --resource-group <rg> \
  --set-env-vars Foundry__MenuAgentVersion=2 Foundry__OrchestratorAgentVersion=2
```

### Option B: Foundry Portal UI

If CLI upgrade is blocked:
1. Go to https://ai.azure.com -> your project -> Agents
2. **Stop** ContosoMenuAgent v1 and ContosoOrchestratorAgent v1 via the portal UI
3. Use SDK `create_version()` to create v2 with the rebuilt images
4. **Start** the new v2 versions via the portal UI "Start agent deployment" button
5. Update bot env vars to point to v2

### Option C: Delete and recreate agents entirely

Nuclear option if stop/delete-deployment fails:

```bash
# Requires CLI >= 2.80
az cognitiveservices agent delete \
  --account-name ai-g3ry5s6ib3wye --project-name project-g3ry5s6ib3wye \
  --name ContosoMenuAgent

az cognitiveservices agent delete \
  --account-name ai-g3ry5s6ib3wye --project-name project-g3ry5s6ib3wye \
  --name ContosoOrchestratorAgent
```

Then recreate with `create_version()` + `start`.

### Option D: `azd up` with `host: azure.ai.agent`

If the `azd ai agent` extension (v0.1.8-preview) is installed, `azd up` handles the full lifecycle including start. Modify `azure.yaml`:

```yaml
services:
  ContosoMenuAgent:
    project: agents/menu-agent
    host: azure.ai.agent
    language: docker
    docker:
      remoteBuild: true
    config:
      container:
        scale:
          minReplicas: 1
          maxReplicas: 2
```

Then `azd up` builds, pushes, creates version, AND starts. This bypasses the CLI version requirement entirely.

### Option E: Force container refresh via image digest

If the running container uses a cached image layer:
1. Rebuild with a new tag (`:v2` instead of `:v1`)
2. Create a new agent version pointing to the new tag
3. Use portal to start the new version

The platform may not re-pull `:v1` if it has a cached layer from the first pull. A new tag forces a fresh pull.

---

## 7. CONTAINER LIFECYCLE — HOW IT WORKS

### Agent State Machine
```
                create_version()
                      |
                      v
    [Not Created] --> [Stopped] (0/0 replicas, definition registered)
                        |
                    start (CLI/portal/azd)
                        |
                        v
                   [Starting] (container pulling image, booting)
                        |
                   +---------+
                   |         |
                   v         v
              [Started]  [Failed]
              (running,   (check logs)
               healthy)
                   |
               stop (CLI/portal)
                   |
                   v
              [Stopping]
                   |
                   v
              [Stopped]
```

### Container provisioning internals
1. Foundry reads agent definition (image URL, cpu, memory, env vars, protocols)
2. Platform provisions an Azure Container Apps revision inside the managed ACA environment
3. Image is pulled from ACR using the **project's system-assigned managed identity** (needs `AcrPull` or `Container Registry Repository Reader`)
4. Container starts, hosting adapter boots on port 8088
5. Platform polls `GET /readiness` every ~30s
6. Once readiness returns 200, agent state transitions to "Started"
7. Agent is available via `/openai/responses` with `agent_reference`

### Why rebuilt images under same tag may not take effect
- ACA uses content-addressable image digests internally
- If the running revision was created with digest `sha256:abc...`, it keeps running that digest
- Pushing a new image with the same `:v1` tag creates a new digest, but the existing revision doesn't know about it
- A **new version** (or `delete-deployment` + `start`) is required to trigger a fresh image pull

---

## 8. CRITICAL GOTCHAS

1. **`create_version()` does NOT start containers.** It only registers the definition. You must separately start via CLI, portal, or `azd up`.

2. **`:start`/`:stop` REST endpoints are NOT available on the data-plane API** at `2025-11-15-preview`. These are CLI-only operations requiring `az cognitiveservices agent start/stop` with CLI >= 2.80.

3. **Can't delete an agent with running containers.** You get `409 Conflict: active associated hosted containers`. Must stop/delete-deployment first.

4. **Can't delete a running deployment via REST either.** `delete-deployment` is CLI-only.

5. **Capability hosts can't be updated.** Delete and recreate. Project-level capability hosts trigger ML Hub auto-creation which can be policy-blocked.

6. **Agent identity is separate from project MI.** Hosted containers run under the auto-generated agent identity (service principal). This identity needs `Cognitive Services OpenAI User` + `Azure AI Developer` on the AI account.

7. **Token audience is `https://ai.azure.com`** for data-plane APIs, NOT `https://cognitiveservices.azure.com`.

8. **Images must be `linux/amd64`.** ARM64 images fail silently with exec format errors.

9. **Same-tag image push doesn't refresh running containers.** Use a new tag or create a new version.

10. **SDK `ImageBasedHostedAgentDefinition` doesn't expose `min_replicas`/`max_replicas` in the constructor.** Use MutableMapping dict injection: `defn["min_replicas"] = 1`.
