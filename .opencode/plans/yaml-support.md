# Plan: Add YAML Support for Orchestration Files

## Goal
Add YAML as an alternative format for authoring orchestration files, alongside existing JSON support. This solves the pain of writing multiline prompts in JSON (which doesn't support multiline strings). YAML's `|` (literal block) and `>` (folded block) scalars make long prompts natural to write and read.

## Approach: YAML-to-JSON Conversion Layer
Convert YAML to JSON at read time, then feed into the existing `System.Text.Json` pipeline. This means:
- Zero changes to `IStepTypeParser`, converters, or any downstream parsing logic
- Existing JSON path completely untouched (no regressions)
- Only touches file discovery and the initial file-read step

## Decisions Made
- **Strategy**: YAML-to-JSON conversion (not direct YamlDotNet deserialization)
- **MCP config**: Keep `orchestra.mcp.json` as JSON only
- **Internal storage**: Managed copies always normalized to JSON; user-facing exports preserve original format
- **Package**: YamlDotNet 17.0.1 (444M+ downloads, well-maintained)

---

## Changes

### 1. Add YamlDotNet NuGet package

**File: `Directory.Packages.props`**
Add after the LLM/MCP section:
```xml
<!-- Serialization -->
<PackageVersion Include="YamlDotNet" Version="17.0.1" />
```

**File: `src/Orchestra.Engine/Orchestra.Engine.csproj`**
Add to the ItemGroup with PackageReferences:
```xml
<PackageReference Include="YamlDotNet" />
```

### 2. Add YAML-to-JSON conversion in OrchestrationParser.cs

**File: `src/Orchestra.Engine/Serialization/OrchestrationParser.cs`**

Add using at top:
```csharp
using YamlDotNet.Serialization;
```

Add these helper methods to the class:

```csharp
/// <summary>
/// File extensions recognized as YAML orchestration files.
/// </summary>
private static readonly string[] s_yamlExtensions = [".yaml", ".yml"];

/// <summary>
/// Returns true if the file extension indicates a YAML file.
/// </summary>
internal static bool IsYamlFile(string path)
{
    var ext = Path.GetExtension(path);
    return s_yamlExtensions.Any(e => ext.Equals(e, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reads an orchestration file and returns its content as a JSON string.
/// YAML files (.yaml, .yml) are converted to JSON; JSON files are returned as-is.
/// </summary>
private static string ReadAsJson(string path)
{
    var content = File.ReadAllText(path);
    return IsYamlFile(path) ? ConvertYamlToJson(content) : content;
}

/// <summary>
/// Converts a YAML string to a JSON string.
/// Uses YamlDotNet's JSON-compatible serializer to ensure output is valid JSON
/// that can be parsed by System.Text.Json.
/// </summary>
internal static string ConvertYamlToJson(string yaml)
{
    var deserializer = new DeserializerBuilder().Build();
    var yamlObject = deserializer.Deserialize(new StringReader(yaml));

    if (yamlObject is null)
        throw new InvalidOperationException("YAML content is empty or null.");

    var serializer = new SerializerBuilder()
        .JsonCompatible()
        .Build();

    return serializer.Serialize(yamlObject);
}

/// <summary>
/// The combined search pattern for orchestration files (JSON + YAML).
/// Since Directory.GetFiles only supports a single pattern, use this with
/// <see cref="GetOrchestrationFiles"/> instead.
/// </summary>
internal static readonly string[] OrchestrationFileExtensions = [".json", ".yaml", ".yml"];

/// <summary>
/// Gets all orchestration files (JSON and YAML) from a directory.
/// </summary>
internal static string[] GetOrchestrationFiles(string directory, SearchOption searchOption = SearchOption.TopDirectoryOnly)
{
    return Directory.GetFiles(directory, "*.*", searchOption)
        .Where(f => OrchestrationFileExtensions.Any(ext =>
            f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
        .ToArray();
}
```

Then update the four `ParseOrchestrationFile*` methods to use `ReadAsJson(path)` instead of `File.ReadAllText(path)`:

**`ParseOrchestrationFile(string path, Mcp[] availableMcps)` (line 79-93):**
Change `var json = File.ReadAllText(path);` to `var json = ReadAsJson(path);`

**`ParseOrchestrationFile(string path, Mcp[] availableMcps, StepTypeParserRegistry parserRegistry)` (line 98-112):**
Change `var json = File.ReadAllText(path);` to `var json = ReadAsJson(path);`

**`ParseOrchestrationFileMetadataOnly(string path)` (line 130-138):**
Change `var json = File.ReadAllText(path);` to `var json = ReadAsJson(path);`

**Do NOT change** `ParseMcpFile` -- MCP config stays JSON only.

### 3. Update file discovery sites

All these locations scan for `*.json` and need to include `*.yaml`/`*.yml`. Use the new `OrchestrationParser.GetOrchestrationFiles()` helper.

#### 3a. OrchestrationRegistry.ScanDirectory (line 299)
**File: `src/Orchestra.Host/Registry/OrchestrationRegistry.cs`**

Change:
```csharp
foreach (var file in Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
```
To:
```csharp
foreach (var file in OrchestrationParser.GetOrchestrationFiles(directory))
```

#### 3b. TriggerManager.ScanForJsonTriggers (line 580)
**File: `src/Orchestra.Host/Triggers/TriggerManager.cs`**

Change:
```csharp
foreach (var file in Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
```
To:
```csharp
foreach (var file in OrchestrationParser.GetOrchestrationFiles(directory))
```

Also update the XML doc comment on line 569-573 from "JSON-defined triggers" to "file-defined triggers" (it's a doc comment, minor text fix).

#### 3c. OrchestrationsApi POST /scan (line 427)
**File: `src/Orchestra.Host/Api/OrchestrationsApi.cs`**

Change:
```csharp
var files = Directory.GetFiles(request.Directory, "*.json", SearchOption.TopDirectoryOnly);
```
To:
```csharp
var files = OrchestrationParser.GetOrchestrationFiles(request.Directory);
```

The `OrchestraConfigLoader.McpConfigFileName` skip check on line 433 will continue to work since it checks file name, not extension.

#### 3d. ControlPlaneTools.ScanDirectory (line 145)
**File: `src/Orchestra.Host/McpServer/ControlPlaneTools.cs`**

Change:
```csharp
var files = Directory.GetFiles(directory, "*.json");
```
To:
```csharp
var files = OrchestrationParser.GetOrchestrationFiles(directory);
```

#### 3e. Portal Program.cs - File browser (line 130)
**File: `playground/Hosting/Orchestra.Playground.Copilot.Portal/Program.cs`**

Change:
```csharp
foreach (var file in Directory.GetFiles(directory, "*.json").OrderBy(f => f))
```
To:
```csharp
foreach (var file in OrchestrationParser.GetOrchestrationFiles(directory).OrderBy(f => f))
```

#### 3f. Portal Program.cs - Folder scan (line 205)
**File: `playground/Hosting/Orchestra.Playground.Copilot.Portal/Program.cs`**

Change:
```csharp
var files = Directory.GetFiles(request.Directory, "*.json", SearchOption.TopDirectoryOnly);
```
To:
```csharp
var files = OrchestrationParser.GetOrchestrationFiles(request.Directory);
```

#### 3g. Terminal TerminalUI.cs - UI text (line 1155)
**File: `playground/Hosting/Orchestra.Playground.Copilot.Terminal/TerminalUI.cs`**

Change:
```csharp
AnsiConsole.MarkupLine("[dim]Enter the path to an orchestration JSON file (or press Esc to cancel)[/]\n");
```
To:
```csharp
AnsiConsole.MarkupLine("[dim]Enter the path to an orchestration file (.json, .yaml, .yml) (or press Esc to cancel)[/]\n");
```

#### 3h. Terminal TerminalUI.cs - Scan UI text (line 1203)
**File: `playground/Hosting/Orchestra.Playground.Copilot.Terminal/TerminalUI.cs`**

Change:
```csharp
AnsiConsole.MarkupLine("[dim]Enter a directory path to scan for .json orchestration files (or press Esc to cancel)[/]\n");
```
To:
```csharp
AnsiConsole.MarkupLine("[dim]Enter a directory path to scan for orchestration files (.json, .yaml, .yml) (or press Esc to cancel)[/]\n");
```

### 4. Update tool descriptions

**File: `src/Orchestra.Host/McpServer/ControlPlaneTools.cs`**

Line 77-78, change description:
```csharp
"The file must be a valid orchestration JSON or YAML file.")]
```

Line 82, change parameter description:
```csharp
[Description("Absolute path to the orchestration file (JSON or YAML).")] string path)
```

Line 137, change description:
```csharp
"Scans a directory for orchestration files (JSON and YAML) and returns metadata. " +
```

### 5. Handle managed copies (always JSON internally)

**File: `src/Orchestra.Host/Registry/OrchestrationRegistry.cs`**

In the `Register` method (around line 71-74), the raw content read for hashing/snapshotting currently does:
```csharp
string? rawJson = null;
if (File.Exists(path))
    rawJson = File.ReadAllText(path);
```

Change to read and convert:
```csharp
string? rawJson = null;
if (File.Exists(path))
{
    var rawContent = File.ReadAllText(path);
    rawJson = OrchestrationParser.IsYamlFile(path) ? OrchestrationParser.ConvertYamlToJson(rawContent) : rawContent;
}
```

This ensures the managed copy is always stored as JSON, and the content hash is computed from the JSON representation so that equivalent JSON and YAML files produce the same hash.

The `CopyToManagedLocation` method (line 341-348) already uses `jsonContent` as the parameter name and writes `.json` extension, so it will continue to work correctly since we're now always passing JSON.

### 6. Create YAML example files

**File: `examples/code-review-example.yaml`** (new file)

Create a YAML version of an existing orchestration to demonstrate the format:

```yaml
name: code-review
description: Multi-step code review pipeline with YAML multiline prompts
version: "1.0.0"

steps:
  - name: analyze-code
    type: Prompt
    model: claude-opus-4.6
    dependsOn: []
    systemPrompt: |
      You are a senior software engineer performing a thorough code review.
      Focus on:
      - Code correctness and potential bugs
      - Performance implications
      - Security vulnerabilities
      - Adherence to best practices and coding standards
      - Code readability and maintainability

      Provide specific, actionable feedback with code examples where helpful.
    userPrompt: |
      Review the following code and provide detailed feedback:

      {{param.code}}

  - name: summarize-review
    type: Prompt
    model: claude-opus-4.6
    dependsOn:
      - analyze-code
    systemPrompt: |
      You are a technical writer. Summarize code review feedback into
      a concise, well-organized report with severity ratings.
    userPrompt: |
      Summarize the following code review into a brief report:

      {{analyze-code.output}}

  - name: format-output
    type: Transform
    dependsOn:
      - summarize-review
    template: |
      # Code Review Report

      {{summarize-review.output}}
```

### 7. Add tests

**File: `tests/Orchestra.Engine.Tests/Serialization/OrchestrationParserTests.cs`**

Add a new test region for YAML parsing. Key tests:

```csharp
#region YAML Parsing

[Fact]
public void ParseOrchestration_YamlString_ReturnsOrchestration()
{
    var yaml = """
        name: test-orchestration
        description: Test description
        steps:
          - name: step1
            type: prompt
            dependsOn: []
            systemPrompt: You are a test assistant.
            userPrompt: Test prompt
            model: claude-opus-4.6
        """;

    var json = OrchestrationParser.ConvertYamlToJson(yaml);
    var orchestration = OrchestrationParser.ParseOrchestration(json, []);

    orchestration.Name.Should().Be("test-orchestration");
    orchestration.Description.Should().Be("Test description");
    orchestration.Steps.Should().HaveCount(1);
    orchestration.Steps[0].Name.Should().Be("step1");
}

[Fact]
public void ParseOrchestration_YamlMultilinePrompts_PreservesContent()
{
    var yaml = """
        name: multiline-test
        description: Test multiline prompts
        steps:
          - name: step1
            type: prompt
            dependsOn: []
            systemPrompt: |
              You are a helpful assistant.
              You should be thorough and precise.
              Always provide examples.
            userPrompt: |
              Analyze the following:
              {{param.input}}
            model: claude-opus-4.6
        """;

    var json = OrchestrationParser.ConvertYamlToJson(yaml);
    var orchestration = OrchestrationParser.ParseOrchestration(json, []);

    var step = orchestration.Steps[0] as PromptOrchestrationStep;
    step.Should().NotBeNull();
    step!.SystemPrompt.Should().Contain("You are a helpful assistant.");
    step.SystemPrompt.Should().Contain("Always provide examples.");
    step.UserPrompt.Should().Contain("{{param.input}}");
}

[Fact]
public void ParseOrchestration_YamlWithVariables_ExtractsVariables()
{
    var yaml = """
        name: vars-test
        description: Test variables
        variables:
          greeting: hello
          target: world
        steps:
          - name: step1
            type: prompt
            dependsOn: []
            systemPrompt: "{{vars.greeting}} {{vars.target}}"
            userPrompt: test
            model: claude-opus-4.6
        """;

    var json = OrchestrationParser.ConvertYamlToJson(yaml);
    var orchestration = OrchestrationParser.ParseOrchestration(json, []);

    orchestration.Name.Should().Be("vars-test");
    orchestration.Variables.Should().ContainKey("greeting");
    orchestration.Variables["greeting"].Should().Be("hello");
}

[Fact]
public void ParseOrchestration_YamlWithTrigger_ParsesTriggerConfig()
{
    var yaml = """
        name: trigger-test
        description: Test trigger
        trigger:
          type: scheduler
          cron: "0 */5 * * *"
          enabled: true
        steps:
          - name: step1
            type: prompt
            dependsOn: []
            systemPrompt: test
            userPrompt: test
            model: claude-opus-4.6
        """;

    var json = OrchestrationParser.ConvertYamlToJson(yaml);
    var orchestration = OrchestrationParser.ParseOrchestration(json, []);

    orchestration.Trigger.Should().BeOfType<SchedulerTriggerConfig>();
    var trigger = (SchedulerTriggerConfig)orchestration.Trigger;
    trigger.Cron.Should().Be("0 */5 * * *");
}

[Fact]
public void ParseOrchestration_YamlWithComplexSteps_ParsesAllStepTypes()
{
    var yaml = """
        name: complex-test
        description: Test all step types
        steps:
          - name: prompt-step
            type: Prompt
            dependsOn: []
            systemPrompt: test system
            userPrompt: test user
            model: claude-opus-4.6
          - name: http-step
            type: Http
            dependsOn:
              - prompt-step
            method: POST
            url: https://api.example.com/data
            headers:
              Authorization: Bearer token
            body: "{{prompt-step.output}}"
          - name: transform-step
            type: Transform
            dependsOn:
              - http-step
            template: "Result: {{http-step.output}}"
          - name: command-step
            type: Command
            dependsOn: []
            command: echo
            arguments:
              - hello
              - world
        """;

    var json = OrchestrationParser.ConvertYamlToJson(yaml);
    var orchestration = OrchestrationParser.ParseOrchestration(json, []);

    orchestration.Steps.Should().HaveCount(4);
    orchestration.Steps[0].Should().BeOfType<PromptOrchestrationStep>();
    orchestration.Steps[1].Should().BeOfType<HttpOrchestrationStep>();
    orchestration.Steps[2].Should().BeOfType<TransformOrchestrationStep>();
    orchestration.Steps[3].Should().BeOfType<CommandOrchestrationStep>();
}

[Fact]
public void ConvertYamlToJson_EmptyYaml_ThrowsInvalidOperationException()
{
    var yaml = "";

    var act = () => OrchestrationParser.ConvertYamlToJson(yaml);

    act.Should().Throw<InvalidOperationException>()
        .WithMessage("*empty*");
}

[Fact]
public void IsYamlFile_ReturnsCorrectly()
{
    OrchestrationParser.IsYamlFile("test.yaml").Should().BeTrue();
    OrchestrationParser.IsYamlFile("test.yml").Should().BeTrue();
    OrchestrationParser.IsYamlFile("test.YAML").Should().BeTrue();
    OrchestrationParser.IsYamlFile("test.json").Should().BeFalse();
    OrchestrationParser.IsYamlFile("test.txt").Should().BeFalse();
}

[Fact]
public void ParseOrchestration_YamlWithInputs_ParsesTypedInputs()
{
    var yaml = """
        name: inputs-test
        description: Test inputs
        inputs:
          ticker:
            type: string
            description: Stock ticker symbol
            required: true
          includeHistory:
            type: boolean
            default: "true"
        steps:
          - name: step1
            type: prompt
            dependsOn: []
            parameters:
              - ticker
            systemPrompt: test
            userPrompt: "Analyze {{param.ticker}}"
            model: claude-opus-4.6
        """;

    var json = OrchestrationParser.ConvertYamlToJson(yaml);
    var orchestration = OrchestrationParser.ParseOrchestration(json, []);

    orchestration.Inputs.Should().NotBeNull();
    orchestration.Inputs.Should().ContainKey("ticker");
    orchestration.Inputs!["ticker"].Type.Should().Be(InputType.String);
    orchestration.Inputs["ticker"].Required.Should().BeTrue();
}

#endregion
```

Also update the example files test at line 1392-1411:

Change the `GetExampleFiles()` method to also include YAML files:
```csharp
public static TheoryData<string> GetExampleFiles()
{
    var data = new TheoryData<string>();
    var examplesDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples"));

    if (Directory.Exists(examplesDir))
    {
        foreach (var file in OrchestrationParser.GetOrchestrationFiles(examplesDir))
        {
            // Skip orchestra.mcp.json -- it's not an orchestration file
            if (Path.GetFileName(file).Equals("orchestra.mcp.json", StringComparison.OrdinalIgnoreCase))
                continue;

            data.Add(file);
        }
    }

    return data;
}
```

### 8. Integration test: YAML file parsing end-to-end

Write a test that creates a temp YAML file and parses it using `ParseOrchestrationFile`:

```csharp
[Fact]
public void ParseOrchestrationFile_YamlFile_ParsesSuccessfully()
{
    var yaml = """
        name: file-test
        description: Test YAML file parsing
        steps:
          - name: step1
            type: prompt
            dependsOn: []
            systemPrompt: |
              You are helpful.
            userPrompt: Hello
            model: claude-opus-4.6
        """;

    var tempDir = Path.Combine(Path.GetTempPath(), $"orchestra-yaml-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);
    var filePath = Path.Combine(tempDir, "test.yaml");

    try
    {
        File.WriteAllText(filePath, yaml);
        var orchestration = OrchestrationParser.ParseOrchestrationFile(filePath, []);

        orchestration.Name.Should().Be("file-test");
        orchestration.Steps.Should().HaveCount(1);
    }
    finally
    {
        Directory.Delete(tempDir, recursive: true);
    }
}
```

---

## Files Changed Summary

| # | File | Change Type | Scope |
|---|------|-------------|-------|
| 1 | `Directory.Packages.props` | Edit | Add YamlDotNet version |
| 2 | `src/Orchestra.Engine/Orchestra.Engine.csproj` | Edit | Add YamlDotNet reference |
| 3 | `src/Orchestra.Engine/Serialization/OrchestrationParser.cs` | Edit | Add YAML conversion helpers, update file read methods |
| 4 | `src/Orchestra.Host/Registry/OrchestrationRegistry.cs` | Edit | Update ScanDirectory + Register to handle YAML |
| 5 | `src/Orchestra.Host/Triggers/TriggerManager.cs` | Edit | Update ScanForJsonTriggers |
| 6 | `src/Orchestra.Host/Api/OrchestrationsApi.cs` | Edit | Update scan endpoint |
| 7 | `src/Orchestra.Host/McpServer/ControlPlaneTools.cs` | Edit | Update scan + descriptions |
| 8 | `playground/.../Portal/Program.cs` | Edit | Update file browser + scan |
| 9 | `playground/.../Terminal/TerminalUI.cs` | Edit | Update UI text |
| 10 | `examples/code-review-example.yaml` | New | YAML example |
| 11 | `tests/.../OrchestrationParserTests.cs` | Edit | Add YAML tests + update example enumeration |

## Risk Assessment

- **Low risk**: Purely additive. Existing JSON pipeline completely untouched.
- **YamlDotNet boolean gotcha**: YamlDotNet's default deserializer treats `yes`/`no`/`on`/`off` as booleans. However, since we use `JsonCompatible()` for the JSON output, these get serialized as `true`/`false` which `System.Text.Json` handles correctly. Users should quote version strings like `version: "1.0.0"` (otherwise YAML might interpret it as a float), but this is standard YAML practice.
- **Performance**: YAML-to-JSON conversion adds a small overhead (double serialization), but orchestration files are small and parsed infrequently (startup, manual scan). Not a concern.
- **Dependency**: YamlDotNet has 444M+ NuGet downloads, is actively maintained, and is the de facto .NET YAML library.

## Verification

After implementation:
1. `dotnet build` -- verify no compilation errors
2. `dotnet test` -- verify all existing tests pass + new YAML tests pass
3. Manual test: create a `.yaml` orchestration file and verify it loads in the playground
