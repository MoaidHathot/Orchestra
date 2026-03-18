---
layout: default
title: Orchestra.Copilot
nav_order: 5
---

# Orchestra.Copilot

Orchestra.Copilot provides a GitHub Copilot SDK implementation of the agent interfaces defined in Orchestra.Engine.

## Overview

This package bridges Orchestra with GitHub Copilot, enabling orchestrations to leverage Copilot's AI capabilities. It implements the `IAgent` and `AgentBuilder` abstractions, handling all the complexity of communicating with the Copilot SDK.

## Installation

```bash
dotnet add package Orchestra.Copilot
```

## Prerequisites

- GitHub Copilot subscription
- GitHub authentication configured

## Quick Start

```csharp
using Orchestra.Copilot;
using Orchestra.Engine;

// Create the agent builder
await using var builder = new CopilotAgentBuilder(loggerFactory);

// Build an agent
var agent = await builder
    .WithModel("claude-opus-4.5")
    .WithSystemPrompt("You are a helpful assistant.")
    .BuildAgentAsync(cancellationToken);

// Send a prompt and stream the response
var task = agent.SendAsync("Explain quantum computing", cancellationToken);

await foreach (var evt in task)
{
    if (evt.Type == AgentEventType.MessageDelta)
    {
        Console.Write(evt.Content);
    }
}

// Get the final result
var result = await task.GetResultAsync();
Console.WriteLine($"\nTokens: {result.Usage?.InputTokens} in, {result.Usage?.OutputTokens} out");
```

## Integration with Orchestra.Host

```csharp
var builder = WebApplication.CreateBuilder(args);

// Register CopilotAgentBuilder as the AgentBuilder implementation
builder.Services.AddSingleton<AgentBuilder, CopilotAgentBuilder>();

// Add Orchestra Host services
builder.Services.AddOrchestraHost(options =>
{
    options.DataPath = "./data";
});

var app = builder.Build();
app.Services.InitializeOrchestraHost();
app.MapOrchestraHostEndpoints();
app.Run();
```

## CopilotAgentBuilder

The `CopilotAgentBuilder` extends the abstract `AgentBuilder` from Orchestra.Engine:

```csharp
public class CopilotAgentBuilder : AgentBuilder, IAsyncDisposable
{
    public CopilotAgentBuilder(ILoggerFactory? loggerFactory = null);
    
    public override Task<IAgent> BuildAgentAsync(CancellationToken ct = default);
    public ValueTask DisposeAsync();
}
```

### Configuration Methods

All configuration is done through the base `AgentBuilder` fluent API:

```csharp
var agent = await builder
    .WithModel("claude-opus-4.5")           // Set the AI model
    .WithSystemPrompt("You are an expert.") // Set system prompt
    .WithSystemPromptMode(SystemPromptMode.Replace)  // Replace or Append
    .WithReasoningLevel(ReasoningLevel.High)         // Low, Medium, High
    .WithMcp(localMcp, remoteMcp)           // Add MCP servers
    .WithSubagents(subagent1, subagent2)    // Add subagents
    .WithReporter(reporter)                 // Set event reporter
    .BuildAgentAsync(cancellationToken);
```

### Lifecycle Management

The builder manages the Copilot client lifecycle. Always dispose when done:

```csharp
await using var builder = new CopilotAgentBuilder(loggerFactory);
// Use builder...
// Client is automatically stopped on disposal
```

## CopilotAgent

The `CopilotAgent` implements `IAgent` and handles communication with the Copilot SDK:

```csharp
public interface IAgent
{
    AgentTask SendAsync(string prompt, CancellationToken ct = default);
}
```

### Sending Prompts

```csharp
AgentTask task = agent.SendAsync("Your prompt here", cancellationToken);
```

### Streaming Events

The `AgentTask` is an `IAsyncEnumerable<AgentEvent>`:

```csharp
await foreach (var evt in task)
{
    switch (evt.Type)
    {
        case AgentEventType.SessionStart:
            Console.WriteLine("Session started");
            break;
            
        case AgentEventType.MessageDelta:
            Console.Write(evt.Content);
            break;
            
        case AgentEventType.ReasoningDelta:
            Console.Write($"[Thinking: {evt.Content}]");
            break;
            
        case AgentEventType.ToolExecutionStart:
            Console.WriteLine($"Calling {evt.ToolName} from {evt.McpServerName}");
            break;
            
        case AgentEventType.ToolExecutionComplete:
            Console.WriteLine($"Tool result: {evt.ToolResult}");
            break;
            
        case AgentEventType.SubagentStarted:
            Console.WriteLine($"Delegating to {evt.SubagentName}");
            break;
            
        case AgentEventType.SubagentCompleted:
            Console.WriteLine($"Subagent completed: {evt.SubagentName}");
            break;
            
        case AgentEventType.Usage:
            Console.WriteLine($"Tokens: {evt.Usage?.InputTokens} in, {evt.Usage?.OutputTokens} out");
            break;
            
        case AgentEventType.Error:
            Console.WriteLine($"Error: {evt.ErrorMessage}");
            break;
    }
}
```

### Getting Results

```csharp
AgentResult result = await task.GetResultAsync();

Console.WriteLine($"Content: {result.Content}");
Console.WriteLine($"Model: {result.ActualModel}");
Console.WriteLine($"Input tokens: {result.Usage?.InputTokens}");
Console.WriteLine($"Output tokens: {result.Usage?.OutputTokens}");
Console.WriteLine($"Cost: ${result.Usage?.Cost}");
```

## Event Types

| Type | Description | Key Properties |
|------|-------------|----------------|
| `SessionStart` | Session has started | `Model` |
| `ModelChange` | Model selection changed | `PreviousModel`, `Model` |
| `MessageDelta` | Streaming text chunk | `Content` |
| `Message` | Complete message | `Content` |
| `ReasoningDelta` | Streaming reasoning | `Content` |
| `Reasoning` | Complete reasoning | `Content` |
| `ToolExecutionStart` | Tool call started | `ToolName`, `ToolArguments`, `McpServerName` |
| `ToolExecutionComplete` | Tool call completed | `ToolName`, `ToolResult`, `ToolSuccess`, `McpServerName` |
| `SubagentSelected` | Subagent was selected | `SubagentName`, `SubagentTools` |
| `SubagentStarted` | Subagent started | `SubagentName`, `SubagentDisplayName` |
| `SubagentCompleted` | Subagent completed | `SubagentName` |
| `SubagentFailed` | Subagent failed | `SubagentName`, `ErrorMessage` |
| `SubagentDeselected` | Subagent deselected | `SubagentName` |
| `Usage` | Token usage info | `Usage` (AgentUsage object) |
| `Error` | Error occurred | `ErrorMessage` |
| `SessionIdle` | Session completed | - |

## MCP Server Configuration

### Local MCP Servers

```csharp
var localMcp = new LocalMcp
{
    Name = "filesystem",
    Type = McpType.Local,
    Command = "npx",
    Arguments = new[] { "-y", "@anthropic/mcp-server-filesystem", "." },
    WorkingDirectory = "/path/to/project"
};

var agent = await builder
    .WithMcp(localMcp)
    .BuildAgentAsync();
```

### Remote MCP Servers

```csharp
var remoteMcp = new RemoteMcp
{
    Name = "web-search",
    Type = McpType.Remote,
    Endpoint = "https://mcp.example.com/search",
    Headers = new Dictionary<string, string>
    {
        ["Authorization"] = "Bearer your-api-key"
    }
};

var agent = await builder
    .WithMcp(remoteMcp)
    .BuildAgentAsync();
```

## Subagents

Subagents allow delegation to specialized agents:

```csharp
var codeReviewer = new Subagent
{
    Name = "code-reviewer",
    DisplayName = "Code Reviewer",
    Description = "Reviews code for bugs and best practices",
    Prompt = "You are an expert code reviewer. Analyze the code for issues.",
    Tools = new[] { "read_file", "search_code" },
    Infer = true  // Allow automatic selection
};

var agent = await builder
    .WithSubagents(codeReviewer)
    .BuildAgentAsync();
```

### Subagent Properties

| Property | Type | Description |
|----------|------|-------------|
| `Name` | `string` | Unique identifier |
| `DisplayName` | `string?` | Human-readable name |
| `Description` | `string?` | Description of capabilities |
| `Prompt` | `string` | System prompt for the subagent |
| `Tools` | `string[]?` | Allowed tool names |
| `Mcps` | `Mcp[]` | MCP servers for this subagent |
| `Infer` | `bool` | Allow automatic selection (default: true) |

## Reasoning Levels

Control the depth of reasoning:

```csharp
// Quick responses
builder.WithReasoningLevel(ReasoningLevel.Low);

// Balanced
builder.WithReasoningLevel(ReasoningLevel.Medium);

// Deep analysis
builder.WithReasoningLevel(ReasoningLevel.High);
```

## System Prompt Modes

Control how the system prompt is applied:

```csharp
// Replace the default system prompt entirely
builder.WithSystemPromptMode(SystemPromptMode.Replace);

// Append to the default system prompt
builder.WithSystemPromptMode(SystemPromptMode.Append);
```

## Model Mismatch Detection

The agent automatically detects when the requested model differs from the actual model used:

```csharp
var result = await task.GetResultAsync();

if (result.SelectedModel != result.ActualModel)
{
    Console.WriteLine($"Requested: {result.SelectedModel}");
    Console.WriteLine($"Actual: {result.ActualModel}");
    
    // Available models are included when there's a mismatch
    if (result.AvailableModels != null)
    {
        foreach (var model in result.AvailableModels)
        {
            Console.WriteLine($"  - {model.Id}: {model.Name}");
        }
    }
}
```

## Usage Information

Token usage and cost information is provided:

```csharp
var result = await task.GetResultAsync();

if (result.Usage != null)
{
    Console.WriteLine($"Input tokens: {result.Usage.InputTokens}");
    Console.WriteLine($"Output tokens: {result.Usage.OutputTokens}");
    Console.WriteLine($"Cache read tokens: {result.Usage.CacheReadTokens}");
    Console.WriteLine($"Cache write tokens: {result.Usage.CacheWriteTokens}");
    Console.WriteLine($"Cost: ${result.Usage.Cost:F4}");
    Console.WriteLine($"Duration: {result.Usage.Duration}ms");
}
```

## Error Handling

```csharp
try
{
    var task = agent.SendAsync("Hello", cancellationToken);
    
    await foreach (var evt in task)
    {
        if (evt.Type == AgentEventType.Error)
        {
            Console.WriteLine($"Error during execution: {evt.ErrorMessage}");
        }
    }
    
    var result = await task.GetResultAsync();
}
catch (OperationCanceledException)
{
    Console.WriteLine("Operation was cancelled");
}
catch (Exception ex)
{
    Console.WriteLine($"Unexpected error: {ex.Message}");
}
```

## Complete Example

```csharp
using Microsoft.Extensions.Logging;
using Orchestra.Copilot;
using Orchestra.Engine;

// Setup logging
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

// Create the builder
await using var agentBuilder = new CopilotAgentBuilder(loggerFactory);

// Configure MCP server
var filesystem = new LocalMcp
{
    Name = "filesystem",
    Type = McpType.Local,
    Command = "npx",
    Arguments = new[] { "-y", "@anthropic/mcp-server-filesystem", "." }
};

// Build the agent
var agent = await agentBuilder
    .WithModel("claude-opus-4.5")
    .WithSystemPrompt("You are a helpful coding assistant.")
    .WithSystemPromptMode(SystemPromptMode.Replace)
    .WithReasoningLevel(ReasoningLevel.Medium)
    .WithMcp(filesystem)
    .BuildAgentAsync();

// Execute
var task = agent.SendAsync("List all TypeScript files and summarize them");

// Stream output
await foreach (var evt in task)
{
    switch (evt.Type)
    {
        case AgentEventType.MessageDelta:
            Console.Write(evt.Content);
            break;
        case AgentEventType.ToolExecutionStart:
            Console.WriteLine($"\n[Calling {evt.ToolName}...]");
            break;
    }
}

// Get final result
var result = await task.GetResultAsync();
Console.WriteLine($"\n\n--- Complete ---");
Console.WriteLine($"Model: {result.ActualModel}");
Console.WriteLine($"Tokens: {result.Usage?.InputTokens} in, {result.Usage?.OutputTokens} out");
```

## Dependencies

- **Target Framework**: .NET 10.0
- **Project Reference**: `Orchestra.Engine`
- **Package Reference**: `GitHub.Copilot.SDK` (v0.1.29)
