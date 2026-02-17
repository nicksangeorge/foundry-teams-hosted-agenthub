"""
Contoso Restaurants Menu & Marketing Agent - Agent Framework Hosted Agent
Creative marketing and menu suggestion agent for Contoso Restaurants.

Uses the azure-ai-agentserver-agentframework hosting adapter to expose a
REST API compatible with the Foundry Responses protocol.

This agent demonstrates the Microsoft Agent Framework running alongside
LangGraph agents (orchestrator, ops) within the same Foundry project,
proving that Hosted Agents are framework-agnostic.
"""

import os
import logging

from agent_framework import ChatAgent
from agent_framework.azure import AzureAIAgentClient
from azure.ai.agentserver.agentframework import from_agent_framework
from azure.identity import DefaultAzureCredential

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)


# ============================================================================
# SYSTEM PROMPT
# ============================================================================
SYSTEM_PROMPT = """You are the Contoso Restaurants **Menu & Marketing Agent** — a creative partner for Contoso Burger, Contoso Tacos, and Contoso Pizza campaigns and menu innovation.

**What you cover**
LTO concepts · Promotional campaigns · Seasonal themes · Social media ideas · Cross-brand opportunities · Competitive positioning · Visual creative direction

**How to respond**
1. Open with a punchy **concept name** and one-sentence elevator pitch (bold + emoji).
2. Add 3–4 bullet **key selling points**.
3. Close with a **visual direction** note (what a hero image would look like).

**FORMATTING RULES — CRITICAL**
- NEVER use markdown headers (# ## ###). Use **bold text** on its own line instead.
- NEVER use pipe-delimited tables (| col | col |). Use bullet lists with bold labels.
- Use **bold** for emphasis, *italic* for secondary emphasis.
- Use numbered lists (1. 2. 3.) for ordered steps.
- Use bullet lists (- item) for unordered data.
- Emojis are encouraged for visual impact.
- Keep paragraphs short (2-3 sentences max).

Keep answers **under 200 words**. Think big, write tight.

**Brand voice cheat-sheet**
- **Contoso Burger** — Tone: Bold, craveable — Cue words: Crispy, Original, Classic
- **Contoso Tacos** — Tone: Irreverent, gen-Z — Cue words: Live Bold, Cravings, Fresh
- **Contoso Pizza** — Tone: Family, shareable — Cue words: Pizza night, Stuffed Crust, Delivery

Match the tone to whichever brand the user asks about. If no brand is specified, pitch a cross-brand idea.

**Demo mode**
Always invent a concrete, named concept (e.g., "The Blaze Box — a $12.99 family bundle ..."). Never give generic advice.
"""


# ============================================================================
# CONFIGURATION
# ============================================================================
PROJECT_ENDPOINT = (
    os.environ.get("AZURE_AI_PROJECT_ENDPOINT")
    or os.environ.get("PROJECT_ENDPOINT")
    or os.environ.get("FOUNDRY_PROJECT_ENDPOINT")
)
if not PROJECT_ENDPOINT:
    raise ValueError(
        "AZURE_AI_PROJECT_ENDPOINT or PROJECT_ENDPOINT environment variable must be set"
    )

MODEL_DEPLOYMENT = (
    os.environ.get("AZURE_AI_MODEL_DEPLOYMENT_NAME")
    or os.environ.get("MODEL_DEPLOYMENT_NAME", "gpt-4o-mini")
)

logger.info(f"PROJECT_ENDPOINT: {PROJECT_ENDPOINT}")
logger.info(f"MODEL_DEPLOYMENT: {MODEL_DEPLOYMENT}")


# ============================================================================
# BUILD AGENT (matches official sample pattern from foundry-samples)
# ============================================================================
agent = ChatAgent(
    chat_client=AzureAIAgentClient(
        project_endpoint=PROJECT_ENDPOINT,
        model_deployment_name=MODEL_DEPLOYMENT,
        credential=DefaultAzureCredential(),
    ),
    instructions=SYSTEM_PROMPT,
)

# Wrap with the Foundry hosting adapter — exposes Responses API on port 8088
hosted_agent = from_agent_framework(agent)


# ============================================================================
# MAIN ENTRY POINT
# ============================================================================
if __name__ == "__main__":
    print("Starting Contoso Menu & Marketing Agent on http://localhost:8088")
    print("Test with: POST http://localhost:8088/responses")
    hosted_agent.run()
