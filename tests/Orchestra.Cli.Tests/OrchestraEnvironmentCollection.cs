using Xunit;

namespace Orchestra.Cli.Tests;

/// <summary>
/// Serializes every test class that mutates process-wide environment variables read by
/// configuration discovery (<c>ORCHESTRA_CONFIG_PATH</c>, <c>XDG_CONFIG_HOME</c>,
/// <c>ORCHESTRA_URL</c>).
/// </summary>
/// <remarks>
/// <para>
/// xUnit runs distinct collections in parallel, so having one collection per test class is not
/// enough: two classes each serialized internally can still interleave with each other and
/// clobber the same variable. Every such class must share <em>this</em> collection. Symptom of
/// getting it wrong is a low-frequency flake — an orchestration registry coming back empty
/// because another class blanked <c>ORCHESTRA_CONFIG_PATH</c> mid-test.
/// </para>
/// <para>
/// <c>DisableParallelization</c> extends that guarantee outward: classes that only <em>read</em>
/// configuration (<c>OrchestraHostSessionTests</c> resolving a server URL, for instance) live in
/// other collections and would otherwise observe a temp <c>orchestra.json</c> that a test here
/// had pointed the environment at.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OrchestraEnvironmentCollection
{
	public const string Name = "orchestra-environment";
}
