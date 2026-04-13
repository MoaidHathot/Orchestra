---
layout: default
title: Getting Started
nav_order: 2
---

# Getting Started

This guide walks you through setting up Orchestra and creating your first orchestration.

## Prerequisites

- .NET 10.0 SDK or later
- GitHub Copilot subscription (for Orchestra.Copilot)

## Installation

### Option 1: Add packages to an existing project

```bash
dotnet add package Orchestra.Engine
dotnet add package Orchestra.Host
dotnet add package Orchestra.Copilot
```

### Option 2: Create a new project

```bash
dotnet new web -n MyOrchestrationHost
cd MyOrchestrationHost
dotnet add package Orchestra.Engine
dotnet add package Orchestra.Host
dotnet add package Orchestra.Copilot
```

## Basic Setup

### Configure Program.cs

```csharp
using Orchestra.Copilot;
using Orchestra.Engine;
using Orchestra.Host.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Register the Copilot agent builder (implements AgentBuilder)
builder.Services.AddSingleton<AgentBuilder, CopilotAgentBuilder>();

// Add Orchestra Host services
builder.Services.AddOrchestraHost(options =>
{
    // Where to store run history, triggers, and registry
    options.DataPath = Path.Combine(builder.Environment.ContentRootPath, "data");
    
    // Optional: Auto-scan this directory for orchestration files on startup
    options.OrchestrationsScan = new OrchestrationsScanConfig
    {
        Directory = Path.Combine(builder.Environment.ContentRootPath, "orchestrations"),
    };
    
    // Load previously registered orchestrations on startup
    options.LoadPersistedOrchestrations = true;
    
    // Register triggers defined in orchestration JSON files
    options.RegisterJsonTriggers = true;
});

var app = builder.Build();

// Initialize Orchestra Host (loads persisted data)
app.Services.InitializeOrchestraHost();

// Map all Orchestra API endpoints
app.MapOrchestraHostEndpoints();

app.Run();
```

## Creating Your First Orchestration

Create a file `orchestrations/hello-world.json`:

```json
{
  "name": "hello-world",
  "description": "A simple hello world orchestration",
  "version": "1.0",
  "steps": [
    {
      "name": "greet",
      "type": "prompt",
      "model": "claude-opus-4.5",
      "systemPrompt": "You are a friendly assistant.",
      "userPrompt": "Say hello to {{name}} in a creative way!"
    }
  ]
}
```

## Running the Orchestration

### 1. Start the host

```bash
dotnet run
```

### 2. Register the orchestration

```bash
curl -X POST http://localhost:5000/api/orchestrations \
  -H "Content-Type: application/json" \
  -d '{"paths": ["./orchestrations/hello-world.json"]}'
```

### 3. Execute the orchestration

```bash
# Get the orchestration ID from the registration response, then:
curl -N "http://localhost:5000/api/orchestrations/{id}/run?params={\"name\":\"World\"}"
```

The response is a Server-Sent Events stream showing real-time execution progress.

## Multi-Step Orchestration

Create an orchestration with dependent steps:

```json
{
  "name": "research-and-summarize",
  "description": "Research a topic and create a summary",
  "version": "1.0",
  "steps": [
    {
      "name": "research",
      "type": "prompt",
      "model": "claude-opus-4.5",
      "systemPrompt": "You are a thorough researcher.",
      "userPrompt": "Research the following topic in depth: {{topic}}"
    },
    {
      "name": "summarize",
      "type": "prompt",
      "model": "claude-opus-4.5",
      "dependsOn": ["research"],
      "systemPrompt": "You are a technical writer who creates concise summaries.",
      "userPrompt": "Create a brief executive summary of the research findings."
    }
  ]
}
```

In this example:
- `research` runs first (no dependencies)
- `summarize` waits for `research` to complete, then receives its output automatically

## Parallel Execution

Steps without mutual dependencies run in parallel:

```json
{
  "name": "parallel-analysis",
  "description": "Parallel technical and market analysis",
  "version": "1.0",
  "steps": [
    {
      "name": "technical-review",
      "type": "prompt",
      "model": "claude-opus-4.5",
      "systemPrompt": "You are a technical analyst.",
      "userPrompt": "Analyze the technical aspects of: {{topic}}"
    },
    {
      "name": "market-review",
      "type": "prompt",
      "model": "claude-opus-4.5",
      "systemPrompt": "You are a market analyst.",
      "userPrompt": "Analyze the market potential of: {{topic}}"
    },
    {
      "name": "final-report",
      "type": "prompt",
      "model": "claude-opus-4.5",
      "dependsOn": ["technical-review", "market-review"],
      "systemPrompt": "You are a senior analyst who creates comprehensive reports.",
      "userPrompt": "Combine the technical and market analyses into a final report."
    }
  ]
}
```

Here, `technical-review` and `market-review` run in parallel, and `final-report` runs after both complete.

## Template Expressions

Orchestra supports several template expression namespaces for dynamic values in your orchestrations:

| Syntax | Description |
|--------|-------------|
| `{{param.name}}` | Runtime parameter value |
| `{{vars.name}}` | User-defined variable (see below) |
| `{{orchestration.name}}` | Orchestration name |
| `{{orchestration.version}}` | Orchestration version |
| `{{orchestration.runId}}` | Unique run identifier |
| `{{orchestration.startedAt}}` | Run start time (ISO 8601) |
| `{{step.name}}` | Current step's name |
| `{{step.type}}` | Current step's type |
| `{{env.VAR_NAME}}` | OS environment variable value |
| `{{stepName.output}}` | Output from a completed step |
| `{{stepName.rawOutput}}` | Raw output from a completed step |
| `{{stepName.files}}` | JSON array of file paths saved by a step via `orchestra_save_file` |
| `{{stepName.files[N]}}` | Path of the Nth file (0-based) saved by a step |

All expressions are case-insensitive and whitespace-tolerant.

## Using Variables

Variables let you define reusable values at the orchestration level:

```json
{
  "name": "deployment-pipeline",
  "version": "2.1.0",
  "variables": {
    "appName": "customer-portal",
    "registry": "ghcr.io/myorg/{{vars.appName}}",
    "artifactPath": "/artifacts/{{vars.appName}}/{{orchestration.runId}}"
  },
  "steps": [
    {
      "name": "build",
      "type": "Command",
      "dependsOn": [],
      "command": "dotnet",
      "arguments": ["publish", "-o", "{{vars.artifactPath}}"]
    },
    {
      "name": "deploy-summary",
      "type": "Transform",
      "dependsOn": ["build"],
      "template": "Deployed {{vars.appName}} to {{vars.registry}}:{{orchestration.runId}}\nBuild output: {{build.output}}"
    }
  ]
}
```

Key points:
- Variable values can contain other template expressions, which are resolved when used
- Variables can reference other variables (e.g., `registry` references `appName`)
- Circular references are detected and left unresolved (no errors or infinite loops)

## Using MCP Servers

Orchestra supports Model Context Protocol (MCP) servers for external tool access:

### Define MCP configuration

Create `mcp.json`:

```json
{
  "mcps": [
    {
      "name": "filesystem",
      "type": "local",
      "command": "npx",
      "arguments": ["-y", "@anthropic/mcp-server-filesystem", "."]
    },
    {
      "name": "web-search",
      "type": "remote",
      "endpoint": "https://mcp.example.com/search",
      "headers": {
        "Authorization": "Bearer ${WEB_SEARCH_API_KEY}"
      }
    }
  ]
}
```

### Reference MCPs in orchestration steps

```json
{
  "name": "file-analyzer",
  "description": "Analyze files using MCP filesystem tools",
  "version": "1.0",
  "steps": [
    {
      "name": "analyze",
      "type": "prompt",
      "model": "claude-opus-4.5",
      "systemPrompt": "You are a code analysis assistant.",
      "mcps": ["filesystem"],
      "userPrompt": "Read and analyze all TypeScript files in the src directory."
    }
  ]
}
```

## Next Steps

- [Orchestra.Engine](engine) - Learn about the core engine architecture
- [Orchestra.Host](host) - Explore the hosting layer and API
- [Orchestra.Copilot](copilot) - Understand the Copilot integration
- [API Reference](api-reference) - Complete REST API documentation
