using FluentAssertions;

namespace Orchestra.Engine.Tests.EngineTools;

public class OrchestrationTempFileStoreTests : IDisposable
{
	private readonly string _tempRoot;

	public OrchestrationTempFileStoreTests()
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

	[Fact]
	public void Constructor_CreatesDirectory()
	{
		var store = new OrchestrationTempFileStore(_tempRoot, "my-orch", "run-1");

		Directory.Exists(store.TempDirectory).Should().BeTrue();
		store.TempDirectory.Should().Contain("temp");
		store.TempDirectory.Should().Contain("my-orch");
		store.TempDirectory.Should().Contain("run-1");
	}

	[Fact]
	public void TempDirectory_ReturnsCorrectPath()
	{
		var store = new OrchestrationTempFileStore(_tempRoot, "pipeline", "run-abc");

		var expected = Path.Combine(_tempRoot, "temp", "pipeline", "run-abc");
		store.TempDirectory.Should().Be(expected);
	}

	[Fact]
	public void SaveFile_DefaultExtension_CreatesTxtFile()
	{
		var store = new OrchestrationTempFileStore(_tempRoot, "orch", "run-1");

		var filePath = store.SaveFile("hello world");

		filePath.Should().EndWith(".txt");
		filePath.Should().StartWith(store.TempDirectory);
		File.Exists(filePath).Should().BeTrue();
	}

	[Fact]
	public void SaveFile_CustomExtension_UsesSpecifiedExtension()
	{
		var store = new OrchestrationTempFileStore(_tempRoot, "orch", "run-1");

		var filePath = store.SaveFile("{\"key\": \"value\"}", "json");

		filePath.Should().EndWith(".json");
		filePath.Should().StartWith(store.TempDirectory);
	}

	[Fact]
	public void SaveFile_ExtensionWithDot_TrimsDot()
	{
		var store = new OrchestrationTempFileStore(_tempRoot, "orch", "run-1");

		var filePath = store.SaveFile("data", ".xml");

		filePath.Should().EndWith(".xml");
		// Should not have double dot in the file name portion
		Path.GetFileName(filePath).Should().NotContain("..");
	}

	[Fact]
	public void SaveFile_EmptyExtension_DefaultsToTxt()
	{
		var store = new OrchestrationTempFileStore(_tempRoot, "orch", "run-1");

		var filePath = store.SaveFile("data", "");

		filePath.Should().EndWith(".txt");
	}

	[Fact]
	public void SaveFile_WhitespaceExtension_DefaultsToTxt()
	{
		var store = new OrchestrationTempFileStore(_tempRoot, "orch", "run-1");

		var filePath = store.SaveFile("data", "   ");

		filePath.Should().EndWith(".txt");
	}

	[Fact]
	public void SaveFile_ContentIsWrittenCorrectly()
	{
		var store = new OrchestrationTempFileStore(_tempRoot, "orch", "run-1");
		var content = "multi\nline\ncontent\nwith special chars: <>&\"'";

		var filePath = store.SaveFile(content);

		var readBack = File.ReadAllText(filePath);
		readBack.Should().Be(content);
	}

	[Fact]
	public void SaveFile_MultipleFiles_GenerateUniqueNames()
	{
		var store = new OrchestrationTempFileStore(_tempRoot, "orch", "run-1");

		var file1 = store.SaveFile("content1");
		var file2 = store.SaveFile("content2");

		file1.Should().NotBe(file2);
	}

	[Fact]
	public void ReadFile_ExistingFile_ReturnsContent()
	{
		var store = new OrchestrationTempFileStore(_tempRoot, "orch", "run-1");
		var originalContent = "this is the file content";
		var filePath = store.SaveFile(originalContent);

		var readContent = store.ReadFile(filePath);

		readContent.Should().Be(originalContent);
	}

	[Fact]
	public void ReadFile_NonexistentFile_ThrowsFileNotFoundException()
	{
		var store = new OrchestrationTempFileStore(_tempRoot, "orch", "run-1");

		var act = () => store.ReadFile("does-not-exist.txt");

		act.Should().Throw<FileNotFoundException>()
			.WithMessage("*does-not-exist.txt*");
	}

	[Fact]
	public void ReadFile_PathTraversalWithDoubleDot_ThrowsInvalidOperationException()
	{
		var store = new OrchestrationTempFileStore(_tempRoot, "orch", "run-1");

		var act = () => store.ReadFile("../../../etc/passwd");

		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*path traversal*");
	}

	[Fact]
	public void ReadFile_AbsolutePathOutsideTempDir_ThrowsInvalidOperationException()
	{
		var store = new OrchestrationTempFileStore(_tempRoot, "orch", "run-1");

		var act = () => store.ReadFile("/etc/passwd");

		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*within the orchestration temp directory*");
	}

	[Fact]
	public void ReadFile_WindowsAbsolutePathOutsideTempDir_ThrowsInvalidOperationException()
	{
		var store = new OrchestrationTempFileStore(_tempRoot, "orch", "run-1");

		var act = () => store.ReadFile("C:\\Windows\\System32\\config.sys");

		act.Should().Throw<InvalidOperationException>()
			.WithMessage("*within the orchestration temp directory*");
	}

	[Fact]
	public void Constructor_SanitizesOrchestrationName()
	{
		// Characters like : or / are invalid in file names
		var store = new OrchestrationTempFileStore(_tempRoot, "my:orch/test", "run-1");

		Directory.Exists(store.TempDirectory).Should().BeTrue();
		// The sanitized orchestration name directory should exist and not contain invalid chars
		var orchDir = Path.GetFileName(Path.GetDirectoryName(store.TempDirectory)!);
		orchDir.Should().NotContain(":");
		orchDir.Should().NotContain("/");
	}

	[Fact]
	public void SaveFile_LargeContent_WorksCorrectly()
	{
		var store = new OrchestrationTempFileStore(_tempRoot, "orch", "run-1");
		var largeContent = new string('x', 1_000_000); // 1MB of x's

		var filePath = store.SaveFile(largeContent, "dat");
		var readBack = store.ReadFile(filePath);

		readBack.Should().Be(largeContent);
	}

	[Fact]
	public void SaveFile_EmptyContent_SavesEmptyFile()
	{
		var store = new OrchestrationTempFileStore(_tempRoot, "orch", "run-1");

		var filePath = store.SaveFile("");

		var readBack = store.ReadFile(filePath);
		readBack.Should().BeEmpty();
	}

	[Fact]
	public void ReadFile_FullPathWithinTempDir_ReturnsContent()
	{
		var store = new OrchestrationTempFileStore(_tempRoot, "orch", "run-1");
		var content = "full path test content";
		var filePath = store.SaveFile(content);

		// ReadFile should accept the full path returned by SaveFile
		var readContent = store.ReadFile(filePath);

		readContent.Should().Be(content);
	}

	[Fact]
	public void ReadFile_BareFileName_ReturnsContent()
	{
		var store = new OrchestrationTempFileStore(_tempRoot, "orch", "run-1");
		var content = "bare name test content";
		var filePath = store.SaveFile(content);

		// Extract just the file name from the full path
		var bareFileName = Path.GetFileName(filePath);
		var readContent = store.ReadFile(bareFileName);

		readContent.Should().Be(content);
	}

	[Fact]
	public void SaveFile_ReturnsFullPath()
	{
		var store = new OrchestrationTempFileStore(_tempRoot, "orch", "run-1");

		var filePath = store.SaveFile("test content");

		Path.IsPathRooted(filePath).Should().BeTrue();
		filePath.Should().StartWith(store.TempDirectory);
	}

	#region Per-Step File Tracking (Fix #6)

	[Fact]
	public void SaveFile_WithStepName_RegistersFileForStep()
	{
		var store = new OrchestrationTempFileStore(_tempRoot, "orch", "run-1");

		var filePath = store.SaveFile("content", "research", "txt");

		store.GetFilesForStep("research").Should().ContainSingle()
			.Which.Should().Be(filePath);
	}

	[Fact]
	public void SaveFile_WithStepName_MultipleFiles_AllRegistered()
	{
		var store = new OrchestrationTempFileStore(_tempRoot, "orch", "run-1");

		var file1 = store.SaveFile("content1", "research", "txt");
		var file2 = store.SaveFile("content2", "research", "json");

		var files = store.GetFilesForStep("research");
		files.Should().HaveCount(2);
		files.Should().Contain(file1);
		files.Should().Contain(file2);
	}

	[Fact]
	public void SaveFile_DifferentSteps_IndependentFileLists()
	{
		var store = new OrchestrationTempFileStore(_tempRoot, "orch", "run-1");

		var file1 = store.SaveFile("research data", "research", "txt");
		var file2 = store.SaveFile("analysis data", "analysis", "txt");

		store.GetFilesForStep("research").Should().ContainSingle()
			.Which.Should().Be(file1);
		store.GetFilesForStep("analysis").Should().ContainSingle()
			.Which.Should().Be(file2);
	}

	[Fact]
	public void GetFilesForStep_NoFilesRegistered_ReturnsEmptyArray()
	{
		var store = new OrchestrationTempFileStore(_tempRoot, "orch", "run-1");

		var files = store.GetFilesForStep("nonexistent-step");

		files.Should().BeEmpty();
	}

	[Fact]
	public void RegisterFileForStep_ManualRegistration_TracksCorrectly()
	{
		var store = new OrchestrationTempFileStore(_tempRoot, "orch", "run-1");

		store.RegisterFileForStep("step1", "/some/path/file.txt");

		store.GetFilesForStep("step1").Should().ContainSingle()
			.Which.Should().Be("/some/path/file.txt");
	}

	[Fact]
	public void GetFilesForStep_CaseInsensitive()
	{
		var store = new OrchestrationTempFileStore(_tempRoot, "orch", "run-1");

		store.SaveFile("content", "Research", "txt");

		// Step name lookup should be case-insensitive (ConcurrentDictionary with OrdinalIgnoreCase)
		store.GetFilesForStep("research").Should().HaveCount(1);
		store.GetFilesForStep("RESEARCH").Should().HaveCount(1);
	}

	[Fact]
	public void SaveFile_WithStepName_ContentIsWrittenCorrectly()
	{
		var store = new OrchestrationTempFileStore(_tempRoot, "orch", "run-1");
		var content = "step-specific content";

		var filePath = store.SaveFile(content, "myStep", "txt");

		File.ReadAllText(filePath).Should().Be(content);
	}

	[Fact]
	public void SaveFile_WithStepName_ConcurrentAccess_IsThreadSafe()
	{
		var store = new OrchestrationTempFileStore(_tempRoot, "orch", "run-1");
		var tasks = new List<Task>();

		for (int i = 0; i < 50; i++)
		{
			var index = i;
			tasks.Add(Task.Run(() =>
			{
				store.SaveFile($"content-{index}", "concurrent-step", "txt");
			}));
		}

		Task.WaitAll(tasks.ToArray());

		store.GetFilesForStep("concurrent-step").Should().HaveCount(50);
	}

	#endregion
}
