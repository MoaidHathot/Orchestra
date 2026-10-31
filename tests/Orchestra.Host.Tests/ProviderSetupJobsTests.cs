using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestra.Host.Workspaces;
using Xunit;

namespace Orchestra.Host.Tests;

public class ProviderSetupJobsTests
{
	[Fact]
	public async Task Login_SurfacesTheCodeAndJoinsCancellation_WithoutStartingDuplicateJobs()
	{
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var backend = new Backend(async (progress, readCode, token) =>
		{
			progress(new("Enter the code", "https://example.com/login", RequiresCode: true));
			entered.SetResult();
			try { await readCode(token); }
			finally { exited.SetResult(); }
			return new("copilot", true, true, "Connected", [], []);
		});
		await using var jobs = new ProviderSetupJobs(backend, Substitute.For<IHostApplicationLifetime>(), NullLogger<ProviderSetupJobs>.Instance);
		var job = jobs.Start("copilot", new("login"));
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
		jobs.Start("copilot", new("login")).Id.Should().Be(job.Id);
		jobs.Get(job.Id)!.Progress.RequiresCode.Should().BeTrue();
		jobs.SubmitCode(job.Id, "code").Should().BeTrue();
		await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await jobs.DisposeAsync();
		jobs.Get(job.Id)!.State.Should().Be("succeeded");
	}

	[Fact]
	public async Task Shutdown_CancelsOutstandingSetupAndWaitsForCleanup()
	{
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var cleaned = false;
		var backend = new Backend(async (_, _, token) =>
		{
			entered.SetResult();
			try { await Task.Delay(Timeout.Infinite, token); }
			finally { cleaned = true; }
			return new("copilot", false, false, "unused", [], []);
		});
		var jobs = new ProviderSetupJobs(backend, Substitute.For<IHostApplicationLifetime>(), NullLogger<ProviderSetupJobs>.Instance);
		var job = jobs.Start("copilot", new("install"));
		await entered.Task;
		await jobs.DisposeAsync();
		cleaned.Should().BeTrue();
		jobs.Get(job.Id)!.State.Should().Be("cancelled");
	}

	[Fact]
	public async Task Failure_DoesNotExposeSubmittedCredentials()
	{
		var backend = new Backend((_, _, _) => throw new InvalidOperationException("Bad key: confidential"));
		var jobs = new ProviderSetupJobs(backend, Substitute.For<IHostApplicationLifetime>(), NullLogger<ProviderSetupJobs>.Instance);
		var job = jobs.Start("opencode", new("login", Key: "confidential"));
		await jobs.DisposeAsync();
		jobs.Get(job.Id)!.Progress.Message.Should().NotContain("confidential");
	}

	private sealed class Backend(Func<Action<SetupProgress>, Func<CancellationToken, Task<string>>, CancellationToken, Task<ProviderReadiness>> run) : IProviderSetupBackend
	{
		public ProviderReadiness[] Inspect() => [];
		public Task<ProviderReadiness> ExecuteAsync(string provider, ProviderOperation operation, Action<SetupProgress> progress, Func<CancellationToken, Task<string>> readCode, CancellationToken token) => run(progress, readCode, token);
	}
}
