using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine.Tests.TestHelpers;

namespace Orchestra.Engine.Tests.Executor;

public class ExecutionWorkingDirectoryTests
{
	[Theory]
	[InlineData(null)]
	[InlineData("nested")]
	public async Task PromptCommandAndScript_UseDefinitionBase(string? configured)
	{
		var root = Directory.CreateTempSubdirectory("orchestra-cwd-").FullName;
		try
		{
			Directory.CreateDirectory(Path.Combine(root, "nested"));
			var expected = configured is null ? root : Path.Combine(root, configured);
			var marker = Guid.NewGuid().ToString("N");
			await File.WriteAllTextAsync(Path.Combine(expected, marker), marker);
			var context = new OrchestrationExecutionContext { OrchestrationInfo = new("cwd", "1", "run", DateTimeOffset.UtcNow, SourceDirectory: root) };
			var builder = new MockAgentBuilder().WithResponse("ok");
			var prompt = new PromptExecutor(builder, NullOrchestrationReporter.Instance, DefaultPromptFormatter.Instance, NullLogger<PromptExecutor>.Instance);
			var pr = await prompt.ExecuteAsync(new PromptOrchestrationStep { Name = "prompt", Type = OrchestrationStepType.Prompt, Model = "model", SystemPrompt = "", UserPrompt = "go", WorkingDirectory = configured }, context);
			pr.Status.Should().Be(ExecutionStatus.Succeeded);
			pr.Trace!.WorkingDirectory.Should().Be(expected);
			builder.CapturedConfig!.WorkingDirectory.Should().Be(expected);
			var command = new CommandStepExecutor(NullOrchestrationReporter.Instance, NullLogger<CommandStepExecutor>.Instance);
			var cr = await command.ExecuteAsync(new CommandOrchestrationStep { Name = "command", Type = OrchestrationStepType.Command, Command = "pwsh", Arguments = ["-NoProfile", "-Command", "(Get-Location).Path"], WorkingDirectory = configured }, context);
			cr.Status.Should().Be(ExecutionStatus.Succeeded, cr.ErrorMessage);
			(await File.ReadAllTextAsync(Path.Combine(cr.Content.Trim(), marker))).Should().Be(marker, "the child must use the same directory, including when macOS resolves /var to /private/var");
			var script = new ScriptStepExecutor(NullOrchestrationReporter.Instance, NullLogger<ScriptStepExecutor>.Instance);
			var sr = await script.ExecuteAsync(new ScriptOrchestrationStep { Name = "script", Type = OrchestrationStepType.Script, Shell = "pwsh", Script = "(Get-Location).Path", WorkingDirectory = configured }, context);
			sr.Status.Should().Be(ExecutionStatus.Succeeded, sr.ErrorMessage);
			(await File.ReadAllTextAsync(Path.Combine(sr.Content.Trim(), marker))).Should().Be(marker);
		}
		finally { Directory.Delete(root, true); }
	}

	[Fact]
	public void InMemoryDefinitionsWithoutSource_PreserveProcessDefault()
	{
		ExecutionWorkingDirectory.Resolve(null, null).Should().BeNull();
		ExecutionWorkingDirectory.Resolve(".", null).Should().Be(Environment.CurrentDirectory);
	}
}
