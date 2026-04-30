using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Host.Api;
using Orchestra.Host.Middleware;
using Orchestra.Host.Profiles;
using Orchestra.Host.Registry;
using Orchestra.Host.Triggers;
using Xunit;

namespace Orchestra.Host.Tests;

/// <summary>
/// Integration tests for the OrchestrationsApi endpoints.
/// Uses TestServer to exercise the full HTTP pipeline including
/// JSON serialization and error handling.
/// </summary>
public class OrchestrationsApiTests : IDisposable
{
	private readonly string _tempDir;
	private readonly string _runsDir;
	private readonly string _persistPath;

	public OrchestrationsApiTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "Orchestra.OrchestrationsApiTests", Guid.NewGuid().ToString("N"));
		_runsDir = Path.Combine(_tempDir, "runs");
		_persistPath = Path.Combine(_tempDir, "registered-orchestrations.json");
		Directory.CreateDirectory(_runsDir);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempDir))
			Directory.Delete(_tempDir, true);
	}

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
		Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
	};

	private IHost CreateTestHost(OrchestrationRegistry registry)
	{
		var loggerFactory = NullLoggerFactory.Instance;
		var scheduler = new OrchestrationScheduler();
		var triggerManager = new TriggerManager(
			new System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource>(),
			new System.Collections.Concurrent.ConcurrentDictionary<string, ActiveExecutionInfo>(),
			agentBuilder: null!,
			scheduler: scheduler,
			loggerFactory: loggerFactory,
			logger: new NullLogger<TriggerManager>(),
			runsDir: _runsDir,
			runStore: null!,
			checkpointStore: null!,
			launcher: null!
		);

		var host = new HostBuilder()
			.ConfigureWebHost(webHost =>
			{
				webHost.UseTestServer();
				webHost.ConfigureServices(services =>
				{
					services.AddRouting();
					services.AddSingleton(registry);
					services.AddSingleton<IScheduler>(scheduler);
					services.AddSingleton(triggerManager);
					services.AddSingleton(new Orchestra.Host.Hosting.OrchestrationHostOptions());
					services.AddSingleton(new OrchestrationTagStore(_tempDir, NullLoggerFactory.Instance.CreateLogger<OrchestrationTagStore>()));
				});
				webHost.Configure(app =>
				{
					app.UseMiddleware<ProblemDetailsExceptionMiddleware>();
					app.UseRouting();
					app.UseEndpoints(endpoints =>
					{
						endpoints.MapOrchestrationsApi(JsonOptions);
					});
				});
			})
			.Build();

		host.Start();
		return host;
	}

	private string CreateOrchestrationFile(string name, string content)
	{
		var path = Path.Combine(_tempDir, $"{name}.json");
		File.WriteAllText(path, content);
		return path;
	}

	[Fact]
	public async Task GetOrchestrationById_WithInvalidDependency_ReturnsOrchestrationWithValidationErrors()
	{
		// Arrange - create an orchestration where a step depends on a non-existent step
		var orchestrationJson = """
		{
			"name": "test-invalid-deps",
			"description": "Orchestration with invalid step dependency",
			"steps": [
				{
					"name": "step-a",
					"type": "prompt",
					"model": "claude-opus-4.5",
					"systemPrompt": "You are a helpful assistant.",
					"userPrompt": "Do something"
				},
				{
					"name": "step-b",
					"type": "prompt",
					"model": "claude-opus-4.5",
					"dependsOn": ["non-existent-step"],
					"systemPrompt": "You are a helpful assistant.",
					"userPrompt": "Do something else"
				}
			]
		}
		""";

		var path = CreateOrchestrationFile("test-invalid-deps", orchestrationJson);
		var registry = new OrchestrationRegistry(_persistPath, NullLoggerFactory.Instance.CreateLogger<OrchestrationRegistry>());
		var entry = registry.Register(path, null, persist: false);

		using var host = CreateTestHost(registry);
		var client = host.GetTestClient();

		// Act
		var response = await client.GetAsync($"/api/orchestrations/{entry.Id}");

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK, "the endpoint should return the orchestration even with invalid dependencies");

		var json = await response.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;

		// Verify orchestration data is still returned
		root.GetProperty("name").GetString().Should().Be("test-invalid-deps");
		root.GetProperty("steps").GetArrayLength().Should().Be(2);

		// Verify validation errors are included
		root.TryGetProperty("validationErrors", out var errorsElement).Should().BeTrue();
		errorsElement.GetArrayLength().Should().Be(1);
		errorsElement[0].GetString().Should().Contain("non-existent-step");
		errorsElement[0].GetString().Should().Contain("does not exist");

		// Verify layers is empty (schedule could not be computed)
		root.GetProperty("layers").GetArrayLength().Should().Be(0);
	}

	[Fact]
	public async Task GetOrchestrationById_WithValidDependencies_ReturnsOrchestrationsWithNoValidationErrors()
	{
		// Arrange - create a valid orchestration
		var orchestrationJson = """
		{
			"name": "test-valid-deps",
			"description": "Orchestration with valid step dependencies",
			"steps": [
				{
					"name": "step-a",
					"type": "prompt",
					"model": "claude-opus-4.5",
					"systemPrompt": "You are a helpful assistant.",
					"userPrompt": "Do something"
				},
				{
					"name": "step-b",
					"type": "prompt",
					"model": "claude-opus-4.5",
					"dependsOn": ["step-a"],
					"systemPrompt": "You are a helpful assistant.",
					"userPrompt": "Do something else"
				}
			]
		}
		""";

		var path = CreateOrchestrationFile("test-valid-deps", orchestrationJson);
		var registry = new OrchestrationRegistry(_persistPath, NullLoggerFactory.Instance.CreateLogger<OrchestrationRegistry>());
		var entry = registry.Register(path, null, persist: false);

		using var host = CreateTestHost(registry);
		var client = host.GetTestClient();

		// Act
		var response = await client.GetAsync($"/api/orchestrations/{entry.Id}");

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK);

		var json = await response.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;

		// Verify orchestration data
		root.GetProperty("name").GetString().Should().Be("test-valid-deps");
		root.GetProperty("steps").GetArrayLength().Should().Be(2);

		// Verify no validation errors (null is omitted from JSON due to WhenWritingNull)
		root.TryGetProperty("validationErrors", out _).Should().BeFalse(
			"validationErrors should be omitted when null (JsonIgnoreCondition.WhenWritingNull)");

		// Verify layers are computed correctly
		root.GetProperty("layers").GetArrayLength().Should().Be(2, "step-a and step-b should be in separate layers");
	}

	[Fact]
	public async Task GetOrchestrationById_WithCircularDependency_ReturnsOrchestrationsWithValidationErrors()
	{
		// Arrange - create an orchestration with circular dependencies
		var orchestrationJson = """
		{
			"name": "test-circular-deps",
			"description": "Orchestration with circular dependency",
			"steps": [
				{
					"name": "step-a",
					"type": "prompt",
					"model": "claude-opus-4.5",
					"dependsOn": ["step-b"],
					"systemPrompt": "You are a helpful assistant.",
					"userPrompt": "Do something"
				},
				{
					"name": "step-b",
					"type": "prompt",
					"model": "claude-opus-4.5",
					"dependsOn": ["step-a"],
					"systemPrompt": "You are a helpful assistant.",
					"userPrompt": "Do something else"
				}
			]
		}
		""";

		var path = CreateOrchestrationFile("test-circular-deps", orchestrationJson);
		var registry = new OrchestrationRegistry(_persistPath, NullLoggerFactory.Instance.CreateLogger<OrchestrationRegistry>());
		var entry = registry.Register(path, null, persist: false);

		using var host = CreateTestHost(registry);
		var client = host.GetTestClient();

		// Act
		var response = await client.GetAsync($"/api/orchestrations/{entry.Id}");

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.OK, "the endpoint should return the orchestration even with circular dependencies");

		var json = await response.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;

		// Verify orchestration data is still returned
		root.GetProperty("name").GetString().Should().Be("test-circular-deps");

		// Verify validation errors
		root.TryGetProperty("validationErrors", out var errorsElement).Should().BeTrue();
		errorsElement.GetArrayLength().Should().Be(1);
		errorsElement[0].GetString().Should().Contain("Circular dependency");
	}

	[Fact]
	public async Task GetOrchestrationById_NotFound_Returns404ProblemDetails()
	{
		// Arrange
		var registry = new OrchestrationRegistry(_persistPath, NullLoggerFactory.Instance.CreateLogger<OrchestrationRegistry>());

		using var host = CreateTestHost(registry);
		var client = host.GetTestClient();

		// Act
		var response = await client.GetAsync("/api/orchestrations/does-not-exist-1234");

		// Assert
		response.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task GetOrchestrationById_WithHooks_ReturnsHookMetadata()
	{
		var orchestrationJson = """
		{
			"name": "test-hooks",
			"description": "Orchestration with hooks",
			"hooks": [
				{
					"name": "notify-failure",
					"on": "step.failure",
					"payload": { "detail": "compact", "steps": "current" },
					"action": {
						"type": "script",
						"shell": "pwsh",
						"scriptFile": "hooks/write.ps1"
					}
				}
			],
			"steps": []
		}
		""";

		var path = CreateOrchestrationFile("test-hooks", orchestrationJson);
		var registry = new OrchestrationRegistry(_persistPath, NullLoggerFactory.Instance.CreateLogger<OrchestrationRegistry>());
		var entry = registry.Register(path, null, persist: false);

		using var host = CreateTestHost(registry);
		var client = host.GetTestClient();

		var response = await client.GetAsync($"/api/orchestrations/{entry.Id}");

		response.StatusCode.Should().Be(HttpStatusCode.OK);

		var json = await response.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(json);
		var hooks = doc.RootElement.GetProperty("hooks");
		hooks.GetArrayLength().Should().Be(1);
		hooks[0].GetProperty("name").GetString().Should().Be("notify-failure");
		hooks[0].GetProperty("eventType").GetString().Should().Be("StepFailure");
		hooks[0].GetProperty("action").GetProperty("type").GetString().Should().Be("Script");
	}
}
