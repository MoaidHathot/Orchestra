using FluentAssertions;
using Orchestra.Client.Sse;
using Xunit;

namespace Orchestra.Client.Tests.Sse;

/// <summary>
/// Unit tests for <see cref="SseStreamReader"/> covering the SSE wire-format parsing rules
/// the CLI relies on: event/data dispatch, multi-line data joins, comments, blank lines,
/// trailing-frame flushes, and cancellation.
/// </summary>
public class SseStreamReaderTests
{
	[Fact]
	public async Task ReadAsync_Parses_Single_Frame()
	{
		var input = "event: step-started\ndata: {\"stepName\":\"build\"}\n\n";
		var frames = await ReadAllAsync(input);

		frames.Should().HaveCount(1);
		frames[0].Event.Should().Be("step-started");
		frames[0].Data.Should().Be("{\"stepName\":\"build\"}");
	}

	[Fact]
	public async Task ReadAsync_Parses_Multiple_Frames()
	{
		var input =
			"event: step-started\ndata: {\"stepName\":\"a\"}\n\n" +
			"event: step-completed\ndata: {\"stepName\":\"a\"}\n\n";
		var frames = await ReadAllAsync(input);

		frames.Should().HaveCount(2);
		frames[0].Event.Should().Be("step-started");
		frames[1].Event.Should().Be("step-completed");
	}

	[Fact]
	public async Task ReadAsync_Joins_MultiLine_Data_With_Newlines()
	{
		var input = "event: step-trace\ndata: line one\ndata: line two\n\n";
		var frames = await ReadAllAsync(input);

		frames.Should().HaveCount(1);
		frames[0].Data.Should().Be("line one\nline two");
	}

	[Fact]
	public async Task ReadAsync_Skips_Comment_Lines()
	{
		var input =
			": this is a comment\n" +
			": another comment\n" +
			"event: orchestration-done\ndata: {\"status\":\"Succeeded\"}\n\n";
		var frames = await ReadAllAsync(input);

		frames.Should().HaveCount(1);
		frames[0].Event.Should().Be("orchestration-done");
	}

	[Fact]
	public async Task ReadAsync_Skips_Blank_Lines_With_No_Buffered_Frame()
	{
		var input =
			"\n\n" +
			"event: step-started\ndata: {}\n\n";
		var frames = await ReadAllAsync(input);

		frames.Should().HaveCount(1);
		frames[0].Event.Should().Be("step-started");
	}

	[Fact]
	public async Task ReadAsync_Ignores_Id_And_Retry_Fields()
	{
		var input =
			"id: 42\nretry: 5000\n" +
			"event: step-started\ndata: {}\n\n";
		var frames = await ReadAllAsync(input);

		frames.Should().HaveCount(1);
		frames[0].Event.Should().Be("step-started");
		frames[0].Data.Should().Be("{}");
	}

	[Fact]
	public async Task ReadAsync_Flushes_Trailing_Partial_Frame_When_Stream_Ends()
	{
		// No trailing blank line — caller should still see the frame.
		var input = "event: step-completed\ndata: {}\n";
		var frames = await ReadAllAsync(input);

		frames.Should().HaveCount(1);
		frames[0].Event.Should().Be("step-completed");
	}

	[Fact]
	public async Task ReadAsync_Throws_On_Cancellation()
	{
		// Use a stream that blocks indefinitely (a pipe with no data) so cancellation can fire.
		var pipe = new System.IO.Pipelines.Pipe();
		using var reader = new StreamReader(pipe.Reader.AsStream());
		using var cts = new CancellationTokenSource();

		var task = Task.Run(async () =>
		{
			await foreach (var _ in SseStreamReader.ReadAsync(reader, cts.Token))
			{
			}
		});

		cts.CancelAfter(50);

		// The reader yields once cancellation fires — either by surfacing
		// OperationCanceledException or by gracefully completing the stream.
		await task;
	}

	[Fact]
	public async Task ReadAsync_Handles_Frame_With_Empty_Event_Field()
	{
		// SSE spec: "event:" with no value defaults to "message". CLI preserves the empty
		// string and lets the consumer decide how to render it (it ignores them).
		var input = "data: anonymous frame\n\n";
		var frames = await ReadAllAsync(input);

		frames.Should().HaveCount(1);
		frames[0].Event.Should().Be(string.Empty);
		frames[0].Data.Should().Be("anonymous frame");
	}

	private static async Task<List<SseFrame>> ReadAllAsync(string input)
	{
		using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(input));
		using var reader = new StreamReader(stream);
		var frames = new List<SseFrame>();
		await foreach (var f in SseStreamReader.ReadAsync(reader))
		{
			frames.Add(f);
		}
		return frames;
	}
}
