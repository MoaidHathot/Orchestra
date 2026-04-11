using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Orchestra.Engine;

namespace Orchestra.Copilot.Tests;

public class EngineToolAIFunctionTests
{
	#region Schema Parsing

	[Fact]
	public void Constructor_ParsesJsonSchemaCorrectly()
	{
		// Arrange
		var tool = new FakeEngineTool(
			name: "test_tool",
			description: "A test tool",
			schema: """{"type":"object","properties":{"input":{"type":"string"}},"required":["input"]}""");
		var context = new EngineToolContext();

		// Act
		var function = new EngineToolAIFunction(tool, context);

		// Assert
		function.JsonSchema.GetProperty("type").GetString().Should().Be("object");
		function.JsonSchema.GetProperty("properties").GetProperty("input").GetProperty("type").GetString().Should().Be("string");
	}

	[Fact]
	public void Constructor_ClonesJsonElement_SoDocumentCanBeDisposed()
	{
		// Arrange — The constructor should clone the JsonElement so it doesn't
		// hold a reference to the parsed JsonDocument's pooled memory.
		var tool = new FakeEngineTool(
			name: "test_tool",
			description: "A test tool",
			schema: """{"type":"object","properties":{"value":{"type":"integer"}}}""");
		var context = new EngineToolContext();

		// Act
		var function = new EngineToolAIFunction(tool, context);

		// Assert — Access the schema after construction to ensure it's still valid
		// (would fail if the JsonDocument was disposed and the element wasn't cloned)
		function.JsonSchema.ValueKind.Should().Be(JsonValueKind.Object);
		function.JsonSchema.GetProperty("type").GetString().Should().Be("object");
	}

	[Fact]
	public void Constructor_WithInvalidJson_Throws()
	{
		// Arrange
		var tool = new FakeEngineTool(
			name: "test_tool",
			description: "A test tool",
			schema: "not valid json {{{");
		var context = new EngineToolContext();

		// Act
		var act = () => new EngineToolAIFunction(tool, context);

		// Assert
		act.Should().Throw<JsonException>();
	}

	#endregion

	#region Properties

	[Fact]
	public void Name_ReturnsTool_Name()
	{
		// Arrange
		var tool = new FakeEngineTool(name: "orchestra_set_status", description: "desc", schema: "{}");
		var context = new EngineToolContext();

		// Act
		var function = new EngineToolAIFunction(tool, context);

		// Assert
		function.Name.Should().Be("orchestra_set_status");
	}

	[Fact]
	public void Description_ReturnsTool_Description()
	{
		// Arrange
		var tool = new FakeEngineTool(name: "test", description: "Sets the execution status", schema: "{}");
		var context = new EngineToolContext();

		// Act
		var function = new EngineToolAIFunction(tool, context);

		// Assert
		function.Description.Should().Be("Sets the execution status");
	}

	#endregion

	#region InvokeCoreAsync

	[Fact]
	public async Task InvokeAsync_SerializesArgumentsAndCallsExecute()
	{
		// Arrange
		var tool = new FakeEngineTool(
			name: "test_tool",
			description: "A test tool",
			schema: """{"type":"object","properties":{"status":{"type":"string"}}}""",
			executeResult: "Tool executed successfully");
		var context = new EngineToolContext();
		var function = new EngineToolAIFunction(tool, context);

		var arguments = new AIFunctionArguments
		{
			["status"] = "success",
			["reason"] = "All items processed"
		};

		// Act
		var result = await function.InvokeAsync(arguments);

		// Assert
		result.Should().Be("Tool executed successfully");
		tool.LastArguments.Should().NotBeNull();

		// Verify the serialized JSON contains the arguments
		using var doc = JsonDocument.Parse(tool.LastArguments!);
		doc.RootElement.GetProperty("status").GetString().Should().Be("success");
		doc.RootElement.GetProperty("reason").GetString().Should().Be("All items processed");
	}

	[Fact]
	public async Task InvokeAsync_PassesContextToTool()
	{
		// Arrange
		var tool = new FakeEngineTool(
			name: "test_tool",
			description: "A test tool",
			schema: "{}",
			executeResult: "ok");
		var context = new EngineToolContext { StepName = "my-step" };
		var function = new EngineToolAIFunction(tool, context);

		// Act
		await function.InvokeAsync(new AIFunctionArguments());

		// Assert
		tool.LastContext.Should().BeSameAs(context);
		tool.LastContext!.StepName.Should().Be("my-step");
	}

	[Fact]
	public async Task InvokeAsync_WithEmptyArguments_SerializesEmptyObject()
	{
		// Arrange
		var tool = new FakeEngineTool(
			name: "test_tool",
			description: "A test tool",
			schema: "{}",
			executeResult: "ok");
		var context = new EngineToolContext();
		var function = new EngineToolAIFunction(tool, context);

		// Act
		await function.InvokeAsync(new AIFunctionArguments());

		// Assert
		tool.LastArguments.Should().Be("{}");
	}

	[Fact]
	public async Task InvokeAsync_WhenCancelled_ThrowsOperationCancelledException()
	{
		// Arrange
		var tool = new FakeEngineTool(
			name: "test_tool",
			description: "A test tool",
			schema: "{}",
			executeResult: "should not reach");
		var context = new EngineToolContext();
		var function = new EngineToolAIFunction(tool, context);

		using var cts = new CancellationTokenSource();
		cts.Cancel();

		// Act
		var act = () => function.InvokeAsync(new AIFunctionArguments(), cts.Token).AsTask();

		// Assert
		await act.Should().ThrowAsync<OperationCanceledException>();
		tool.ExecuteCallCount.Should().Be(0, "tool should not be invoked when already cancelled");
	}

	#endregion

	#region Integration with Real Engine Tools

	[Fact]
	public async Task InvokeAsync_WithSetStatusTool_SetsContextStatus()
	{
		// Arrange — Use the real SetStatusTool to verify end-to-end integration
		var tool = new SetStatusTool();
		var context = new EngineToolContext();
		var function = new EngineToolAIFunction(tool, context);

		var arguments = new AIFunctionArguments
		{
			["status"] = "failed",
			["reason"] = "Required MCP server unavailable"
		};

		// Act
		var result = await function.InvokeAsync(arguments);

		// Assert
		context.HasStatusOverride.Should().BeTrue();
		context.StatusOverride.Should().Be(ExecutionStatus.Failed);
		context.StatusReason.Should().Be("Required MCP server unavailable");
		result.Should().NotBeNull();
		result!.ToString().Should().Contain("failed");
	}

	[Fact]
	public void Schema_FromSetStatusTool_ParsesCorrectly()
	{
		// Arrange — Verify that real engine tool schemas parse without error
		var tool = new SetStatusTool();
		var context = new EngineToolContext();

		// Act
		var function = new EngineToolAIFunction(tool, context);

		// Assert
		function.JsonSchema.GetProperty("type").GetString().Should().Be("object");
		function.JsonSchema.GetProperty("properties").GetProperty("status").GetProperty("type").GetString().Should().Be("string");
		function.JsonSchema.GetProperty("required").GetArrayLength().Should().Be(2);
	}

	#endregion

	#region Test Helpers

	private sealed class FakeEngineTool : IEngineTool
	{
		private readonly string _executeResult;

		public FakeEngineTool(
			string name,
			string description,
			string schema,
			string executeResult = "ok")
		{
			Name = name;
			Description = description;
			ParametersSchema = schema;
			_executeResult = executeResult;
		}

		public string Name { get; }
		public string Description { get; }
		public string ParametersSchema { get; }

		public string? LastArguments { get; private set; }
		public EngineToolContext? LastContext { get; private set; }
		public int ExecuteCallCount { get; private set; }

		public string Execute(string arguments, EngineToolContext context)
		{
			ExecuteCallCount++;
			LastArguments = arguments;
			LastContext = context;
			return _executeResult;
		}
	}

	#endregion
}
