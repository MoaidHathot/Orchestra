using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestra.Engine;
using Xunit;

namespace Orchestra.Cli.Tests.Init;

public class GeneratorExecutionTests
{
	[Fact]
	public async Task ApprovedReview_ValidatesAndWritesTheFinalDraft()
	{
		var root = Directory.CreateTempSubdirectory("orchestra-generator-").FullName;
		try
		{
			var definition = OrchestrationParser.ParseOrchestrationFile(Path.Combine(AppContext.BaseDirectory, "templates", "generate.yaml"), []);
			var check = (ScriptOrchestrationStep)definition.Steps.Single(s => s.Name == "check");
			var cli = Path.Combine(AppContext.BaseDirectory, "Orchestra.Cli.dll").Replace("'", "''");
			var context = new OrchestrationExecutionContext
			{
				OrchestrationInfo = new("generate", "1", "test", DateTimeOffset.UtcNow),
				Parameters = new() { ["outputPath"] = "" }, TempFileStore = new(root, "generate", "test"),
			};
			context.AddResult("draft", ExecutionResult.Succeeded("name: generated\ndescription: Valid draft\nsteps:\n  - name: result\n    type: Transform\n    template: success\n"));
			context.AddResult("review", ExecutionResult.Succeeded("APPROVED"));
			var step = new ScriptOrchestrationStep
			{
				Name = check.Name, Type = check.Type, Shell = "pwsh", DependsOn = check.DependsOn,
				Script = $"function orchestra {{ & dotnet '{cli}' @args }}\n" + check.Script,
			};
			var result = await new ScriptStepExecutor(NullOrchestrationReporter.Instance, NullLogger<ScriptStepExecutor>.Instance).ExecuteAsync(step, context);
			result.Status.Should().Be(ExecutionStatus.Succeeded, result.ErrorMessage);
			var generated = OrchestrationParser.ParseOrchestrationFile(result.Content.Trim(), []);
			generated.Name.Should().Be("generated");
		}
		finally { Directory.Delete(root, true); }
	}
}
