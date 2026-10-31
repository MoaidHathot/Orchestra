using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Orchestra.Host.Hosting;
using Orchestra.Host.Registry;
using Xunit;

namespace Orchestra.Portal.Tests;

[Collection(PortalHostEnvironmentCollection.Name)]
public class WorkspaceAssistantTests
{
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task Authoring_UsesTheNormalEngineAndAValidatedWorkspaceSave(bool valid)
	{
		using var factory = new PortalWebApplicationFactory();
		using var client = factory.CreateClient();
		var response = await client.PostAsync("/api/workspace/assistants/create-orchestration", null);
		response.EnsureSuccessStatusCode();
		var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
		var registry = factory.Services.GetRequiredService<OrchestrationRegistry>();
		var definition = registry.Get(id)!.Orchestration;
		definition.Steps.Should().OnlyContain(s => s.Type == OrchestrationStepType.Prompt || s.Type == OrchestrationStepType.Http);
		var candidate = JsonSerializer.Serialize(new { name = "created-by-assistant", description = "Generated", steps = new[] { new { name = "answer", type = "Transform", template = valid ? "Created!" : "{{missing.output}}" } } });
		var agent = new DraftBuilder(candidate);
		var providers = new SingleAgentProviderRegistry(agent, "copilot");
		using var http = new HttpClient(new ForwardToPortal(client));
		var tools = factory.Services.GetRequiredService<EngineToolRegistry>();
		var prompt = new PromptExecutor(providers, NullOrchestrationReporter.Instance, DefaultPromptFormatter.Instance, NullLogger<PromptExecutor>.Instance, tools);
		var executors = new StepExecutorRegistry().Register(new PromptStepExecutor(prompt))
			.Register(new HttpStepExecutor(http, NullOrchestrationReporter.Instance, NullLogger<HttpStepExecutor>.Instance));
		var engine = new OrchestrationExecutor(new OrchestrationScheduler(), providers, NullOrchestrationReporter.Instance,
			NullLoggerFactory.Instance, stepExecutorRegistry: executors, engineToolRegistry: tools);
		var result = await engine.ExecuteAsync(definition, new() { ["description"] = "Create a constant-output workflow" });
		result.Status.Should().Be(valid ? ExecutionStatus.Succeeded : ExecutionStatus.Failed);
		var created = registry.GetByIdOrName("created-by-assistant");
		if (valid)
		{
			created.Should().NotBeNull();
			created!.SourcePath.Should().StartWith(factory.TestDataPath);
			var run = await new OrchestrationExecutor(new OrchestrationScheduler(), agent, NullOrchestrationReporter.Instance, NullLoggerFactory.Instance).ExecuteAsync(created.Orchestration);
			run.Results["answer"].Content.Should().Be("Created!");
		}
		else created.Should().BeNull();
		agent.Tools.Should().Contain("orchestra_validate_orchestration");
	}

	[Fact]
	public async Task Builtins_UseCurrentDefaultsAndSettingsFlowHasAPreviewAndApproval()
	{
		using var factory = new PortalWebApplicationFactory();
		using var client = factory.CreateClient();
		var options = factory.Services.GetRequiredService<OrchestrationHostOptions>();
		options.Provider = "opencode"; options.DefaultModel = "provider/chosen-model";
		var response = await client.PostAsync("/api/workspace/assistants/configure-orchestra", null);
		response.EnsureSuccessStatusCode();
		var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
		var definition = factory.Services.GetRequiredService<OrchestrationRegistry>().Get(id)!.Orchestration;
		definition.DefaultModel.Should().Be("provider/chosen-model");
		definition.DefaultProvider.Should().Be("opencode");
		definition.Steps.Should().Contain(s => s.Type == OrchestrationStepType.Approval);
		new OrchestrationScheduler().Schedule(definition);
		TemplateExpressionValidator.ValidateOrchestration(definition).IsValid.Should().BeTrue();
	}

	private sealed class DraftBuilder(string content) : AgentBuilder
	{
		public string[] Tools = [];
		public override AgentProviderCapabilities GetCapabilities() => AgentProviderCapabilities.All("copilot");
		public override Task<IAgent> BuildAgentAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public override Task<IAgent> BuildAgentAsync(AgentBuildConfig config, CancellationToken cancellationToken = default)
		{
			Tools = config.EngineTools.Select(t => t.Name).ToArray();
			return Task.FromResult<IAgent>(new Agent(content));
		}
		private sealed class Agent(string content) : IAgent
		{
			public AgentTask SendAsync(string prompt, CancellationToken cancellationToken = default)
			{
				var events = Channel.CreateUnbounded<AgentEvent>(); events.Writer.TryComplete();
				return new(events.Reader, Task.FromResult(new AgentResult { Content = content }));
			}
		}
	}
	private sealed class ForwardToPortal(HttpClient client) : HttpMessageHandler
	{
		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var copy = new HttpRequestMessage(request.Method, request.RequestUri!.PathAndQuery);
			if (request.Content is not null) copy.Content = new StringContent(await request.Content.ReadAsStringAsync(cancellationToken), System.Text.Encoding.UTF8, "application/json");
			return await client.SendAsync(copy, cancellationToken);
		}
	}
}
