namespace Orchestra.Engine;

/// <summary>
/// Abstracts the formatting of prompts and context for LLM interactions.
/// Implement this interface to customize how dependency outputs, feedback,
/// and transformation prompts are formatted for different LLM providers or use cases.
/// </summary>
public interface IPromptFormatter
{
	/// <summary>
	/// Formats multiple dependency outputs into a single string for inclusion in a prompt.
	/// </summary>
	/// <param name="dependencyOutputs">Dictionary of step name to output content.</param>
	/// <returns>Formatted string containing all dependency outputs.</returns>
	string FormatDependencyOutputs(IReadOnlyDictionary<string, string> dependencyOutputs);

	/// <summary>
	/// Builds the complete user prompt including the original prompt, dependency context, and any loop feedback.
	/// </summary>
	/// <param name="userPrompt">The original user prompt (with parameters already injected).</param>
	/// <param name="dependencyOutputs">Formatted dependency outputs (from FormatDependencyOutputs).</param>
	/// <param name="loopFeedback">Optional feedback from a previous loop iteration.</param>
	/// <param name="inputHandlerPrompt">Optional input handler prompt for custom formatting.</param>
	/// <returns>The complete formatted user prompt.</returns>
	string BuildUserPrompt(
		string userPrompt,
		string dependencyOutputs,
		string? loopFeedback = null,
		string? inputHandlerPrompt = null);

	/// <summary>
	/// Creates the system prompt for content transformation handlers (input/output handlers).
	/// </summary>
	/// <param name="handlerInstructions">The transformation instructions provided by the user.</param>
	/// <returns>The complete system prompt for the transformation agent.</returns>
	string BuildTransformationSystemPrompt(string handlerInstructions);

	/// <summary>
	/// Wraps content for transformation, ensuring the LLM treats it as data to transform
	/// rather than instructions to follow.
	/// </summary>
	/// <param name="content">The content to be transformed.</param>
	/// <returns>The wrapped content ready to send to the transformation agent.</returns>
	string WrapContentForTransformation(string content);
}
