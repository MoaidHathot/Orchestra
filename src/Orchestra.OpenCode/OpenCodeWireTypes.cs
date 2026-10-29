using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orchestra.OpenCode;

/// <summary>
/// A raw V2 event from <c>GET /api/event</c>: a type discriminator plus its untyped
/// <c>data</c> envelope in <see cref="Properties"/>.
/// All wire-shape interpretation lives in <c>OpenCodeSessionHandler</c> so it can be
/// unit-tested from JSON fixtures and the transport stays a thin pass-through.
/// </summary>
public sealed record OpenCodeServerEvent
{
	public required string Type { get; init; }
	public JsonElement Properties { get; init; }
}

/// <summary>V2 model reference selected at session creation.</summary>
internal sealed record OpenCodeModelDto
{
	[JsonPropertyName("providerID")] public required string ProviderId { get; init; }
	[JsonPropertyName("id")] public required string ModelId { get; init; }
	[JsonPropertyName("variant")] public string? Variant { get; init; }
}

/// <summary>A message part sent to OpenCode (text or file).</summary>
internal sealed record OpenCodePartDto
{
	[JsonPropertyName("type")] public required string Type { get; init; }
	[JsonPropertyName("text")] public string? Text { get; init; }
	[JsonPropertyName("mime")] public string? Mime { get; init; }
	[JsonPropertyName("filename")] public string? Filename { get; init; }
	[JsonPropertyName("url")] public string? Url { get; init; }

	public static OpenCodePartDto TextPart(string text) => new() { Type = "text", Text = text };
}

/// <summary>Adapter prompt plan. Transport sends text/files; model and agent are selected at session creation.</summary>
internal sealed record OpenCodePromptRequest
{
	[JsonPropertyName("model")] public required OpenCodeModelDto Model { get; init; }
	[JsonPropertyName("agent")] public string? Agent { get; init; }
	[JsonPropertyName("system")] public string? System { get; init; }
	[JsonPropertyName("parts")] public required IReadOnlyList<OpenCodePartDto> Parts { get; init; }
}

/// <summary>Body for V2 <c>POST /api/session</c>.</summary>
internal sealed record OpenCodeCreateSessionRequest
{
	[JsonPropertyName("title")] public string? Title { get; init; }
	[JsonPropertyName("location")] public object? Location { get; init; }
	[JsonPropertyName("model")] public OpenCodeModelDto? Model { get; init; }
	[JsonPropertyName("agent")] public string? Agent { get; init; }
	[JsonPropertyName("permissions")] public object? Permissions { get; init; }
}

internal static class OpenCodeJson
{
	/// <summary>
	/// Shared options. OpenCode's wire casing is irregular (<c>providerID</c>, <c>sessionID</c>,
	/// <c>callID</c>), so DTOs pin names explicitly via <see cref="JsonPropertyNameAttribute"/>
	/// rather than relying on a naming policy.
	/// </summary>
	public static readonly JsonSerializerOptions Options = new()
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		PropertyNameCaseInsensitive = true,
	};
}
