"""
Contoso Restaurants Ops Agent - LangGraph Hosted Agent
Operational Q&A agent with chain-of-thought reasoning for Contoso Restaurants.

Uses the azure-ai-agentserver-langgraph hosting adapter to expose a REST API
compatible with the Foundry Responses protocol.
"""

import os
import logging
from typing import TypedDict, Annotated, Sequence
from langchain_core.messages import BaseMessage, HumanMessage, AIMessage, SystemMessage
from langgraph.graph import StateGraph, END
from langgraph.graph.message import add_messages

# Import the hosting adapter
from azure.ai.agentserver.langgraph import from_langgraph

# Setup logging for debugging
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

# Use langchain_openai for Azure OpenAI model access (proven compatible with hosted agents)
from langchain_openai import AzureChatOpenAI
logger.info("Using langchain_openai for Azure OpenAI model access")


# ============================================================================
# SYSTEM PROMPT
# ============================================================================
SYSTEM_PROMPT = """You are the Contoso Restaurants **Ops Agent** — a fast, data-driven assistant for Contoso Burger, Contoso Tacos, and Contoso Pizza restaurant operations.

**What you cover**
Store performance · Sales & traffic · Labor % · Speed of service · Order accuracy · Customer satisfaction · Inventory & supply chain · Staffing · Regional comparisons

**How to respond**
1. Lead with a one-line **headline insight** (bold, with a relevant emoji).
2. Follow with a **bullet list** of key metrics (use bold labels).
3. End with a single **actionable recommendation**.

**FORMATTING RULES — CRITICAL**
- NEVER use markdown headers (# ## ###). Use **bold text** on its own line instead.
- NEVER use pipe-delimited tables (| col | col |). Use bullet lists with bold labels.
- Use **bold** for emphasis, *italic* for secondary emphasis.
- Use numbered lists (1. 2. 3.) for ordered steps.
- Use bullet lists (- item) for unordered data.
- Emojis are encouraged for visual impact.
- Keep paragraphs short (2-3 sentences max).

Keep answers **under 150 words**. Operators are busy — be specific, skip filler.

**Demo data**
Simulate realistic numbers when answering:
- Sales: $2–5M range per region, +/−3–8% YoY
- Stores: #1234, #5678, #9102  
- Regions: Southwest, Northeast, Great Lakes, Southeast
- KPIs: SoS 3.2 min, OA 94.1%, CSAT 4.3/5

Always cite specific numbers — never say "data unavailable."
"""


# ============================================================================
# LANGGRAPH STATE
# ============================================================================
class AgentState(TypedDict):
    """The state maintained across the agent graph."""
    messages: Annotated[Sequence[BaseMessage], add_messages]
    reasoning_steps: list[str]


# ============================================================================
# AGENT LOGIC
# ============================================================================

# Configuration from environment
AZURE_OPENAI_ENDPOINT = os.environ.get("AZURE_OPENAI_ENDPOINT") or os.environ.get("AZURE_AI_ENDPOINT")
if not AZURE_OPENAI_ENDPOINT:
    raise ValueError("AZURE_OPENAI_ENDPOINT or AZURE_AI_ENDPOINT environment variable must be set")
MODEL_DEPLOYMENT = os.environ.get("AZURE_AI_MODEL_DEPLOYMENT_NAME", "gpt-4o-mini")
API_VERSION = os.environ.get("OPENAI_API_VERSION", "2025-03-01-preview")

# Log configuration on startup
logger.info(f"AZURE_OPENAI_ENDPOINT: {AZURE_OPENAI_ENDPOINT}")
logger.info(f"MODEL_DEPLOYMENT: {MODEL_DEPLOYMENT}")
logger.info(f"API_VERSION: {API_VERSION}")

def create_llm():
    """Create the LLM instance using AzureChatOpenAI with managed identity."""
    from azure.identity import DefaultAzureCredential, get_bearer_token_provider
    logger.info("Creating AzureChatOpenAI with DefaultAzureCredential token provider")
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


def reasoning_node(state: AgentState) -> AgentState:
    """
    Process the user message with chain-of-thought reasoning.
    Emits reasoning steps that can be streamed to the client.
    """
    logger.info("reasoning_node called")

    try:
        llm = create_llm()
        logger.info("LLM created successfully")

        # Build message history with system prompt
        messages = [SystemMessage(content=SYSTEM_PROMPT)] + list(state["messages"])
        logger.info(f"Invoking LLM with {len(messages)} messages")

        # Get response from LLM
        response = llm.invoke(messages)
        logger.info("LLM response received")

        # Extract reasoning steps for streaming (if present in response)
        reasoning = []
        content = response.content

        if "Understanding" in content:
            reasoning.append("Analyzing the operational query...")
        if "Analyzing" in content:
            reasoning.append("Gathering relevant metrics and context...")
        if "Response" in content:
            reasoning.append("Preparing actionable insights...")

        return {
            "messages": [response],
            "reasoning_steps": reasoning
        }
    except Exception as e:
        logger.error(f"Error in reasoning_node: {e}", exc_info=True)
        # Return an error message instead of crashing
        error_message = AIMessage(content=f"Sorry, I encountered an error: {str(e)}")
        return {
            "messages": [error_message],
            "reasoning_steps": ["Error occurred"]
        }


def should_continue(state: AgentState) -> str:
    """Determine if we should continue or end the graph."""
    # For this simple agent, we always end after one response
    return END


# ============================================================================
# BUILD THE GRAPH
# ============================================================================
def build_graph():
    """Build and compile the LangGraph agent."""
    workflow = StateGraph(AgentState)
    
    # Add nodes
    workflow.add_node("reasoning", reasoning_node)
    
    # Set entry point
    workflow.set_entry_point("reasoning")
    
    # Add edges
    workflow.add_conditional_edges("reasoning", should_continue)
    
    # Compile the graph
    return workflow.compile()


# ============================================================================
# CREATE HOSTED AGENT
# ============================================================================
# Build the LangGraph agent
graph = build_graph()

# Create the hosted agent using the Foundry adapter
# This wraps the graph in a REST API compatible with the Foundry Responses protocol
hosted_agent = from_langgraph(graph)


# ============================================================================
# MAIN ENTRY POINT
# ============================================================================
if __name__ == "__main__":
    # Run the hosted agent locally for testing
    print("🚀 Starting Contoso Ops Agent on http://localhost:8088")
    print("📝 Test with: POST http://localhost:8088/responses")
    print("   Body: {\"input\": {\"messages\": [{\"role\": \"user\", \"content\": \"What are today's store metrics?\"}]}}")
    
    hosted_agent.run()
