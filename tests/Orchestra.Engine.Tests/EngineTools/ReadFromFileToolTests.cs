using FluentAssertions;

namespace Orchestra.Engine.Tests.EngineTools;

public class ReadFromFileToolTests : IDisposable
{
	private readonly string _tempRoot;

	public ReadFromFileToolTests()
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
		var tool = new ReadFromFileTool();

		tool.Name.Should().Be("orchestra_read_file");
	}

	[Fact]
	public void Description_IsNotEmpty()
	{
		var tool = new ReadFromFileTool();

		tool.Description.Should().NotBeNullOrWhiteSpace();
	}

	[Fact]
	public void ParametersSchema_IsValidJson()
	{
		var tool = new ReadFromFileTool();

		var act = () => System.Text.Json.JsonDocument.Parse(tool.ParametersSchema);

		act.Should().NotThrow();
	}

	[Fact]
	public void Execute_ExistingFile_ReturnsContent()
	{
		var tool = new ReadFromFileTool();
		var store = CreateStore();
		var context = new EngineToolContext { TempFileStore = store };
		var expectedContent = "file content here";
		var filePath = store.SaveFile(expectedContent);

		var result = tool.Execute($$$"""{"filePath": "{{{filePath.Replace("\\", "\\\\")}}}"}""", context);

		result.Should().Be(expectedContent);
	}

	[Fact]
	public void Execute_NonexistentFile_ReturnsError()
	{
		var tool = new ReadFromFileTool();
		var store = CreateStore();
		var context = new EngineToolContext { TempFileStore = store };

		var result = tool.Execute("""{"filePath": "nonexistent.txt"}""", context);

		result.Should().Contain("Error");
		result.Should().Contain("not found");
	}

	[Fact]
	public void Execute_PathTraversal_ReturnsError()
	{
		var tool = new ReadFromFileTool();
		var store = CreateStore();
		var context = new EngineToolContext { TempFileStore = store };

		var result = tool.Execute("""{"filePath": "../../../etc/passwd"}""", context);

		result.Should().Contain("Error");
		result.Should().Contain("path traversal");
	}

	[Fact]
	public void Execute_MissingFilePath_ReturnsError()
	{
		var tool = new ReadFromFileTool();
		var store = CreateStore();
		var context = new EngineToolContext { TempFileStore = store };

		var result = tool.Execute("""{}""", context);

		result.Should().Contain("Error");
		result.Should().Contain("filePath");
	}

	[Fact]
	public void Execute_EmptyFilePath_ReturnsError()
	{
		var tool = new ReadFromFileTool();
		var store = CreateStore();
		var context = new EngineToolContext { TempFileStore = store };

		var result = tool.Execute("""{"filePath": ""}""", context);

		result.Should().Contain("Error");
		result.Should().Contain("filePath");
	}

	[Fact]
	public void Execute_InvalidJson_ReturnsError()
	{
		var tool = new ReadFromFileTool();
		var store = CreateStore();
		var context = new EngineToolContext { TempFileStore = store };

		var result = tool.Execute("not json", context);

		result.Should().Contain("Invalid arguments");
	}

	[Fact]
	public void Execute_NullTempFileStore_ReturnsError()
	{
		var tool = new ReadFromFileTool();
		var context = new EngineToolContext { TempFileStore = null };

		var result = tool.Execute("""{"filePath": "test.txt"}""", context);

		result.Should().Contain("Error");
		result.Should().Contain("not available");
	}

	[Fact]
	public void Execute_SaveThenRead_RoundTrips()
	{
		var readTool = new ReadFromFileTool();
		var store = CreateStore();
		var context = new EngineToolContext { TempFileStore = store };
		var content = "round-trip content with special chars";

		// Save directly via the store to get the full file path
		var filePath = store.SaveFile(content);

		var readResult = readTool.Execute($$$"""{"filePath": "{{{filePath.Replace("\\", "\\\\")}}}"}""", context);

		readResult.Should().Be(content);
	}

	[Fact]
	public void Execute_AbsolutePathOutsideTempDir_ReturnsError()
	{
		var tool = new ReadFromFileTool();
		var store = CreateStore();
		var context = new EngineToolContext { TempFileStore = store };

		// Use a platform-appropriate absolute path outside the temp dir
		var outsidePath = OperatingSystem.IsWindows()
			? "C:\\\\Windows\\\\System32\\\\config.sys"
			: "/etc/hostname";

		var result = tool.Execute($"{{\"filePath\": \"{outsidePath}\"}}", context);

		result.Should().Contain("Error");
	}
}
