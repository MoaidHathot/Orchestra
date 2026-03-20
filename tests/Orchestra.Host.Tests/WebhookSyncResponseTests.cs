using FluentAssertions;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Tests for synchronous webhook response features including response templates,
/// WebhookResponseConfig model, and OrchestrationParser webhook parsing.
/// </summary>
public class WebhookSyncResponseTests
{
	// ── Response Template Tests ──

	[Fact]
	public void ApplyResponseTemplate_SinglePlaceholder_ReplacesCorrectly()
	{
		// Arrange
		var template = "The result is: {{analyze.Content}}";
		var result = CreateOrchestrationResult(
			ExecutionStatus.Succeeded,
			("analyze", new ExecutionResult { Content = "42", Status = ExecutionStatus.Succeeded }));

		// Act
		var output = WebhooksApi.ApplyResponseTemplate(template, result);

		// Assert
		output.Should().Be("The result is: 42");
	}

	[Fact]
	public void ApplyResponseTemplate_MultiplePlaceholders_ReplacesAll()
	{
		// Arrange
		var template = "Step1: {{step1.Content}}, Step2: {{step2.Content}}";
		var result = CreateOrchestrationResult(
			ExecutionStatus.Succeeded,
			("step1", new ExecutionResult { Content = "Hello", Status = ExecutionStatus.Succeeded }),
			("step2", new ExecutionResult { Content = "World", Status = ExecutionStatus.Succeeded }));

		// Act
		var output = WebhooksApi.ApplyResponseTemplate(template, result);

		// Assert
		output.Should().Be("Step1: Hello, Step2: World");
	}

	[Fact]
	public void ApplyResponseTemplate_StatusPlaceholder_ReplacesWithStatus()
	{
		// Arrange
		var template = "Status: {{analyze.Status}}";
		var result = CreateOrchestrationResult(
			ExecutionStatus.Succeeded,
			("analyze", new ExecutionResult { Content = "done", Status = ExecutionStatus.Succeeded }));

		// Act
		var output = WebhooksApi.ApplyResponseTemplate(template, result);

		// Assert
		output.Should().Be("Status: succeeded");
	}

	[Fact]
	public void ApplyResponseTemplate_ErrorPlaceholder_ReplacesWithError()
	{
		// Arrange
		var template = "Error: {{analyze.Error}}";
		var result = CreateOrchestrationResult(
			ExecutionStatus.Failed,
			("analyze", new ExecutionResult { Content = "", Status = ExecutionStatus.Failed, ErrorMessage = "Connection timeout" }));

		// Act
		var output = WebhooksApi.ApplyResponseTemplate(template, result);

		// Assert
		output.Should().Be("Error: Connection timeout");
	}

	[Fact]
	public void ApplyResponseTemplate_ModelPlaceholder_ReplacesWithModel()
	{
		// Arrange
		var template = "Used model: {{analyze.Model}}";
		var result = CreateOrchestrationResult(
			ExecutionStatus.Succeeded,
			("analyze", new ExecutionResult { Content = "done", Status = ExecutionStatus.Succeeded, ActualModel = "claude-opus-4.5" }));

		// Act
		var output = WebhooksApi.ApplyResponseTemplate(template, result);

		// Assert
		output.Should().Be("Used model: claude-opus-4.5");
	}

	[Fact]
	public void ApplyResponseTemplate_UnknownStep_LeavesPlaceholderAsIs()
	{
		// Arrange
		var template = "Result: {{nonexistent.Content}}";
		var result = CreateOrchestrationResult(
			ExecutionStatus.Succeeded,
			("analyze", new ExecutionResult { Content = "done", Status = ExecutionStatus.Succeeded }));

		// Act
		var output = WebhooksApi.ApplyResponseTemplate(template, result);

		// Assert
		output.Should().Be("Result: {{nonexistent.Content}}");
	}

	[Fact]
	public void ApplyResponseTemplate_UnknownProperty_LeavesPlaceholderAsIs()
	{
		// Arrange
		var template = "Result: {{analyze.Unknown}}";
		var result = CreateOrchestrationResult(
			ExecutionStatus.Succeeded,
			("analyze", new ExecutionResult { Content = "done", Status = ExecutionStatus.Succeeded }));

		// Act
		var output = WebhooksApi.ApplyResponseTemplate(template, result);

		// Assert
		output.Should().Be("Result: {{analyze.Unknown}}");
	}

	[Fact]
	public void ApplyResponseTemplate_NullContent_ReplacesWithEmpty()
	{
		// Arrange
		var template = "Result: {{analyze.Content}}";
		var result = CreateOrchestrationResult(
			ExecutionStatus.Succeeded,
			("analyze", new ExecutionResult { Content = null!, Status = ExecutionStatus.Succeeded }));

		// Act
		var output = WebhooksApi.ApplyResponseTemplate(template, result);

		// Assert
		output.Should().Be("Result: ");
	}

	[Fact]
	public void ApplyResponseTemplate_NullError_ReplacesWithEmpty()
	{
		// Arrange
		var template = "Error: {{analyze.Error}}";
		var result = CreateOrchestrationResult(
			ExecutionStatus.Succeeded,
			("analyze", new ExecutionResult { Content = "done", Status = ExecutionStatus.Succeeded }));

		// Act
		var output = WebhooksApi.ApplyResponseTemplate(template, result);

		// Assert
		output.Should().Be("Error: ");
	}

	[Fact]
	public void ApplyResponseTemplate_NoPlaceholders_ReturnsTemplateAsIs()
	{
		// Arrange
		var template = "Hello, world!";
		var result = CreateOrchestrationResult(ExecutionStatus.Succeeded);

		// Act
		var output = WebhooksApi.ApplyResponseTemplate(template, result);

		// Assert
		output.Should().Be("Hello, world!");
	}

	[Fact]
	public void ApplyResponseTemplate_MixedValidAndInvalid_HandlesCorrectly()
	{
		// Arrange
		var template = "Found: {{analyze.Content}}, Missing: {{ghost.Content}}, Status: {{analyze.Status}}";
		var result = CreateOrchestrationResult(
			ExecutionStatus.Succeeded,
			("analyze", new ExecutionResult { Content = "42", Status = ExecutionStatus.Succeeded }));

		// Act
		var output = WebhooksApi.ApplyResponseTemplate(template, result);

		// Assert
		output.Should().Be("Found: 42, Missing: {{ghost.Content}}, Status: succeeded");
	}

	[Fact]
	public void ApplyResponseTemplate_MultilineTemplate_WorksCorrectly()
	{
		// Arrange
		var template = "Summary:\n- Analysis: {{analyze.Content}}\n- Status: {{analyze.Status}}";
		var result = CreateOrchestrationResult(
			ExecutionStatus.Succeeded,
			("analyze", new ExecutionResult { Content = "All clear", Status = ExecutionStatus.Succeeded }));

		// Act
		var output = WebhooksApi.ApplyResponseTemplate(template, result);

		// Assert
		output.Should().Be("Summary:\n- Analysis: All clear\n- Status: succeeded");
	}

	[Fact]
	public void ApplyResponseTemplate_UsesStepResultsForNonTerminalSteps()
	{
		// Arrange: step "intermediate" is in StepResults but NOT in Results (not terminal)
		var template = "Intermediate: {{intermediate.Content}}, Final: {{final.Content}}";
		var stepResults = new Dictionary<string, ExecutionResult>
		{
			["intermediate"] = new ExecutionResult { Content = "mid-result", Status = ExecutionStatus.Succeeded },
			["final"] = new ExecutionResult { Content = "end-result", Status = ExecutionStatus.Succeeded }
		};
		var terminalResults = new Dictionary<string, ExecutionResult>
		{
			["final"] = new ExecutionResult { Content = "end-result", Status = ExecutionStatus.Succeeded }
		};
		var result = new OrchestrationResult
		{
			Status = ExecutionStatus.Succeeded,
			Results = terminalResults,
			StepResults = stepResults,
		};

		// Act
		var output = WebhooksApi.ApplyResponseTemplate(template, result);

		// Assert
		output.Should().Be("Intermediate: mid-result, Final: end-result");
	}

	// ── WebhookResponseConfig Model Tests ──

	[Fact]
	public void WebhookResponseConfig_CustomValues()
	{
		// Act
		var config = new WebhookResponseConfig
		{
			WaitForResult = true,
			ResponseTemplate = "{{step.Content}}",
			TimeoutSeconds = 30,
		};

		// Assert
		config.WaitForResult.Should().BeTrue();
		config.ResponseTemplate.Should().Be("{{step.Content}}");
		config.TimeoutSeconds.Should().Be(30);
	}

	[Fact]
	public void WebhookTriggerConfig_WithResponse_Serialization()
	{
		// Arrange
		var config = new WebhookTriggerConfig
		{
			Type = TriggerType.Webhook,
			Secret = "s3cret",
			MaxConcurrent = 2,
			Response = new WebhookResponseConfig
			{
				WaitForResult = true,
				ResponseTemplate = "Answer: {{final.Content}}",
				TimeoutSeconds = 45,
			}
		};

		// Assert
		config.Type.Should().Be(TriggerType.Webhook);
		config.Secret.Should().Be("s3cret");
		config.MaxConcurrent.Should().Be(2);
		config.Response.Should().NotBeNull();
		config.Response!.WaitForResult.Should().BeTrue();
		config.Response.ResponseTemplate.Should().Be("Answer: {{final.Content}}");
		config.Response.TimeoutSeconds.Should().Be(45);
	}

	// ── OrchestrationParser Webhook Parsing Tests ──

	[Fact]
	public void ParseOrchestration_WebhookTrigger_WithResponseConfig()
	{
		// Arrange
		var json = """
		{
			"name": "Test Orchestration",
			"description": "Test orchestration for webhook parsing",
			"steps": [
				{
					"name": "step1",
					"type": "prompt",
					"systemPrompt": "You are helpful",
					"userPrompt": "Hello",
					"model": "gpt-4o-mini"
				}
			],
			"trigger": {
				"type": "webhook",
				"secret": "test-secret",
				"maxConcurrent": 5,
				"response": {
					"waitForResult": true,
					"responseTemplate": "Result: {{step1.Content}}",
					"timeoutSeconds": 60
				}
			}
		}
		""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationMetadataOnly(json);

		// Assert
		orchestration.Trigger.Should().NotBeNull();
		orchestration.Trigger.Should().BeOfType<WebhookTriggerConfig>();
		var webhookConfig = (WebhookTriggerConfig)orchestration.Trigger!;
		webhookConfig.Secret.Should().Be("test-secret");
		webhookConfig.MaxConcurrent.Should().Be(5);
		webhookConfig.Response.Should().NotBeNull();
		webhookConfig.Response!.WaitForResult.Should().BeTrue();
		webhookConfig.Response.ResponseTemplate.Should().Be("Result: {{step1.Content}}");
		webhookConfig.Response.TimeoutSeconds.Should().Be(60);
	}

	[Fact]
	public void ParseOrchestration_WebhookTrigger_WithoutResponseConfig()
	{
		// Arrange
		var json = """
		{
			"name": "Test Orchestration",
			"description": "Test orchestration for webhook parsing",
			"steps": [
				{
					"name": "step1",
					"type": "prompt",
					"systemPrompt": "You are helpful",
					"userPrompt": "Hello",
					"model": "gpt-4o-mini"
				}
			],
			"trigger": {
				"type": "webhook",
				"secret": "my-secret"
			}
		}
		""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationMetadataOnly(json);

		// Assert
		orchestration.Trigger.Should().NotBeNull();
		orchestration.Trigger.Should().BeOfType<WebhookTriggerConfig>();
		var webhookConfig = (WebhookTriggerConfig)orchestration.Trigger!;
		webhookConfig.Secret.Should().Be("my-secret");
		webhookConfig.MaxConcurrent.Should().Be(1);
		webhookConfig.Response.Should().BeNull();
	}

	[Fact]
	public void ParseOrchestration_WebhookTrigger_ResponseDefaults()
	{
		// Arrange — response block with only waitForResult, others use defaults
		var json = """
		{
			"name": "Test Orchestration",
			"description": "Test orchestration for webhook parsing",
			"steps": [
				{
					"name": "step1",
					"type": "prompt",
					"systemPrompt": "You are helpful",
					"userPrompt": "Hello",
					"model": "gpt-4o-mini"
				}
			],
			"trigger": {
				"type": "webhook",
				"response": {
					"waitForResult": true
				}
			}
		}
		""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationMetadataOnly(json);

		// Assert
		var webhookConfig = (WebhookTriggerConfig)orchestration.Trigger!;
		webhookConfig.Response.Should().NotBeNull();
		webhookConfig.Response!.WaitForResult.Should().BeTrue();
		webhookConfig.Response.ResponseTemplate.Should().BeNull();
		webhookConfig.Response.TimeoutSeconds.Should().Be(120); // default
	}

	[Fact]
	public void ParseOrchestration_WebhookTrigger_ResponseWaitForResultFalse()
	{
		// Arrange
		var json = """
		{
			"name": "Test Orchestration",
			"description": "Test orchestration for webhook parsing",
			"steps": [
				{
					"name": "step1",
					"type": "prompt",
					"systemPrompt": "You are helpful",
					"userPrompt": "Hello",
					"model": "gpt-4o-mini"
				}
			],
			"trigger": {
				"type": "webhook",
				"response": {
					"waitForResult": false,
					"timeoutSeconds": 30
				}
			}
		}
		""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationMetadataOnly(json);

		// Assert
		var webhookConfig = (WebhookTriggerConfig)orchestration.Trigger!;
		webhookConfig.Response.Should().NotBeNull();
		webhookConfig.Response!.WaitForResult.Should().BeFalse();
		webhookConfig.Response.TimeoutSeconds.Should().Be(30);
	}

	// ── Helper Methods ──

	private static OrchestrationResult CreateOrchestrationResult(
		ExecutionStatus status,
		params (string Name, ExecutionResult Result)[] steps)
	{
		var stepResults = new Dictionary<string, ExecutionResult>();
		var terminalResults = new Dictionary<string, ExecutionResult>();

		foreach (var (name, result) in steps)
		{
			stepResults[name] = result;
			terminalResults[name] = result; // Treat all as terminal for simplicity
		}

		return new OrchestrationResult
		{
			Status = status,
			Results = terminalResults,
			StepResults = stepResults,
		};
	}
}
