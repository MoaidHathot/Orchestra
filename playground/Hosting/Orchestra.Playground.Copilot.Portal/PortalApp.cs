using Microsoft.Extensions.Logging.Console;
using Orchestra.Copilot;
using Orchestra.Engine;
using Orchestra.Host.Extensions;
using Orchestra.Host.Hosting;
using Orchestra.Host.McpServer;

namespace Orchestra.Playground.Copilot.Portal;

internal static class PortalApp
{
	public static async Task RunAsync(string[] args, Type loggerCategoryType)
	{
		await RunAsync(args, loggerCategoryType, useAppBaseContentRoot: false);
	}

	public static async Task RunAsync(string[] args, Type loggerCategoryType, bool useAppBaseContentRoot)
	{
		ConfigureThreadPool();
		var orchestraConfig = OrchestraConfigLoader.Load();

		var builder = useAppBaseContentRoot
			? WebApplication.CreateBuilder(new WebApplicationOptions
			{
				Args = args,
				ContentRootPath = AppContext.BaseDirectory,
				WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
			})
			: WebApplication.CreateBuilder(args);

		ApplyUrlBindingFallback(builder.Configuration, orchestraConfig);

		builder.Logging.AddSimpleConsole(options =>
		{
			options.SingleLine = true;
			options.IncludeScopes = false;
			options.TimestampFormat = "HH:mm:ss ";
			options.ColorBehavior = LoggerColorBehavior.Enabled;
		});

		builder.Services.AddSingleton<AgentBuilder, CopilotAgentBuilder>();

		builder.Services.AddOrchestraHost((options, configuration) =>
		{
			var dataPath = configuration["data-path"]
				?? Environment.GetEnvironmentVariable("ORCHESTRA_PORTAL_DATA_PATH")
				?? configuration["executions-path"];

			if (dataPath is not null)
				options.DataPath = dataPath;

			var scanPath = Environment.GetEnvironmentVariable("ORCHESTRA_ORCHESTRATIONS_PATH")
				?? configuration["orchestrations-path"];
			if (scanPath is not null)
			{
				if (options.Scan is not null)
					options.Scan.Directory = scanPath;
				else
					options.Scan = new ScanConfig { Directory = scanPath };
			}

			options.LoadPersistedOrchestrations = true;
			options.RegisterJsonTriggers = true;
		});

		builder.Services.AddOrchestraMcpServer();
		builder.Services.AddSingleton<PortalStatusService>();

		var app = builder.Build();

		await app.Services.InitializeOrchestraHostAsync();

		var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(loggerCategoryType);
		var resolvedDataPath = app.Services.GetRequiredService<OrchestrationHostOptions>().DataPath;
		startupLogger.LogInformation("Orchestra Portal started with data path: {DataPath}", resolvedDataPath);

		app.UseStaticFiles();
		app.MapOrchestraHostEndpoints();
		app.MapOrchestraMcpEndpoints();
		MapPortalEndpoints(app);

		await app.RunAsync();
	}

	private static void ConfigureThreadPool()
	{
		ThreadPool.GetMinThreads(out var workerMin, out var ioMin);
		var targetMin = Math.Max(workerMin, 64);
		var targetIoMin = Math.Max(ioMin, 64);
		ThreadPool.SetMinThreads(targetMin, targetIoMin);
	}

	private static void ApplyUrlBindingFallback(IConfigurationManager configuration, OrchestraConfigFile? orchestraConfig)
	{
		if (string.IsNullOrWhiteSpace(orchestraConfig?.Urls))
			return;

		var hasExplicitUrls = !string.IsNullOrWhiteSpace(configuration["Urls"])
			|| !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
			|| !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_URLS"));

		if (hasExplicitUrls)
			return;

		configuration["Urls"] = orchestraConfig.Urls;
	}

	private static void MapPortalEndpoints(WebApplication app)
	{
		app.MapGet("/api/browse", ([AsParameters] BrowseRequest request) =>
		{
			var directory = request.Directory;
			if (string.IsNullOrWhiteSpace(directory))
				directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

			if (!Directory.Exists(directory))
				return Results.BadRequest(new { error = $"Directory not found: {directory}" });

			try
			{
				var entries = new List<object>();

				var parent = Directory.GetParent(directory);
				if (parent != null)
				{
					entries.Add(new
					{
						name = "..",
						path = parent.FullName,
						isDirectory = true,
						isParent = true
					});
				}

				foreach (var dir in Directory.GetDirectories(directory).OrderBy(d => d))
				{
					var dirInfo = new DirectoryInfo(dir);
					entries.Add(new
					{
						name = dirInfo.Name,
						path = dirInfo.FullName,
						isDirectory = true,
						isParent = false
					});
				}

				foreach (var file in OrchestrationParser.GetOrchestrationFiles(directory).OrderBy(f => f))
				{
					var fileInfo = new FileInfo(file);
					entries.Add(new
					{
						name = fileInfo.Name,
						path = fileInfo.FullName,
						isDirectory = false,
						isParent = false,
						size = fileInfo.Length
					});
				}

				return Results.Json(new
				{
					currentDirectory = directory,
					entries
				});
			}
			catch (Exception ex)
			{
				return Results.BadRequest(new { error = ex.Message });
			}
		});

		app.MapGet("/api/folder/browse", async (IWebHostEnvironment env) =>
		{
			if (env.EnvironmentName == "Testing")
				return Results.Json(new { cancelled = true, path = (string?)null });

			if (!OperatingSystem.IsWindows())
				return Results.BadRequest(new { error = "Native folder dialog is only supported on Windows." });

			try
			{
				var psi = new System.Diagnostics.ProcessStartInfo
				{
					FileName = "powershell",
					Arguments = "-NoProfile -Command \"Add-Type -AssemblyName System.Windows.Forms; $d = New-Object System.Windows.Forms.FolderBrowserDialog; $d.Description = 'Select orchestration folder'; $d.ShowNewFolderButton = $false; if ($d.ShowDialog() -eq 'OK') { $d.SelectedPath } else { '' }\"",
					RedirectStandardOutput = true,
					UseShellExecute = false,
					CreateNoWindow = true,
				};

				using var proc = System.Diagnostics.Process.Start(psi);
				if (proc is null)
					return Results.BadRequest(new { error = "Failed to launch folder dialog." });

				var output = (await proc.StandardOutput.ReadToEndAsync()).Trim();
				await proc.WaitForExitAsync();

				return string.IsNullOrEmpty(output)
					? Results.Json(new { cancelled = true, path = (string?)null })
					: Results.Json(new { cancelled = false, path = output });
			}
			catch (Exception ex)
			{
				return Results.BadRequest(new { error = ex.Message });
			}
		});

		app.MapPost("/api/folder/scan", (FolderScanRequest request) =>
		{
			try
			{
				if (string.IsNullOrWhiteSpace(request.Directory))
					return Results.BadRequest(new { error = "Directory path is required." });

				if (!Directory.Exists(request.Directory))
					return Results.BadRequest(new { error = $"Directory not found: {request.Directory}" });

				var files = OrchestrationParser.GetOrchestrationFiles(request.Directory);
				var orchestrations = new List<object>();

				foreach (var file in files.OrderBy(f => f))
				{
					if (Path.GetFileName(file).Equals("orchestra.mcp.json", StringComparison.OrdinalIgnoreCase))
						continue;

					try
					{
						var orchestration = OrchestrationParser.ParseOrchestrationFileMetadataOnly(file);

						orchestrations.Add(new
						{
							path = file,
							fileName = Path.GetFileName(file),
							name = orchestration.Name,
							description = orchestration.Description,
							stepCount = orchestration.Steps.Length,
							steps = orchestration.Steps.Select(s => s.Name).ToArray(),
							parameters = orchestration.Steps.SelectMany(s => s.Parameters).Distinct().ToArray(),
							hasInlineMcps = orchestration.Mcps.Length > 0,
							inlineMcpNames = orchestration.Mcps.Select(m => m.Name).ToArray(),
							valid = true,
							error = (string?)null,
						});
					}
					catch (Exception ex)
					{
						orchestrations.Add(new
						{
							path = file,
							fileName = Path.GetFileName(file),
							name = (string?)null,
							description = (string?)null,
							stepCount = 0,
							steps = Array.Empty<string>(),
							parameters = Array.Empty<string>(),
							hasInlineMcps = false,
							inlineMcpNames = Array.Empty<string>(),
							valid = false,
							error = ex.Message,
						});
					}
				}

				return Results.Json(new
				{
					directory = request.Directory,
					count = orchestrations.Count,
					orchestrations,
				});
			}
			catch (Exception ex)
			{
				return Results.BadRequest(new { error = ex.Message });
			}
		});

		app.MapGet("/api/file/read", (string path) =>
		{
			try
			{
				if (string.IsNullOrWhiteSpace(path))
					return Results.BadRequest(new { error = "File path is required." });

				if (!System.IO.File.Exists(path))
					return Results.NotFound(new { error = $"File not found: {path}" });

				var content = System.IO.File.ReadAllText(path);
				return Results.Content(content, "application/json");
			}
			catch (Exception ex)
			{
				return Results.BadRequest(new { error = ex.Message });
			}
		});

		app.MapFallbackToFile("index.html");
	}

	private sealed record BrowseRequest(string? Directory);
	private sealed record FolderScanRequest(string? Directory);
}
