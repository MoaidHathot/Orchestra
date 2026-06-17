using System.Text.Json;

namespace Orchestra.OpenCode.Tests;

internal static class TestEvents
{
	/// <summary>Builds an <see cref="OpenCodeServerEvent"/> from a type and a properties JSON literal.</summary>
	public static OpenCodeServerEvent Event(string type, string propertiesJson)
	{
		using var doc = JsonDocument.Parse(propertiesJson);
		return new OpenCodeServerEvent { Type = type, Properties = doc.RootElement.Clone() };
	}
}
