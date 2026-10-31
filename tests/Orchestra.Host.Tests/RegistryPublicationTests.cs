using System.Text.Json;
using FluentAssertions;
using Orchestra.Host.Registry;
using Xunit;

namespace Orchestra.Host.Tests;

public class RegistryPublicationTests
{
	[Fact]
	public async Task ConcurrentRegistrations_PublishCompleteRegistrySnapshots()
	{
		var directory = Directory.CreateTempSubdirectory("orchestra-registry-publication-").FullName;
		try
		{
			var path = Path.Combine(directory, "registry.json");
			var registry = new OrchestrationRegistry(path, dataPath: directory);
			await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() => registry.RegisterFromJson(JsonSerializer.Serialize(new
			{
				name = $"workflow-{i}",
				description = "Concurrent create",
				steps = new[] { new { name = "render", type = "Transform", template = "done" } },
			})))));
			using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
			json.RootElement.GetArrayLength().Should().Be(32);
			var restored = new OrchestrationRegistry(path, dataPath: directory);
			restored.LoadFromDisk().Should().Be(32);
			Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories).Should().BeEmpty();
		}
		finally { Directory.Delete(directory, true); }
	}
}
