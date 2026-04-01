using System.Text.Json;
using FluentAssertions;

namespace Orchestra.Engine.Tests.Serialization;

public class HttpStepParsingTests
{
	private static readonly StepParseContext s_context = new(BaseDirectory: null);
	[Fact]
	public void Parse_MinimalHttpStep_SetsDefaults()
	{
		// Arrange
		var json = JsonSerializer.Deserialize<JsonElement>("""
			{
				"name": "api-call",
				"type": "Http",
				"url": "https://api.example.com/data"
			}
			""");
		var parser = new HttpStepTypeParser();

		// Act
		var step = parser.Parse(json, s_context) as HttpOrchestrationStep;

		// Assert
		step.Should().NotBeNull();
		step!.Name.Should().Be("api-call");
		step.Type.Should().Be(OrchestrationStepType.Http);
		step.Url.Should().Be("https://api.example.com/data");
		step.Method.Should().Be("GET");
		step.ContentType.Should().Be("application/json");
		step.DependsOn.Should().BeEmpty();
		step.Headers.Should().BeEmpty();
		step.Body.Should().BeNull();
		step.TimeoutSeconds.Should().BeNull();
		step.Retry.Should().BeNull();
		step.Parameters.Should().BeEmpty();
	}

	[Fact]
	public void Parse_FullHttpStep_AllPropertiesSet()
	{
		// Arrange
		var json = JsonSerializer.Deserialize<JsonElement>("""
			{
				"name": "full-api-call",
				"type": "Http",
				"url": "https://api.example.com/submit",
				"method": "POST",
				"headers": {
					"Authorization": "Bearer token123",
					"X-Custom": "value"
				},
				"body": "{\"key\": \"value\"}",
				"contentType": "application/xml",
				"dependsOn": ["step1", "step2"],
				"timeoutSeconds": 30,
				"retry": {
					"maxRetries": 5,
					"backoffSeconds": 2.0,
					"backoffMultiplier": 3.0,
					"retryOnTimeout": false
				},
				"parameters": ["apiKey", "userId"]
			}
			""");
		var parser = new HttpStepTypeParser();

		// Act
		var step = parser.Parse(json, s_context) as HttpOrchestrationStep;

		// Assert
		step.Should().NotBeNull();
		step!.Name.Should().Be("full-api-call");
		step.Type.Should().Be(OrchestrationStepType.Http);
		step.Url.Should().Be("https://api.example.com/submit");
		step.Method.Should().Be("POST");
		step.Headers.Should().HaveCount(2);
		step.Headers["Authorization"].Should().Be("Bearer token123");
		step.Headers["X-Custom"].Should().Be("value");
		step.Body.Should().Be("{\"key\": \"value\"}");
		step.ContentType.Should().Be("application/xml");
		step.DependsOn.Should().BeEquivalentTo(["step1", "step2"]);
		step.TimeoutSeconds.Should().Be(30);
		step.Retry.Should().NotBeNull();
		step.Retry!.MaxRetries.Should().Be(5);
		step.Retry.BackoffSeconds.Should().Be(2.0);
		step.Retry.BackoffMultiplier.Should().Be(3.0);
		step.Retry.RetryOnTimeout.Should().BeFalse();
		step.Parameters.Should().BeEquivalentTo(["apiKey", "userId"]);
	}

	[Fact]
	public void Parse_PostWithBody_SetsBodyAndMethod()
	{
		// Arrange
		var json = JsonSerializer.Deserialize<JsonElement>("""
			{
				"name": "post-call",
				"type": "Http",
				"url": "https://api.example.com/create",
				"method": "POST",
				"body": "{\"title\": \"New Item\", \"count\": 42}"
			}
			""");
		var parser = new HttpStepTypeParser();

		// Act
		var step = parser.Parse(json, s_context) as HttpOrchestrationStep;

		// Assert
		step.Should().NotBeNull();
		step!.Method.Should().Be("POST");
		step.Body.Should().Be("{\"title\": \"New Item\", \"count\": 42}");
		step.ContentType.Should().Be("application/json");
	}

	[Fact]
	public void Parse_WithHeaders_DeserializesHeaders()
	{
		// Arrange
		var json = JsonSerializer.Deserialize<JsonElement>("""
			{
				"name": "headers-call",
				"type": "Http",
				"url": "https://api.example.com/data",
				"headers": {
					"Authorization": "Bearer {{param.token}}",
					"Accept": "application/json",
					"X-Request-Id": "abc-123"
				}
			}
			""");
		var parser = new HttpStepTypeParser();

		// Act
		var step = parser.Parse(json, s_context) as HttpOrchestrationStep;

		// Assert
		step.Should().NotBeNull();
		step!.Headers.Should().HaveCount(3);
		step.Headers["Authorization"].Should().Be("Bearer {{param.token}}");
		step.Headers["Accept"].Should().Be("application/json");
		step.Headers["X-Request-Id"].Should().Be("abc-123");
	}

	[Fact]
	public void Parse_WithRetry_DeserializesRetryPolicy()
	{
		// Arrange
		var json = JsonSerializer.Deserialize<JsonElement>("""
			{
				"name": "retry-call",
				"type": "Http",
				"url": "https://api.example.com/data",
				"retry": {
					"maxRetries": 3,
					"backoffSeconds": 1.5,
					"backoffMultiplier": 2.5,
					"retryOnTimeout": true
				}
			}
			""");
		var parser = new HttpStepTypeParser();

		// Act
		var step = parser.Parse(json, s_context) as HttpOrchestrationStep;

		// Assert
		step.Should().NotBeNull();
		step!.Retry.Should().NotBeNull();
		step.Retry!.MaxRetries.Should().Be(3);
		step.Retry.BackoffSeconds.Should().Be(1.5);
		step.Retry.BackoffMultiplier.Should().Be(2.5);
		step.Retry.RetryOnTimeout.Should().BeTrue();
	}

	[Fact]
	public void ParseOrchestration_WithHttpStep_ParsesCorrectly()
	{
		// Arrange
		var json = """
			{
				"name": "http-orchestration",
				"description": "Orchestration with an HTTP step",
				"steps": [
					{
						"name": "api-call",
						"type": "Http",
						"method": "POST",
						"url": "https://api.example.com/data",
						"body": "{\"key\": \"value\"}",
						"headers": {
							"Content-Type": "application/json"
						},
						"timeoutSeconds": 15
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationMetadataOnly(json);

		// Assert
		orchestration.Name.Should().Be("http-orchestration");
		orchestration.Description.Should().Be("Orchestration with an HTTP step");
		orchestration.Steps.Should().HaveCount(1);

		var step = orchestration.Steps[0] as HttpOrchestrationStep;
		step.Should().NotBeNull();
		step!.Name.Should().Be("api-call");
		step.Type.Should().Be(OrchestrationStepType.Http);
		step.Method.Should().Be("POST");
		step.Url.Should().Be("https://api.example.com/data");
		step.Body.Should().Be("{\"key\": \"value\"}");
		step.Headers["Content-Type"].Should().Be("application/json");
		step.DependsOn.Should().BeEmpty();
		step.TimeoutSeconds.Should().Be(15);
	}

	[Fact]
	public void ParseOrchestration_HttpStepWithoutMethod_DefaultsToGet()
	{
		// Arrange
		var json = """
			{
				"name": "http-no-method",
				"description": "HTTP step without explicit method",
				"steps": [
					{
						"name": "api-call",
						"type": "Http",
						"url": "https://api.example.com/data"
					}
				]
			}
			""";

		// Act
		var orchestration = OrchestrationParser.ParseOrchestrationMetadataOnly(json);

		// Assert
		var step = orchestration.Steps[0] as HttpOrchestrationStep;
		step.Should().NotBeNull();
		step!.Method.Should().Be("GET");
		step.DependsOn.Should().BeEmpty();
	}
}
