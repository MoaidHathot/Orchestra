using FluentAssertions;

namespace Orchestra.Engine.Tests.Executor;

public class RetryPolicyTests
{
	[Fact]
	public void GetDelay_FirstAttempt_ReturnsBaseBackoff()
	{
		// Arrange
		var policy = new RetryPolicy
		{
			BackoffSeconds = 2.0,
			BackoffMultiplier = 3.0,
		};

		// Act
		var delay = policy.GetDelay(1);

		// Assert
		delay.Should().Be(TimeSpan.FromSeconds(2.0));
	}

	[Fact]
	public void GetDelay_SecondAttempt_ReturnsBaseTimesMultiplier()
	{
		// Arrange
		var policy = new RetryPolicy
		{
			BackoffSeconds = 1.0,
			BackoffMultiplier = 2.0,
		};

		// Act
		var delay = policy.GetDelay(2);

		// Assert — 1.0 * 2^(2-1) = 2.0
		delay.Should().Be(TimeSpan.FromSeconds(2.0));
	}

	[Fact]
	public void GetDelay_ThirdAttempt_ReturnsExponentialBackoff()
	{
		// Arrange
		var policy = new RetryPolicy
		{
			BackoffSeconds = 1.0,
			BackoffMultiplier = 2.0,
		};

		// Act
		var delay = policy.GetDelay(3);

		// Assert — 1.0 * 2^(3-1) = 4.0
		delay.Should().Be(TimeSpan.FromSeconds(4.0));
	}

	[Fact]
	public void GetDelay_WithMultiplierOfOne_ReturnsConstantDelay()
	{
		// Arrange — no exponential growth, linear constant delay
		var policy = new RetryPolicy
		{
			BackoffSeconds = 5.0,
			BackoffMultiplier = 1.0,
		};

		// Act & Assert — all attempts should return 5.0
		policy.GetDelay(1).Should().Be(TimeSpan.FromSeconds(5.0));
		policy.GetDelay(2).Should().Be(TimeSpan.FromSeconds(5.0));
		policy.GetDelay(3).Should().Be(TimeSpan.FromSeconds(5.0));
	}

	[Fact]
	public void GetDelay_ZeroOrNegativeAttempt_ReturnsBaseBackoff()
	{
		// Arrange
		var policy = new RetryPolicy
		{
			BackoffSeconds = 3.0,
			BackoffMultiplier = 2.0,
		};

		// Act & Assert — attempt <= 1 returns base
		policy.GetDelay(0).Should().Be(TimeSpan.FromSeconds(3.0));
		policy.GetDelay(-1).Should().Be(TimeSpan.FromSeconds(3.0));
	}

	[Fact]
	public void GetDelay_DefaultValues_ProducesExpectedSequence()
	{
		// Arrange — defaults: backoff=1.0, multiplier=2.0
		var policy = new RetryPolicy();

		// Act & Assert
		policy.GetDelay(1).Should().Be(TimeSpan.FromSeconds(1.0));  // 1 * 2^0 = 1
		policy.GetDelay(2).Should().Be(TimeSpan.FromSeconds(2.0));  // 1 * 2^1 = 2
		policy.GetDelay(3).Should().Be(TimeSpan.FromSeconds(4.0));  // 1 * 2^2 = 4
		policy.GetDelay(4).Should().Be(TimeSpan.FromSeconds(8.0));  // 1 * 2^3 = 8
		policy.GetDelay(5).Should().Be(TimeSpan.FromSeconds(16.0)); // 1 * 2^4 = 16
	}

	[Fact]
	public void GetDelay_FractionalBackoff_ReturnsFractionalDelay()
	{
		// Arrange
		var policy = new RetryPolicy
		{
			BackoffSeconds = 0.5,
			BackoffMultiplier = 2.0,
		};

		// Act
		var delay1 = policy.GetDelay(1);
		var delay2 = policy.GetDelay(2);

		// Assert
		delay1.Should().Be(TimeSpan.FromSeconds(0.5));
		delay2.Should().Be(TimeSpan.FromSeconds(1.0));
	}

	[Fact]
	public void DefaultValues_AreCorrect()
	{
		// Arrange & Act
		var policy = new RetryPolicy();

		// Assert
		policy.MaxRetries.Should().Be(3);
		policy.BackoffSeconds.Should().Be(1.0);
		policy.BackoffMultiplier.Should().Be(2.0);
		policy.RetryOnTimeout.Should().BeTrue();
	}
}
