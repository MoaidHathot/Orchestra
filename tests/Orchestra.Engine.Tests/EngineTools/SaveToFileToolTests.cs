using FluentAssertions;

namespace Orchestra.Engine.Tests.EngineTools;

public class SaveToFileToolTests : IDisposable
{
	private readonly string _tempRoot;

	public SaveToFileToolTests()
	{
		_tempRoot = Path.Combine(Path.GetTempPath(), $"orchestra-test-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempRoot);
	}

	public void Dispose()
	{
		if (Directory.Exists(_tempRoot))
		{
			Directory.Delete(_tempRoot, recursive: true);
		}
	}

	private OrchestrationTempFileStore CreateStore() =>
		new(_tempRoot, "test-orch", "run-1");

	[Fact]
	public void Name_ReturnsExpectedName()
	{
		var tool = new SaveToFileTool();

		tool.Name.Should().Be("orchestra_save_file");
	}

	[Fact]
	public void Description_IsNotEmpty()
	{
		var tool = new SaveToFileTool();

		tool.Description.Should().NotBeNullOrWhiteSpace();
	}

	[Fact]
	public void ParametersSchema_IsValidJson()
	{
		var tool = new SaveToFileTool();

		var act = () => System.Text.Json.JsonDocument.Parse(tool.ParametersSchema);

		act.Should().NotThrow();
	}

	[Fact]
	public void Execute_WithContent_SavesFileAndReturnsFilePath()
	{
		var tool = new SaveToFileTool();
		var store = CreateStore();
		var context = new EngineToolContext { TempFileStore = store };

		var result = tool.Execute("""{"content": "hello world"}""", context);

		result.Should().Contain("File saved successfully");
		result.Should().Contain("File path:");
		result.Should().Contain(".txt");
		// The response should contain the full path within the temp directory
		result.Should().Contain(store.TempDirectory);
	}

	[Fact]
	public void Execute_WithContentAndExtension_SavesFileWithCorrectExtension()
	{
		var tool = new SaveToFileTool();
		var store = CreateStore();
		var context = new EngineToolContext { TempFileStore = store };

		var result = tool.Execute("""{"content": "{\"key\": \"value\"}", "extension": "json"}""", context);

		result.Should().Contain("File saved successfully");
		result.Should().Contain(".json");
	}

	[Fact]
	public void Execute_SavedContentIsReadable()
	{
		var tool = new SaveToFileTool();
		var store = CreateStore();
		var context = new EngineToolContext { TempFileStore = store };

		tool.Execute("""{"content": "simple plain text content"}""", context);

		// Verify a file was created in the directory
		var files = Directory.GetFiles(store.TempDirectory);
		files.Should().HaveCount(1);
		File.ReadAllText(files[0]).Should().Be("simple plain text content");
	}

	[Fact]
	public void Execute_MissingContent_ReturnsError()
	{
		var tool = new SaveToFileTool();
		var store = CreateStore();
		var context = new EngineToolContext { TempFileStore = store };

		var result = tool.Execute("""{"extension": "json"}""", context);

		result.Should().Contain("Error");
		result.Should().Contain("content");
	}

	[Fact]
	public void Execute_InvalidJson_ReturnsError()
	{
		var tool = new SaveToFileTool();
		var store = CreateStore();
		var context = new EngineToolContext { TempFileStore = store };

		var result = tool.Execute("not json", context);

		result.Should().Contain("Invalid arguments");
	}

	[Fact]
	public void Execute_NullTempFileStore_ReturnsError()
	{
		var tool = new SaveToFileTool();
		var context = new EngineToolContext { TempFileStore = null };

		var result = tool.Execute("""{"content": "hello"}""", context);

		result.Should().Contain("Error");
		result.Should().Contain("not available");
	}

	[Fact]
	public void Execute_ContentOnlyNoExtension_DefaultsToTxt()
	{
		var tool = new SaveToFileTool();
		var store = CreateStore();
		var context = new EngineToolContext { TempFileStore = store };

		var result = tool.Execute("""{"content": "plain text"}""", context);

		result.Should().Contain(".txt");
	}
}
