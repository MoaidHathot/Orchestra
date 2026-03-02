namespace Orchestra.Engine;

/// <summary>
/// Default implementation of IPromptFormatter using markdown-style formatting.
/// This formatter uses separators (---), headers (##), and XML-like tags for content wrapping.
/// </summary>
public class DefaultPromptFormatter : IPromptFormatter
{
	/// <summary>
	/// Singleton instance for convenience when not using DI.
	/// </summary>
	public static IPromptFormatter Instance { get; } = new DefaultPromptFormatter();

	/// <inheritdoc />
	public string FormatDependencyOutputs(IReadOnlyDictionary<string, string> dependencyOutputs)
	{
		if (dependencyOutputs.Count == 0)
			return string.Empty;

		if (dependencyOutputs.Count == 1)
		{
			return dependencyOutputs.Values.First();
		}

		return string.Join("\n\n---\n\n",
			dependencyOutputs.Select(kvp => $"## Output from '{kvp.Key}':\n{kvp.Value}"));
	}

	/// <inheritdoc />
	public string BuildUserPrompt(
		string userPrompt,
		string dependencyOutputs,
		string? loopFeedback = null,
		string? inputHandlerPrompt = null)
	{
		if (string.IsNullOrEmpty(dependencyOutputs) && loopFeedback is null)
			return userPrompt;

		string result;

		if (inputHandlerPrompt is not null)
		{
			result = $"""
				{inputHandlerPrompt}

				---
				Previous step outputs:
				{dependencyOutputs}

				---
				Task:
				{userPrompt}
				""";
		}
		else
		{
			result = $"""
				{userPrompt}

				---
				Context from previous steps:
				{dependencyOutputs}
				""";
		}

		if (loopFeedback is not null)
		{
			result += $"""


				---
				Feedback from previous attempt (use this to improve your output):
				{loopFeedback}
				""";
		}

		return result;
	}

	/// <inheritdoc />
	public string BuildTransformationSystemPrompt(string handlerInstructions)
	{
		return $"""
			You are a stateless content transformation function.

			CRITICAL RULES:
			1. You receive INPUT CONTENT and TRANSFORMATION INSTRUCTIONS
			2. You output ONLY the transformed content - nothing else
			3. Do NOT engage in conversation, ask questions, or add commentary
			4. Do NOT reference any external context, projects, or repositories
			5. Do NOT add greetings, offers to help, or clarifying questions
			6. Simply apply the transformation and output the result

			TRANSFORMATION INSTRUCTIONS:
			{handlerInstructions}

			OUTPUT FORMAT:
			Return ONLY the transformed content. No preamble. No commentary. No follow-up questions.
			""";
	}

	/// <inheritdoc />
	public string WrapContentForTransformation(string content)
	{
		return $"""
			<INPUT_CONTENT>
			{content}
			</INPUT_CONTENT>

			Transform the content above according to your instructions. Output ONLY the transformed content.
			""";
	}
}
