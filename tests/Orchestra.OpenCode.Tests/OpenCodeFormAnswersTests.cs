using System.Text.Json;
using FluentAssertions;
using Orchestra.Engine;

namespace Orchestra.OpenCode.Tests;

public class OpenCodeFormAnswersTests
{
	[Fact]
	public async Task MultipleFields_UseReadableLabelsAndNativeAnswerTypes()
	{
		var (context, waiter, prompts) = Context("Production", "yes", "42", "1.25", "Alpha, Beta");
		var answer = await OpenCodeFormAnswers.ReadAsync(Form("""
			[
			  {"key":"environment","type":"string","title":"Environment","description":"Where should this run?","options":[{"value":"prod-id","label":"Production","description":"Customer traffic"}]},
			  {"key":"enabled","type":"boolean","title":"Enable it?"},
			  {"key":"count","type":"integer","title":"Count"},
			  {"key":"ratio","type":"number","title":"Ratio"},
			  {"key":"targets","type":"multiselect","title":"Targets","options":[{"value":"a","label":"Alpha"},{"value":"b","label":"Beta"}]}
			]
			"""), context, default);

		answer.Should().NotBeNull();
		answer!["environment"].Should().Be("prod-id");
		answer["enabled"].Should().BeOfType<bool>().Which.Should().BeTrue();
		answer["count"].Should().BeOfType<long>().Which.Should().Be(42);
		answer["ratio"].Should().BeOfType<double>().Which.Should().Be(1.25);
		answer["targets"].Should().BeOfType<string[]>().Which.Should().Equal("a", "b");
		prompts.Should().HaveCount(5);
		prompts[0].Prompt.Should().Contain("Where should this run?").And.Contain("Customer traffic").And.NotContain("\"key\"");
		prompts[0].Choices.Should().Equal("Production");
		prompts[1].Choices.Should().Equal("yes", "no");
		waiter.EndCount.Should().Be(5);
	}

	[Fact]
	public async Task HiddenDefaultsAndConditions_SkipIrrelevantFieldsAndPreserveJsonTypes()
	{
		var (context, _, prompts) = Context("yes", "included", "different-type");
		Dictionary<string, object>? answers;
		using (var document = JsonDocument.Parse("""
			{"title":"Conditional form","fields":[
			  {"key":"seed","type":"integer","hidden":true,"default":1},
			  {"key":"absent","type":"string","hidden":true},
			  {"key":"enabled","type":"boolean","title":"Enable it?"},
			  {"key":"active","type":"string","title":"Active","when":[{"key":"enabled","op":"eq","value":true},{"key":"seed","op":"eq","value":1}]},
			  {"key":"inactive","type":"string","title":"Inactive","when":[{"key":"enabled","op":"eq","value":false}]},
			  {"key":"stringSeed","type":"string","title":"Wrong type","when":[{"key":"seed","op":"eq","value":"1"}]},
			  {"key":"differentType","type":"string","title":"Different type","when":[{"key":"seed","op":"neq","value":"1"}]}
			]}
			"""))
		{
			answers = await OpenCodeFormAnswers.ReadAsync(document.RootElement, context, default);
		}

		answers!.Keys.Should().BeEquivalentTo("seed", "enabled", "active", "differentType");
		((JsonElement)answers["seed"]).GetInt32().Should().Be(1, "defaults must survive disposal of the original form document");
		answers["active"].Should().Be("included");
		prompts.Should().HaveCount(3);
		prompts.Should().OnlyContain(p => !p.Prompt.Contains("Inactive") && !p.Prompt.Contains("Wrong type"));
	}

	[Fact]
	public async Task BlankReply_UsesAnOwnedDefaultValue()
	{
		var (context, _, prompts) = Context(" ");
		Dictionary<string, object>? answer;
		using (var document = JsonDocument.Parse("""{"title":"Defaults","fields":[{"key":"count","type":"integer","title":"Count","default":7}]}"""))
			answer = await OpenCodeFormAnswers.ReadAsync(document.RootElement, context, default);
		((JsonElement)answer!["count"]).GetInt32().Should().Be(7);
		prompts.Single().Prompt.Should().Contain("Default: 7");
	}

	[Fact]
	public async Task InvalidTypedReply_RepromptsAndReturnsTheCorrectedValue()
	{
		var (context, _, prompts) = Context("forty two", "42");
		var answer = await OpenCodeFormAnswers.ReadAsync(Form("""[{"key":"count","type":"integer","title":"Count"}]"""), context, default);
		answer!["count"].Should().Be(42L);
		prompts.Should().HaveCount(2);
		prompts[1].Prompt.Should().StartWith("Please enter a valid integer value.");
	}

	[Fact]
	public async Task InvalidReplies_StopAtTheRetryLimit()
	{
		var (context, waiter, prompts) = Context("wrong", "still wrong", "wrong again");
		Func<Task> read = async () => await OpenCodeFormAnswers.ReadAsync(Form("""[{"key":"count","type":"integer"}]"""), context, default);
		await read.Should().ThrowAsync<FormatException>();
		prompts.Should().HaveCount(3);
		waiter.EndCount.Should().Be(3);
	}

	[Theory]
	[InlineData(false, 2, "prod-id")]
	[InlineData(true, 1, "custom-environment")]
	public async Task CustomChoicePolicy_IsEnforced(bool custom, int expectedQuestions, string expectedAnswer)
	{
		var (context, _, prompts) = Context("custom-environment", "Production");
		var form = Form($$"""[{"key":"environment","type":"string","custom":{{custom.ToString().ToLowerInvariant()}},"options":[{"value":"prod-id","label":"Production"}]}]""");
		var answer = await OpenCodeFormAnswers.ReadAsync(form, context, default);
		answer!["environment"].Should().Be(expectedAnswer);
		prompts.Should().HaveCount(expectedQuestions);
	}

	[Theory]
	[InlineData("[\"Alpha\",\"Beta\"]")]
	[InlineData("Alpha, Beta")]
	public async Task MultiSelect_AcceptsJsonOrCommaSeparatedLabels(string reply)
	{
		var (context, _, _) = Context(reply);
		var answer = await OpenCodeFormAnswers.ReadAsync(Form("""[{"key":"targets","type":"multiselect","options":[{"value":"a","label":"Alpha"},{"value":"b","label":"Beta"}]}]"""), context, default);
		answer!["targets"].Should().BeOfType<string[]>().Which.Should().Equal("a", "b");
	}

	[Theory]
	[InlineData("[1]")]
	[InlineData("[null]")]
	[InlineData("[\"Alpha\",null]")]
	[InlineData("[broken")]
	public void MultiSelect_RejectsMalformedOrNonStringSelections(string reply)
	{
		Action parse = () => OpenCodeFormAnswers.ConvertValue("multiselect", reply, []);
		parse.Should().Throw<FormatException>();
	}

	[Fact]
	public async Task NullSelection_RepromptsEvenWhenCustomChoicesAreAllowed()
	{
		var (context, _, prompts) = Context("[null]", "[\"Beta\"]");
		var answer = await OpenCodeFormAnswers.ReadAsync(Form("""[{"key":"targets","type":"multiselect","custom":true,"options":[{"value":"b","label":"Beta"}]}]"""), context, default);
		answer!["targets"].Should().BeOfType<string[]>().Which.Should().Equal("b");
		prompts.Should().HaveCount(2);
		prompts[1].Prompt.Should().StartWith("Please enter a valid multiselect value.");
	}

	[Fact]
	public async Task MissingRunIdentity_DeclinesWithoutWaiting()
	{
		var (_, waiter, _) = Context("unused");
		var answer = await OpenCodeFormAnswers.ReadAsync(Form("""[{"key":"answer","type":"string"}]"""), new() { HumanInputWaiter = waiter }, default);
		answer.Should().BeNull();
		waiter.CallCount.Should().Be(0);
	}

	[Fact]
	public async Task ExternalBrowserField_FailsBeforeAskingForAnUnsupportedAnswer()
	{
		var (context, waiter, prompts) = Context();
		Func<Task> read = async () => await OpenCodeFormAnswers.ReadAsync(Form("""[{"key":"login","type":"external","url":"https://example.test/login"}]"""), context, default);
		await read.Should().ThrowAsync<NotSupportedException>().WithMessage("*external browser*");
		waiter.CallCount.Should().Be(0);
		prompts.Should().BeEmpty();
	}

	[Fact]
	public async Task CancellationDuringAQuestion_UnwindsTheInputWait()
	{
		var (context, waiter, prompts) = Context();
		waiter.Block = true;
		using var cancellation = new CancellationTokenSource();
		var read = OpenCodeFormAnswers.ReadAsync(Form("""[{"key":"answer","type":"string"}]"""), context, cancellation.Token);
		await waiter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await cancellation.CancelAsync();
		Func<Task> result = async () => await read.WaitAsync(TimeSpan.FromSeconds(5));
		await result.Should().ThrowAsync<OperationCanceledException>();
		waiter.EndCount.Should().Be(1);
		prompts.Should().ContainSingle();
	}

	private static JsonElement Form(string fields) => JsonSerializer.Deserialize<JsonElement>($$"""{"title":"Configuration","fields":{{fields}}}""");

	private static (EngineToolContext Context, AnswerWaiter Waiter, List<PendingInputRecord> Prompts) Context(params string[] replies)
	{
		var waiter = new AnswerWaiter(replies);
		var prompts = new List<PendingInputRecord>();
		return (new EngineToolContext { OrchestrationName = "form-test", RunId = "run", StepName = "step", HumanInputWaiter = waiter, OnAwaitingInput = prompts.Add }, waiter, prompts);
	}

	private sealed class AnswerWaiter(string[] replies) : IHumanInputWaiter
	{
		private readonly Queue<string> _replies = new(replies);
		public bool Block { get; set; }
		public int CallCount { get; private set; }
		public int EndCount { get; private set; }
		public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public async Task<UserInputResponse> WaitAsync(string orchestration, string run, string step, CancellationToken token)
		{
			CallCount++;
			Entered.TrySetResult();
			if (Block)
				await Task.Delay(Timeout.Infinite, token);
			return new() { Reply = _replies.Dequeue(), RespondedAt = DateTimeOffset.UtcNow };
		}
		public bool TryComplete(string orchestration, string run, string step, UserInputResponse response) => false;
		public bool TryCancel(string orchestration, string run, string step) => false;
		public void BeginWait(string run, string step) { }
		public void EndWait(string run, string step) => EndCount++;
	}
}
