using FluentAssertions;

namespace Orchestra.Engine.Tests.Storage;

public class TemplateResolutionTrackerTests
{
	[Fact]
	public void TrackEnvironmentVariable_StoresNameAndValue()
	{
		var tracker = new TemplateResolutionTracker();

		tracker.TrackEnvironmentVariable("API_KEY", "secret123");

		tracker.AccessedEnvironmentVariables.Should().ContainKey("API_KEY")
			.WhoseValue.Should().Be("secret123");
	}

	[Fact]
	public void TrackEnvironmentVariable_StoresNullForMissingVar()
	{
		var tracker = new TemplateResolutionTracker();

		tracker.TrackEnvironmentVariable("MISSING_VAR", null);

		tracker.AccessedEnvironmentVariables.Should().ContainKey("MISSING_VAR")
			.WhoseValue.Should().BeNull();
	}

	[Fact]
	public void TrackEnvironmentVariable_DuplicateKey_KeepsFirstValue()
	{
		var tracker = new TemplateResolutionTracker();

		tracker.TrackEnvironmentVariable("VAR", "first");
		tracker.TrackEnvironmentVariable("VAR", "second");

		tracker.AccessedEnvironmentVariables["VAR"].Should().Be("first");
	}

	[Fact]
	public void TrackResolvedVariable_StoresResolvedValue()
	{
		var tracker = new TemplateResolutionTracker();

		tracker.TrackResolvedVariable("baseUrl", "https://api.example.com");

		tracker.ResolvedVariables.Should().ContainKey("baseUrl")
			.WhoseValue.Should().Be("https://api.example.com");
	}

	[Fact]
	public void TrackResolvedVariable_OverwritesPreviousValue()
	{
		var tracker = new TemplateResolutionTracker();

		tracker.TrackResolvedVariable("var1", "old");
		tracker.TrackResolvedVariable("var1", "new");

		tracker.ResolvedVariables["var1"].Should().Be("new");
	}

	[Fact]
	public void EmptyTracker_HasEmptyDictionaries()
	{
		var tracker = new TemplateResolutionTracker();

		tracker.AccessedEnvironmentVariables.Should().BeEmpty();
		tracker.ResolvedVariables.Should().BeEmpty();
	}

	[Fact]
	public void TrackMultipleEnvironmentVariables_AllStored()
	{
		var tracker = new TemplateResolutionTracker();

		tracker.TrackEnvironmentVariable("VAR1", "value1");
		tracker.TrackEnvironmentVariable("VAR2", "value2");
		tracker.TrackEnvironmentVariable("VAR3", null);

		tracker.AccessedEnvironmentVariables.Should().HaveCount(3);
		tracker.AccessedEnvironmentVariables["VAR1"].Should().Be("value1");
		tracker.AccessedEnvironmentVariables["VAR2"].Should().Be("value2");
		tracker.AccessedEnvironmentVariables["VAR3"].Should().BeNull();
	}

	[Fact]
	public void IsThreadSafe_ConcurrentAccess()
	{
		var tracker = new TemplateResolutionTracker();
		var tasks = new List<Task>();

		for (int i = 0; i < 100; i++)
		{
			var index = i;
			tasks.Add(Task.Run(() =>
			{
				tracker.TrackEnvironmentVariable($"ENV_{index}", $"val_{index}");
				tracker.TrackResolvedVariable($"VAR_{index}", $"resolved_{index}");
			}));
		}

		Task.WaitAll(tasks.ToArray());

		tracker.AccessedEnvironmentVariables.Should().HaveCount(100);
		tracker.ResolvedVariables.Should().HaveCount(100);
	}
}

public class RunContextTests
{
	[Fact]
	public void RunContext_RequiredProperties_AreSet()
	{
		var context = new RunContext
		{
			RunId = "run-123",
			OrchestrationName = "test-orch",
			OrchestrationVersion = "1.0.0",
			StartedAt = DateTimeOffset.UtcNow
		};

		context.RunId.Should().Be("run-123");
		context.OrchestrationName.Should().Be("test-orch");
		context.OrchestrationVersion.Should().Be("1.0.0");
	}

	[Fact]
	public void RunContext_DefaultValues_AreCorrect()
	{
		var context = new RunContext
		{
			RunId = "run-1",
			OrchestrationName = "test",
			OrchestrationVersion = "1.0",
			StartedAt = DateTimeOffset.UtcNow
		};

		context.TriggeredBy.Should().Be("manual");
		context.TriggerId.Should().BeNull();
		context.Parameters.Should().BeEmpty();
		context.Variables.Should().BeEmpty();
		context.ResolvedVariables.Should().BeEmpty();
		context.AccessedEnvironmentVariables.Should().BeEmpty();
		context.DataDirectory.Should().BeNull();
	}

	[Fact]
	public void RunContext_WithAllFields_RoundTrips()
	{
		var now = DateTimeOffset.UtcNow;
		var context = new RunContext
		{
			RunId = "run-abc",
			OrchestrationName = "my-orchestration",
			OrchestrationVersion = "2.1.0",
			StartedAt = now,
			TriggeredBy = "webhook",
			TriggerId = "trigger-xyz",
			Parameters = new Dictionary<string, string>
			{
				["topic"] = "AI",
				["format"] = "markdown"
			},
			Variables = new Dictionary<string, string>
			{
				["baseUrl"] = "{{env.API_URL}}/v1"
			},
			ResolvedVariables = new Dictionary<string, string>
			{
				["baseUrl"] = "https://api.example.com/v1"
			},
			AccessedEnvironmentVariables = new Dictionary<string, string?>
			{
				["API_URL"] = "https://api.example.com",
				["MISSING"] = null
			},
			DataDirectory = @"C:\data\executions\my-orchestration\run-abc"
		};

		context.RunId.Should().Be("run-abc");
		context.OrchestrationName.Should().Be("my-orchestration");
		context.OrchestrationVersion.Should().Be("2.1.0");
		context.StartedAt.Should().Be(now);
		context.TriggeredBy.Should().Be("webhook");
		context.TriggerId.Should().Be("trigger-xyz");
		context.Parameters.Should().HaveCount(2);
		context.Parameters["topic"].Should().Be("AI");
		context.Variables.Should().HaveCount(1);
		context.ResolvedVariables.Should().HaveCount(1);
		context.ResolvedVariables["baseUrl"].Should().Be("https://api.example.com/v1");
		context.AccessedEnvironmentVariables.Should().HaveCount(2);
		context.AccessedEnvironmentVariables["API_URL"].Should().Be("https://api.example.com");
		context.AccessedEnvironmentVariables["MISSING"].Should().BeNull();
		context.DataDirectory.Should().Be(@"C:\data\executions\my-orchestration\run-abc");
	}
}

public class TemplateResolverTrackerIntegrationTests
{
	private static readonly OrchestrationInfo s_defaultInfo = new("test-orchestration", "1.0.0", "run123", DateTimeOffset.UtcNow);

	private static readonly TransformOrchestrationStep s_defaultStep = new()
	{
		Name = "current-step",
		Type = OrchestrationStepType.Transform,
		DependsOn = [],
		Template = ""
	};

	[Fact]
	public void Resolve_EnvExpression_TracksAccessedEnvVar()
	{
		// Arrange
		var uniqueEnvVar = $"ORCHESTRA_TEST_{Guid.NewGuid():N}";
		Environment.SetEnvironmentVariable(uniqueEnvVar, "test-value");

		try
		{
			var context = new OrchestrationExecutionContext
			{
				OrchestrationInfo = s_defaultInfo,
				Parameters = new Dictionary<string, string>()
			};

			var template = $"URL is {{{{env.{uniqueEnvVar}}}}}";

			// Act
			var result = TemplateResolver.Resolve(template, new Dictionary<string, string>(), context, [], s_defaultStep);

			// Assert
			result.Should().Be("URL is test-value");
			context.ResolutionTracker.AccessedEnvironmentVariables.Should().ContainKey(uniqueEnvVar)
				.WhoseValue.Should().Be("test-value");
		}
		finally
		{
			Environment.SetEnvironmentVariable(uniqueEnvVar, null);
		}
	}

	[Fact]
	public void Resolve_MissingEnvVar_TracksAsNull()
	{
		// Arrange
		var uniqueEnvVar = $"ORCHESTRA_MISSING_{Guid.NewGuid():N}";
		// Ensure it's not set
		Environment.SetEnvironmentVariable(uniqueEnvVar, null);

		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>()
		};

		var template = $"URL is {{{{env.{uniqueEnvVar}}}}}";

		// Act
		var result = TemplateResolver.Resolve(template, new Dictionary<string, string>(), context, [], s_defaultStep);

		// Assert — unresolvable env vars are left as-is
		result.Should().Contain(uniqueEnvVar);
		context.ResolutionTracker.AccessedEnvironmentVariables.Should().ContainKey(uniqueEnvVar)
			.WhoseValue.Should().BeNull();
	}

	[Fact]
	public void Resolve_VariableWithTemplateExpansion_TracksResolvedVariable()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
			Variables = new Dictionary<string, string>
			{
				["greeting"] = "Hello {{param.name}}"
			}
		};

		var parameters = new Dictionary<string, string> { ["name"] = "World" };
		var template = "Message: {{vars.greeting}}";

		// Act
		var result = TemplateResolver.Resolve(template, parameters, context, [], s_defaultStep);

		// Assert
		result.Should().Be("Message: Hello World");
		context.ResolutionTracker.ResolvedVariables.Should().ContainKey("greeting")
			.WhoseValue.Should().Be("Hello World");
	}

	[Fact]
	public void Resolve_VariableWithoutExpansion_DoesNotTrackResolvedVariable()
	{
		// Arrange
		var context = new OrchestrationExecutionContext
		{
			OrchestrationInfo = s_defaultInfo,
			Parameters = new Dictionary<string, string>(),
			Variables = new Dictionary<string, string>
			{
				["staticVar"] = "plain value"
			}
		};

		var template = "Value: {{vars.staticVar}}";

		// Act
		var result = TemplateResolver.Resolve(template, new Dictionary<string, string>(), context, [], s_defaultStep);

		// Assert
		result.Should().Be("Value: plain value");
		// Static variables (no template expansion needed) should NOT be in resolvedVariables
		context.ResolutionTracker.ResolvedVariables.Should().NotContainKey("staticVar");
	}
}
