"""
Contoso Restaurants Orchestrator Agent - LangGraph Agent
Central routing intelligence that classifies user intent and delegates to
specialized sub-agents (Ops, Menu) using tool-calling.

Uses the azure-ai-agentserver-langgraph hosting adapter to expose a REST API
compatible with the Foundry Responses protocol.
"""

import os
import logging
import httpx
from langchain_core.messages import SystemMessage, ToolMessage
from langchain_core.tools import tool
from langgraph.graph import END, START, MessagesState, StateGraph

# Import the hosting adapter
from azure.ai.agentserver.langgraph import from_langgraph

# Setup logging for debugging
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

from langchain_openai import AzureChatOpenAI
logger.info("Using langchain_openai for Azure OpenAI model access")


# ============================================================================
# SYSTEM PROMPT
# ============================================================================
SYSTEM_PROMPT = """You are the **Contoso Restaurants Orchestrator Agent** — the central routing intelligence for a multi-agent system serving Contoso Burger, Contoso Tacos, and Contoso Pizza restaurant operations.

**Your role**
You receive user messages and determine which specialized agent should handle them. You have two tools available:
- `query_ops_agent` — for operational questions (store performance, KPIs, sales, labor, food safety, etc.)
- `query_menu_agent` — for creative/marketing questions (menu innovation, campaigns, promotions, brand strategy, etc.)

**How to behave**
1. Analyze the user's message to understand intent.
2. Call the appropriate tool with the user's question (pass the full question, don't summarize).
3. Return the tool's response directly to the user — do NOT add your own commentary or re-summarize.
4. If the message is ambiguous or spans both domains, pick the most relevant agent. If truly equal, prefer the Ops Agent.
5. For greetings or general questions not related to either domain, respond directly with a brief, friendly message explaining what you can help with.

**CRITICAL RULES**
- ALWAYS use a tool for domain-specific questions. Never make up operational data or marketing concepts yourself.
- Pass the user's EXACT question to the tool. Do not rephrase or summarize.
- Return the tool's response AS-IS. Do not wrap it in additional commentary.
- If a tool returns an error, apologize briefly and suggest the user try again."""


# ============================================================================
# CONFIGURATION
# ============================================================================

# LLM configuration from environment
AZURE_OPENAI_ENDPOINT = os.environ.get("AZURE_OPENAI_ENDPOINT") or os.environ.get("AZURE_AI_ENDPOINT")
if not AZURE_OPENAI_ENDPOINT:
    raise ValueError("AZURE_OPENAI_ENDPOINT or AZURE_AI_ENDPOINT environment variable must be set")
MODEL_DEPLOYMENT = os.environ.get("AZURE_AI_MODEL_DEPLOYMENT_NAME", "gpt-4o-mini")
API_VERSION = os.environ.get("OPENAI_API_VERSION", "2025-03-01-preview")

# Sub-agent routing configuration
FOUNDRY_PROJECT_ENDPOINT = os.environ.get("FOUNDRY_PROJECT_ENDPOINT", "")
OPS_AGENT_NAME = os.environ.get("OPS_AGENT_NAME", "ContosoOpsAgent")
OPS_AGENT_VERSION = os.environ.get("OPS_AGENT_VERSION", "1")
MENU_AGENT_NAME = os.environ.get("MENU_AGENT_NAME", "ContosoMenuAgent")
MENU_AGENT_VERSION = os.environ.get("MENU_AGENT_VERSION", "1")

# Log configuration on startup
logger.info(f"AZURE_OPENAI_ENDPOINT: {AZURE_OPENAI_ENDPOINT}")
logger.info(f"MODEL_DEPLOYMENT: {MODEL_DEPLOYMENT}")
logger.info(f"API_VERSION: {API_VERSION}")
logger.info(f"FOUNDRY_PROJECT_ENDPOINT: {FOUNDRY_PROJECT_ENDPOINT}")
logger.info(f"OPS_AGENT_NAME: {OPS_AGENT_NAME} v{OPS_AGENT_VERSION}")
logger.info(f"MENU_AGENT_NAME: {MENU_AGENT_NAME} v{MENU_AGENT_VERSION}")


# ============================================================================
# SUB-AGENT CALLING HELPER
# ============================================================================
def _call_sub_agent(question: str, agent_name: str, agent_version: str) -> str:
    """
    Call a sub-agent via the Foundry Responses API.
    Returns the text response from the sub-agent.
    """
    if not FOUNDRY_PROJECT_ENDPOINT:
        return f"Error: FOUNDRY_PROJECT_ENDPOINT is not configured. Cannot reach {agent_name}."

    url = f"{FOUNDRY_PROJECT_ENDPOINT.rstrip('/')}/openai/responses?api-version=2025-11-15-preview"

    # Get Bearer token for Foundry API
    from azure.identity import DefaultAzureCredential
    credential = DefaultAzureCredential()
    token = credential.get_token("https://ai.azure.com/.default")

    headers = {
        "Authorization": f"Bearer {token.token}",
        "Content-Type": "application/json",
    }

    payload = {
        "input": [{"role": "user", "content": question}],
        "agent": {
            "type": "agent_reference",
            "name": agent_name,
            "version": agent_version,
        },
        "stream": False,
    }

    logger.info(f"Calling sub-agent {agent_name} v{agent_version} at {url}")
    logger.info(f"Question: {question[:200]}...")

    try:
        with httpx.Client(timeout=120.0) as client:
            response = client.post(url, json=payload, headers=headers)
            response.raise_for_status()
            data = response.json()

        # Parse response — try multiple known formats
        # Format 1: output_text (simplest)
        if "output_text" in data and data["output_text"]:
            result = data["output_text"]
            logger.info(f"Sub-agent {agent_name} responded (output_text): {result[:200]}...")
            return result

        # Format 2: output array with message objects
        if "output" in data and isinstance(data["output"], list):
            for item in data["output"]:
                if item.get("type") == "message" and "content" in item:
                    texts = []
                    for content_block in item["content"]:
                        if isinstance(content_block, dict) and "text" in content_block:
                            texts.append(content_block["text"])
                        elif isinstance(content_block, str):
                            texts.append(content_block)
                    if texts:
                        result = "\n".join(texts)
                        logger.info(f"Sub-agent {agent_name} responded (output array): {result[:200]}...")
                        return result

        # Format 3: OpenAI chat completion format
        if "choices" in data and data["choices"]:
            content = data["choices"][0].get("message", {}).get("content", "")
            if content:
                logger.info(f"Sub-agent {agent_name} responded (choices): {content[:200]}...")
                return content

        # Fallback — return raw response for debugging
        logger.warning(f"Sub-agent {agent_name} returned unexpected format: {str(data)[:500]}")
        return f"Received a response from {agent_name} but could not parse it. Raw: {str(data)[:300]}"

    except httpx.HTTPStatusError as e:
        logger.error(f"HTTP error calling {agent_name}: {e.response.status_code} - {e.response.text[:500]}")
        return f"Sorry, I couldn't reach the {agent_name}. It returned HTTP {e.response.status_code}. Please try again."
    except httpx.RequestError as e:
        logger.error(f"Request error calling {agent_name}: {e}")
        return f"Sorry, I couldn't connect to the {agent_name}. Please try again later."
    except Exception as e:
        logger.error(f"Unexpected error calling {agent_name}: {e}", exc_info=True)
        return f"Sorry, an unexpected error occurred while contacting the {agent_name}. Please try again."


# ============================================================================
# TOOLS
# ============================================================================
@tool
def query_ops_agent(question: str) -> str:
    """Route operational questions about store performance, KPIs, sales data,
    labor, food safety, drive-thru, inventory, staffing, and regional
    comparisons to the Ops Agent."""
    logger.info(f"[Tool] query_ops_agent called with: {question[:200]}")
    result = _call_sub_agent(question, OPS_AGENT_NAME, OPS_AGENT_VERSION)
    logger.info(f"[Tool] query_ops_agent result: {result[:200]}")
    return result


@tool
def query_menu_agent(question: str) -> str:
    """Route creative and marketing questions about menu innovation,
    promotional campaigns, LTOs, brand strategies, social media ideas,
    and visual creative direction to the Menu & Marketing Agent."""
    logger.info(f"[Tool] query_menu_agent called with: {question[:200]}")
    result = _call_sub_agent(question, MENU_AGENT_NAME, MENU_AGENT_VERSION)
    logger.info(f"[Tool] query_menu_agent result: {result[:200]}")
    return result


# ============================================================================
# LLM WITH TOOLS (lazy initialization like official calculator sample)
# ============================================================================
_tools = [query_ops_agent, query_menu_agent]
_tools_by_name = {t.name: t for t in _tools}
_llm_with_tools = None


def get_llm_with_tools():
    """Create and cache the LLM with tools bound. Uses lazy init to avoid
    module-level credential calls that can fail during container startup."""
    global _llm_with_tools
    if _llm_with_tools is None:
        from azure.identity import DefaultAzureCredential, get_bearer_token_provider
        logger.info("Creating AzureChatOpenAI with DefaultAzureCredential token provider")
        token_provider = get_bearer_token_provider(
            DefaultAzureCredential(),
            "https://cognitiveservices.azure.com/.default"
        )
        llm = AzureChatOpenAI(
            azure_endpoint=AZURE_OPENAI_ENDPOINT,
            azure_deployment=MODEL_DEPLOYMENT,
            api_version=API_VERSION,
            azure_ad_token_provider=token_provider,
        )
        _llm_with_tools = llm.bind_tools(_tools)
    return _llm_with_tools


# ============================================================================
# GRAPH NODES (matches official foundry-samples calculator-agent pattern)
# ============================================================================
def llm_call(state: MessagesState):
    """LLM decides whether to call a tool or respond directly."""
    return {
        "messages": [
            get_llm_with_tools().invoke(
                [SystemMessage(content=SYSTEM_PROMPT)] + list(state["messages"])
            )
        ]
    }


def tool_node(state: dict):
    """Execute the tool calls from the last LLM message."""
    result = []
    for tool_call in state["messages"][-1].tool_calls:
        t = _tools_by_name[tool_call["name"]]
        observation = t.invoke(tool_call["args"])
        result.append(ToolMessage(content=str(observation), tool_call_id=tool_call["id"]))
    return {"messages": result}


def should_continue(state: MessagesState):
    """Route to tools node if the LLM made a tool call, otherwise end."""
    last_message = state["messages"][-1]
    if last_message.tool_calls:
        return "tools"
    return END


# ============================================================================
# BUILD THE GRAPH
# ============================================================================
def build_graph():
    """Build and compile the LangGraph agent with tool-calling."""
    builder = StateGraph(MessagesState)

    builder.add_node("llm_call", llm_call)
    builder.add_node("tools", tool_node)

    builder.add_edge(START, "llm_call")
    builder.add_conditional_edges(
        "llm_call",
        should_continue,
        {"tools": "tools", END: END},
    )
    builder.add_edge("tools", "llm_call")

    return builder.compile()


# ============================================================================
# CREATE HOSTED AGENT
# ============================================================================
# Build the LangGraph ReAct agent
graph = build_graph()

# Create the hosted agent using the Foundry adapter
# This wraps the graph in a REST API compatible with the Foundry Responses protocol
hosted_agent = from_langgraph(graph)


# ============================================================================
# MAIN ENTRY POINT
# ============================================================================
if __name__ == "__main__":
    # Run the hosted agent locally for testing
    print("🚀 Starting Contoso Orchestrator Agent on http://localhost:8088")
    print("📝 Test with: POST http://localhost:8088/responses")
    print('   Body: {"input": {"messages": [{"role": "user", "content": "How are Southwest stores performing?"}]}}')

    hosted_agent.run()
