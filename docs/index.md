---
layout: default
title: Home
nav_order: 1
---

# Orchestra

A declarative orchestration framework for LLM workflows in .NET.

Orchestra enables you to define multi-step AI pipelines declaratively in JSON, where steps can depend on each other forming a Directed Acyclic Graph (DAG). Independent steps execute in parallel automatically, and the framework handles retries, quality control loops, and external tool integration via the Model Context Protocol (MCP).

## Features

- **Declarative Orchestrations**: Define AI workflows in JSON with clear step dependencies
- **Parallel Execution**: Independent steps run concurrently for optimal performance
- **Quality Control Loops**: Built-in retry mechanisms with checker steps for output validation
- **MCP Integration**: Connect to external tools via Model Context Protocol (local or remote servers)
- **Multiple Trigger Types**: Schedule orchestrations via cron, webhooks, email polling, or loops
- **Real-time Streaming**: Server-Sent Events (SSE) for live execution progress
- **Run History**: Persistent storage of execution traces with detailed step-by-step records
- **Provider Agnostic**: Abstract agent interfaces allow any LLM provider implementation

## Architecture

Orchestra is composed of three main packages:

| Package | Description |
|---------|-------------|
| [Orchestra.Engine](engine) | Core orchestration engine with execution, scheduling, and storage abstractions |
| [Orchestra.Host](host) | ASP.NET Core hosting layer with REST API, triggers, and SSE streaming |
| [Orchestra.Copilot](copilot) | GitHub Copilot SDK implementation of the agent interfaces |

## Quick Start

### 1. Install the packages

```bash
dotnet add package Orchestra.Engine
dotnet add package Orchestra.Host
dotnet add package Orchestra.Copilot
```

### 2. Define an orchestration

Create a JSON file defining your workflow:

```json
{
  "name": "research-assistant",
  "description": "Research a topic and generate a summary",
  "version": "1.0",
  "steps": [
    {
      "name": "research",
      "type": "prompt",
      "model": "claude-opus-4.5",
      "systemPrompt": "You are a research assistant.",
      "userPrompt": "Research the following topic: {{topic}}"
    },
    {
      "name": "summarize",
      "type": "prompt",
      "model": "claude-opus-4.5",
      "dependsOn": ["research"],
      "systemPrompt": "You are a technical writer.",
      "userPrompt": "Summarize the research findings into a concise report."
    }
  ]
}
```

### 3. Set up the host

```csharp
var builder = WebApplication.CreateBuilder(args);

// Register the Copilot agent builder
builder.Services.AddSingleton<AgentBuilder, CopilotAgentBuilder>();

// Add Orchestra Host services
builder.Services.AddOrchestraHost(options =>
{
    options.DataPath = "./data";
    options.OrchestrationsScan = new OrchestrationsScanConfig
    {
        Directory = "./orchestrations",
    };
});

var app = builder.Build();

// Initialize and map endpoints
app.Services.InitializeOrchestraHost();
app.MapOrchestraHostEndpoints();

app.Run();
```

### 4. Run an orchestration

```bash
# Register the orchestration
curl -X POST http://localhost:5000/api/orchestrations \
  -H "Content-Type: application/json" \
  -d '{"paths": ["./orchestrations/research-assistant.json"]}'

# Execute with parameters (SSE stream)
curl -N "http://localhost:5000/api/orchestrations/{id}/run?params={\"topic\":\"quantum computing\"}"
```

## Documentation

- [Getting Started](getting-started) - Installation and basic setup
- [Orchestra.Engine](engine) - Core engine documentation
- [Orchestra.Host](host) - Hosting layer and API reference
- [Orchestra.Copilot](copilot) - GitHub Copilot integration
- [API Reference](api-reference) - Complete REST API documentation

## Requirements

- .NET 10.0 or later
- GitHub Copilot subscription (for Orchestra.Copilot)

## License

This project is licensed under the MIT License.
