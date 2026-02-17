# Contoso Orchestrator Agent - Dockerfile
# LangGraph ReAct Agent for Foundry Agent Service

FROM python:3.12-slim

# Set working directory
WORKDIR /app

# Install system dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    && rm -rf /var/lib/apt/lists/*

# Copy requirements first for better caching
COPY requirements.txt .

# Install Python dependencies
RUN pip install --no-cache-dir -r requirements.txt

# Copy application code
COPY main.py .

# Expose port for the hosting adapter
EXPOSE 8088

# Health check endpoint (provided by hosting adapter)
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:8088/readiness || exit 1

# Run the agent
CMD ["python", "main.py"]
