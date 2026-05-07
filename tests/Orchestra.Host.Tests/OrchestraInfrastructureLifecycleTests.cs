using FluentAssertions;
using Orchestra.Host.Hosting;
using Xunit;

namespace Orchestra.Host.Tests;

public class OrchestraInfrastructureLifecycleTests
{
	[Fact]
	public async Task InitializeAsync_StartsServicesAndMcpsConcurrently()
	{
		var servicesStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var mcpsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseInitialization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		var initialization = OrchestraInfrastructureLifecycle.InitializeAsync(
			async ct =>
			{
				servicesStarted.SetResult();
				await releaseInitialization.Task.WaitAsync(ct);
			},
			async ct =>
			{
				mcpsStarted.SetResult();
				await releaseInitialization.Task.WaitAsync(ct);
			},
			_ => Task.CompletedTask,
			_ => Task.CompletedTask);

		await Task.WhenAll(servicesStarted.Task, mcpsStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
		initialization.IsCompleted.Should().BeFalse("both initializers should be running at the same time");

		releaseInitialization.SetResult();
		await initialization;
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
	public async Task InitializeAsync_WhenOneInitializerFails_CancelsOtherAndStopsBoth()
	{
		var servicesCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var servicesStopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var mcpsStopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		var initialization = OrchestraInfrastructureLifecycle.InitializeAsync(
			async ct =>
			{
				try
				{
					await Task.Delay(Timeout.InfiniteTimeSpan, ct);
				}
				catch (OperationCanceledException)
				{
					servicesCancelled.SetResult();
					throw;
				}
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

		await servicesCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await Task.WhenAll(servicesStopStarted.Task, mcpsStopStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));

		releaseStop.SetResult();
		var act = async () => await initialization;
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("MCP startup failed");
	}
}
