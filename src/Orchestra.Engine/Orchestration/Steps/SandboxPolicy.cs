namespace Orchestra.Engine;

/// <summary>
/// Opt-in sandbox policy constraining a Prompt step's shell/file/network tool access.
/// Applied to the Copilot session via the runtime's options-update RPC. When null (the
/// default) no sandbox is configured and the runtime default applies.
/// </summary>
public sealed class SandboxPolicy
{
	/// <summary>When false, the policy is ignored (no sandbox applied). Defaults to true.</summary>
	public bool Enabled { get; init; } = true;

	/// <summary>Optional filesystem restrictions.</summary>
	public SandboxFilesystemPolicy? Filesystem { get; init; }

	/// <summary>Optional network restrictions.</summary>
	public SandboxNetworkPolicy? Network { get; init; }
}

/// <summary>Filesystem section of a <see cref="SandboxPolicy"/>.</summary>
public sealed class SandboxFilesystemPolicy
{
	/// <summary>Paths the agent may read but not write.</summary>
	public string[] ReadonlyPaths { get; init; } = [];

	/// <summary>Paths the agent may read and write.</summary>
	public string[] ReadwritePaths { get; init; } = [];

	/// <summary>Paths the agent may not access at all.</summary>
	public string[] DeniedPaths { get; init; } = [];
}

/// <summary>Network section of a <see cref="SandboxPolicy"/>.</summary>
public sealed class SandboxNetworkPolicy
{
	/// <summary>Hosts the agent's tools may reach (allow-list).</summary>
	public string[] AllowedHosts { get; init; } = [];

	/// <summary>Hosts the agent's tools may not reach (deny-list).</summary>
	public string[] BlockedHosts { get; init; } = [];

	/// <summary>Whether outbound network access is permitted at all.</summary>
	public bool? AllowOutbound { get; init; }

	/// <summary>Whether access to the local network (loopback / private ranges) is permitted.</summary>
	public bool? AllowLocalNetwork { get; init; }
}
