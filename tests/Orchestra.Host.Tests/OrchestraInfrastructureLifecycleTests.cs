using FluentAssertions;
using Orchestra.Host.Hosting;
using Xunit;

namespace Orchestra.Host.Tests;

public class OrchestraInfrastructureLifecycleTests
{
	[Fact]
	public async Task InitializeAsync_WaitsForServicesBeforeConnectingMcps()
	{
		var servicesStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var mcpsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseInitialization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var servicesReady = false;
		var mcpsStartedBeforeServicesReady = false;

		var initialization = OrchestraInfrastructureLifecycle.InitializeAsync(
			async ct =>
			{
				servicesStarted.SetResult();
				await releaseInitialization.Task.WaitAsync(ct);
				servicesReady = true;
			},
			_ =>
			{
				mcpsStartedBeforeServicesReady = !servicesReady;
				mcpsStarted.SetResult();
				return Task.CompletedTask;
			},
			_ => Task.CompletedTask,
			_ => Task.CompletedTask);

		await servicesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		// Keep readiness pending long enough to expose a prematurely scheduled MCP initializer.
		await Task.WhenAny(mcpsStarted.Task, Task.Delay(TimeSpan.FromMilliseconds(100)));
		releaseInitialization.SetResult();
		await initialization;
		mcpsStarted.Task.IsCompletedSuccessfully.Should().BeTrue();
		mcpsStartedBeforeServicesReady.Should().BeFalse("MCP endpoints may be hosted by those services");
	}

	[Fact]
	public async Task StopAsync_StopsServicesAndMcpsConcurrently()
	{
		var servicesStopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var mcpsStopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		var stop = OrchestraInfrastructureLifecycle.StopAsync(
			async ct =>
			{
				servicesStopStarted.SetResult();
				await mcpsStopStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
				await releaseStop.Task.WaitAsync(ct);
			},
			async ct =>
			{
				mcpsStopStarted.SetResult();
				await servicesStopStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
				await releaseStop.Task.WaitAsync(ct);
			});

		await Task.WhenAll(servicesStopStarted.Task, mcpsStopStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
		stop.IsCompleted.Should().BeFalse("both stop operations should be running at the same time");

		releaseStop.SetResult();
		await stop;
	}

	[Fact]
	public async Task InitializeAsync_WhenMcpValidationFails_StopsBothAndPreservesFailure()
	{
		var servicesReady = false;
		var servicesStopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var mcpsStopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		var initialization = OrchestraInfrastructureLifecycle.InitializeAsync(
			_ =>
			{
				servicesReady = true;
				return Task.CompletedTask;
			},
			_ => Task.FromException(new InvalidOperationException("MCP startup failed")),
			async ct =>
			{
				servicesStopStarted.SetResult();
				await mcpsStopStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
				await releaseStop.Task.WaitAsync(ct);
			},
			async ct =>
			{
				mcpsStopStarted.SetResult();
				await servicesStopStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
				await releaseStop.Task.WaitAsync(ct);
			});

		await Task.WhenAll(servicesStopStarted.Task, mcpsStopStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));

		releaseStop.SetResult();
		var act = async () => await initialization;
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("MCP startup failed");
		servicesReady.Should().BeTrue();
	}

	[Fact]
	public async Task InitializeAsync_WhenRequiredServiceFails_SkipsMcpsAndPreservesFailureDuringCleanup()
	{
		var mcpsStarted = false;
		var mcpsStopped = false;
		var initialize = () => OrchestraInfrastructureLifecycle.InitializeAsync(
			_ => Task.FromException(new InvalidOperationException("Required service failed readiness")),
			_ =>
			{
				mcpsStarted = true;
				return Task.CompletedTask;
			},
			_ => throw new InvalidOperationException("Cleanup failed"),
			_ =>
			{
				mcpsStopped = true;
				return Task.CompletedTask;
			});

		await initialize.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("Required service failed readiness");
		mcpsStarted.Should().BeFalse();
		mcpsStopped.Should().BeTrue("all cleanup must be attempted even if another cleanup fails");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task InitializeAsync_CancelledWhileWaitingForServices_SkipsMcpsAndCleansUp(bool swallowCancellation)
	{
		using var cancellation = new CancellationTokenSource();
		var servicesStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var mcpsStarted = false;
		var stopCount = 0;
		Task Stop(CancellationToken token)
		{
			token.IsCancellationRequested.Should().BeFalse("cleanup must survive startup cancellation");
			Interlocked.Increment(ref stopCount);
			return Task.CompletedTask;
		}

		var initialization = OrchestraInfrastructureLifecycle.InitializeAsync(
			async ct =>
			{
				servicesStarted.SetResult();
				try
				{
					await Task.Delay(Timeout.InfiniteTimeSpan, ct);
				}
				catch (OperationCanceledException) when (swallowCancellation) { }
			},
			_ =>
			{
				mcpsStarted = true;
				return Task.CompletedTask;
			},
			Stop,
			Stop,
			cancellation.Token);

		await servicesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		cancellation.Cancel();
		var initialize = () => initialization;
		await initialize.Should().ThrowAsync<OperationCanceledException>();
		mcpsStarted.Should().BeFalse();
		stopCount.Should().Be(2);
	}
}
