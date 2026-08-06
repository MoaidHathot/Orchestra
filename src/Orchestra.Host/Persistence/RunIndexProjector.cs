using System.Text.Json;
using Orchestra.Engine;

namespace Orchestra.Host.Persistence;

/// <summary>
/// Projects a <c>run.json</c> file into a <see cref="RunIndex"/> without materializing the full
/// <c>OrchestrationRunRecord</c> object graph.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="RunIndex"/> needs about twenty scalar fields, but the record it comes from is
/// dominated by per-step <c>trace.conversationHistory</c> and <c>content</c> — measured p50 is
/// 311 KB, p99 is 9 MB, with a 52 MB outlier. Deserializing the whole graph to read twenty fields
/// allocates that entire payload and then throws it away.
/// </para>
/// <para>
/// This reader walks the document with <see cref="Utf8JsonReader"/> and skips those subtrees
/// outright. Only <c>allStepRecords</c> is inspected below the top level, and only for the four
/// fields needed to reproduce the failure summary.
/// </para>
/// <para>
/// Deliberately tolerant: an unrecognised or malformed property is skipped rather than fatal. The
/// index is a derived cache, so a partially readable run is better than none, and a completely
/// unreadable one is reported by returning <see langword="null"/>.
/// </para>
/// </remarks>
internal static class RunIndexProjector
{
	/// <summary>
	/// Projects the given <c>run.json</c> content. Returns <see langword="null"/> when the document
	/// is not usable (unparseable, or missing the identity fields the index is keyed on).
	/// </summary>
	/// <param name="utf8Json">Raw file bytes.</param>
	/// <param name="folderPath">Absolute path of the run folder that owns the file.</param>
	public static RunIndex? Project(ReadOnlySpan<byte> utf8Json, string folderPath)
	{
		var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
		{
			CommentHandling = JsonCommentHandling.Skip,
			AllowTrailingCommas = true,
		});

		string? runId = null;
		string? orchestrationName = null;
		var orchestrationVersion = "1.0.0";
		var triggeredBy = "manual";
		DateTimeOffset startedAt = default;
		DateTimeOffset completedAt = default;
		var status = ExecutionStatus.Succeeded;
		string? triggerId = null;
		string? completionReason = null;
		string? completedByStep = null;
		var isIncomplete = false;
		string? cancellationJson = null;
		var hookExecutionCount = 0;
		string? retriedFromRunId = null;
		string? retryMode = null;
		string? parentExecutionId = null;
		string? parentStepName = null;
		string? rootExecutionId = null;
		var nestingDepth = 0;
		(string? StepName, string? ErrorMessage) failure = (null, null);

		try
		{
			if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
				return null;

			while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
			{
				if (reader.TokenType != JsonTokenType.PropertyName)
					return null;

				var name = reader.GetString();
				if (!reader.Read())
					return null;

				switch (name)
				{
					case "runId": runId = ReadNullableString(ref reader); break;
					case "orchestrationName": orchestrationName = ReadNullableString(ref reader); break;
					case "orchestrationVersion": orchestrationVersion = ReadNullableString(ref reader) ?? orchestrationVersion; break;
					case "triggeredBy": triggeredBy = ReadNullableString(ref reader) ?? triggeredBy; break;
					case "startedAt": startedAt = ReadDateTimeOffset(ref reader); break;
					case "completedAt": completedAt = ReadDateTimeOffset(ref reader); break;
					case "status": status = ReadStatus(ref reader); break;
					case "triggerId": triggerId = ReadNullableString(ref reader); break;
					case "completionReason": completionReason = ReadNullableString(ref reader); break;
					case "completedByStep": completedByStep = ReadNullableString(ref reader); break;
					case "isIncomplete": isIncomplete = reader.TokenType == JsonTokenType.True; break;
					case "retriedFromRunId": retriedFromRunId = ReadNullableString(ref reader); break;
					case "retryMode": retryMode = ReadNullableString(ref reader); break;
					case "parentExecutionId": parentExecutionId = ReadNullableString(ref reader); break;
					case "parentStepName": parentStepName = ReadNullableString(ref reader); break;
					case "rootExecutionId": rootExecutionId = ReadNullableString(ref reader); break;
					case "nestingDepth": nestingDepth = reader.TokenType == JsonTokenType.Number ? reader.GetInt32() : 0; break;

					// Small object, kept verbatim so CancellationDetails (with its computed
					// members) can be rehydrated on read without duplicating its shape here.
					case "cancellation":
						cancellationJson = reader.TokenType == JsonTokenType.Null
							? null
							: CaptureRawJson(ref reader, utf8Json);
						break;

					// Only the count is indexed; the payloads can be large.
					case "hookExecutions":
						hookExecutionCount = CountArrayElements(ref reader);
						break;

					// The only subtree worth descending into, and only for four fields per step.
					case "allStepRecords":
						failure = ExtractFailureInfo(ref reader, status);
						break;

					default:
						reader.Skip();
						break;
				}
			}
		}
		catch (JsonException)
		{
			return null;
		}

		if (string.IsNullOrEmpty(runId) || string.IsNullOrEmpty(orchestrationName))
			return null;

		// A cancelled run with no cancelled step still reports a reason, matching the
		// behaviour callers already depend on.
		if (status == ExecutionStatus.Cancelled && failure.ErrorMessage is null)
			failure = (null, "Cancelled");

		return new RunIndex
		{
			RunId = runId,
			OrchestrationName = orchestrationName,
			OrchestrationVersion = orchestrationVersion,
			TriggeredBy = triggeredBy,
			StartedAt = startedAt,
			CompletedAt = completedAt,
			Status = status,
			TriggerId = triggerId,
			FolderPath = folderPath,
			FailedStepName = failure.StepName,
			ErrorMessage = failure.ErrorMessage,
			CompletionReason = completionReason,
			CompletedByStep = completedByStep,
			IsIncomplete = isIncomplete,
			Cancellation = DeserializeCancellation(cancellationJson),
			HookExecutionCount = hookExecutionCount,
			RetriedFromRunId = retriedFromRunId,
			RetryMode = retryMode,
			ParentExecutionId = parentExecutionId,
			ParentStepName = parentStepName,
			RootExecutionId = rootExecutionId,
			NestingDepth = nestingDepth,
		};
	}

	/// <summary>
	/// Walks <c>allStepRecords</c> and returns the earliest step whose status matches the run's
	/// terminal state and which carries a message. Mirrors the eager implementation's semantics.
	/// </summary>
	private static (string? StepName, string? ErrorMessage) ExtractFailureInfo(
		ref Utf8JsonReader reader, ExecutionStatus runStatus)
	{
		var wanted = runStatus switch
		{
			ExecutionStatus.Cancelled => ExecutionStatus.Cancelled,
			ExecutionStatus.Failed => ExecutionStatus.Failed,
			_ => (ExecutionStatus?)null,
		};

		if (reader.TokenType != JsonTokenType.StartObject)
		{
			reader.Skip();
			return (null, null);
		}

		// Nothing to look for, but the subtree still has to be consumed.
		if (wanted is null)
		{
			reader.Skip();
			return (null, null);
		}

		string? bestStep = null;
		string? bestMessage = null;
		var bestStarted = DateTimeOffset.MaxValue;

		while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
		{
			if (reader.TokenType != JsonTokenType.PropertyName)
				break;

			// The dictionary key is the step name; the record repeats it as `stepName`.
			var keyName = reader.GetString();
			if (!reader.Read())
				break;

			if (reader.TokenType != JsonTokenType.StartObject)
			{
				reader.Skip();
				continue;
			}

			string? stepName = keyName;
			string? errorMessage = null;
			ExecutionStatus? stepStatus = null;
			var stepStarted = DateTimeOffset.MaxValue;

			while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
			{
				if (reader.TokenType != JsonTokenType.PropertyName)
					break;

				var field = reader.GetString();
				if (!reader.Read())
					break;

				switch (field)
				{
					case "stepName": stepName = ReadNullableString(ref reader) ?? stepName; break;
					case "status": stepStatus = ReadStatus(ref reader); break;
					case "startedAt": stepStarted = ReadDateTimeOffset(ref reader); break;
					case "errorMessage": errorMessage = ReadNullableString(ref reader); break;

					// Everything else — content, rawContent, trace, conversationHistory,
					// toolCalls, retryHistory — is skipped without being materialized. This is
					// the whole point of the streaming reader.
					default: reader.Skip(); break;
				}
			}

			if (stepStatus == wanted
				&& !string.IsNullOrEmpty(errorMessage)
				&& stepStarted < bestStarted)
			{
				bestStarted = stepStarted;
				bestStep = stepName;
				bestMessage = errorMessage;
			}
		}

		return (bestStep, bestMessage);
	}

	private static string? ReadNullableString(ref Utf8JsonReader reader) =>
		reader.TokenType == JsonTokenType.String ? reader.GetString() : null;

	private static DateTimeOffset ReadDateTimeOffset(ref Utf8JsonReader reader)
	{
		if (reader.TokenType != JsonTokenType.String)
			return default;

		return reader.TryGetDateTimeOffset(out var value) ? value : default;
	}

	/// <summary>
	/// Reads <c>ExecutionStatus</c> written either as its enum name (the store's
	/// <c>JsonStringEnumConverter</c>) or as a number (older records).
	/// </summary>
	private static ExecutionStatus ReadStatus(ref Utf8JsonReader reader)
	{
		if (reader.TokenType == JsonTokenType.Number)
			return (ExecutionStatus)reader.GetInt32();

		var text = ReadNullableString(ref reader);
		return Enum.TryParse<ExecutionStatus>(text, ignoreCase: true, out var parsed)
			? parsed
			: ExecutionStatus.Succeeded;
	}

	private static int CountArrayElements(ref Utf8JsonReader reader)
	{
		if (reader.TokenType != JsonTokenType.StartArray)
		{
			reader.Skip();
			return 0;
		}

		var count = 0;
		while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
		{
			count++;
			reader.Skip();
		}

		return count;
	}

	/// <summary>Captures the current value's raw JSON text so it can be deserialized separately.</summary>
	private static string CaptureRawJson(ref Utf8JsonReader reader, ReadOnlySpan<byte> source)
	{
		var start = (int)reader.TokenStartIndex;
		reader.Skip();
		var end = (int)(reader.BytesConsumed);
		return System.Text.Encoding.UTF8.GetString(source[start..end]);
	}

	private static CancellationDetails? DeserializeCancellation(string? json)
	{
		if (string.IsNullOrWhiteSpace(json))
			return null;

		try
		{
			return JsonSerializer.Deserialize<CancellationDetails>(json, s_cancellationOptions);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static readonly JsonSerializerOptions s_cancellationOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
	};
}
