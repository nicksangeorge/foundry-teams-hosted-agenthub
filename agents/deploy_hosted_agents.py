"""
Deploy Hosted Agents to Azure AI Foundry Agent Service.
Uses the azure-ai-projects SDK to create hosted agent versions,
then sets min_replicas via raw REST (SDK doesn't expose this field).

Set these environment variables before running:
  FOUNDRY_PROJECT_ENDPOINT  - Foundry project endpoint URL
  FOUNDRY_ACR               - Container registry hostname (e.g., myacr.azurecr.io)
  AI_ENDPOINT               - Azure AI endpoint URL
  MODEL_DEPLOYMENT          - Model deployment name (default: gpt-4o-mini)
"""
import os
import sys
import json
from azure.ai.projects import AIProjectClient
from azure.ai.projects.models import (
    ImageBasedHostedAgentDefinition,
    ProtocolVersionRecord,
    AgentProtocol,
)
from azure.identity import DefaultAzureCredential

ENDPOINT = os.environ.get("FOUNDRY_PROJECT_ENDPOINT")
ACR = os.environ.get("FOUNDRY_ACR")
AI_ENDPOINT = os.environ.get("AI_ENDPOINT")
MODEL_DEPLOYMENT = os.environ.get("MODEL_DEPLOYMENT", "gpt-4o-mini")

if not ENDPOINT or not ACR or not AI_ENDPOINT:
    print("ERROR: Set FOUNDRY_PROJECT_ENDPOINT, FOUNDRY_ACR, and AI_ENDPOINT environment variables")
    sys.exit(1)

def deploy_agent(client, agent_name, image, display_name, extra_env=None):
    """Create a hosted agent version with min_replicas=1 so it starts automatically.

    ImageBasedHostedAgentDefinition inherits MutableMapping, so we inject
    min_replicas / max_replicas via dict access after construction. The SDK
    serializes them into the REST body alongside the other fields.
    """
    print(f"\n--- Deploying {display_name} ---")
    print(f"  Image: {image}")
    print(f"  Model: {MODEL_DEPLOYMENT}")

    try:
        env_vars = {
            "AZURE_AI_PROJECT_ENDPOINT": ENDPOINT,
            "AZURE_AI_MODEL_DEPLOYMENT_NAME": MODEL_DEPLOYMENT,
            "AZURE_AI_ENDPOINT": AI_ENDPOINT,
            "FOUNDRY_PROJECT_ENDPOINT": ENDPOINT,
        }
        if extra_env:
            env_vars.update(extra_env)

        defn = ImageBasedHostedAgentDefinition(
            container_protocol_versions=[
                ProtocolVersionRecord(protocol=AgentProtocol.RESPONSES, version="v1")
            ],
            cpu="1",
            memory="2Gi",
            image=image,
            environment_variables=env_vars,
        )
        # Inject scaling fields — SDK constructor doesn't accept them,
        # but the REST API does and MutableMapping lets us set them.
        defn["min_replicas"] = 1
        defn["max_replicas"] = 2

        agent = client.agents.create_version(
            agent_name=agent_name,
            definition=defn
        )
        print(f"  SUCCESS: {agent_name} version {agent.version} created (min_replicas=1)")
        return agent
    except Exception as e:
        print(f"  ERROR: {e}")
        return None

def main():
    print("Connecting to Foundry project...")
    client = AIProjectClient(
        endpoint=ENDPOINT,
        credential=DefaultAzureCredential()
    )
    
    # Deploy Ops Agent
    ops = deploy_agent(
        client,
        agent_name="ContosoOpsAgent",
        image=f"{ACR}/contoso-ops-agent:v1",
        display_name="Contoso Ops Agent"
    )
    
    # Deploy Menu Agent
    menu = deploy_agent(
        client,
        agent_name="ContosoMenuAgent",
        image=f"{ACR}/contoso-menu-agent:v1",
        display_name="Contoso Menu & Marketing Agent"
    )
    
    # Deploy Orchestrator Agent — needs sub-agent version env vars
    orchestrator = deploy_agent(
        client,
        agent_name="ContosoOrchestratorAgent",
        image=f"{ACR}/contoso-orchestrator-agent:v1",
        display_name="Contoso Orchestrator Agent",
        extra_env={
            "OPS_AGENT_VERSION": str(ops.version) if ops else "1",
            "MENU_AGENT_VERSION": str(menu.version) if menu else "1",
        }
    )

    # List all agents
    print("\n--- Listing all agents ---")
    try:
        agents = client.agents.list()
        for a in agents:
            print(f"  {a.name} (id: {a.id})")
    except Exception as e:
        print(f"  List error: {e}")
    
    if ops and menu and orchestrator:
        print("\n=== All agents deployed successfully! ===")
    else:
        print("\n=== Some agents failed to deploy. Check errors above. ===")
        sys.exit(1)

if __name__ == "__main__":
    main()
