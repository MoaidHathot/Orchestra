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
		policy.MaxDelaySeconds.Should().BeNull("exponential growth is unbounded unless capped");
		policy.JitterSeconds.Should().Be(0, "delays are deterministic unless jitter is requested");
	}

	[Fact]
	public void GetDelay_MaxDelaySeconds_CapsExponentialGrowth()
	{
		var policy = new RetryPolicy { BackoffSeconds = 1.0, BackoffMultiplier = 2.0, MaxDelaySeconds = 5.0 };

		policy.GetDelay(1).Should().Be(TimeSpan.FromSeconds(1));
		policy.GetDelay(3).Should().Be(TimeSpan.FromSeconds(4), "below the cap the curve is untouched");
		policy.GetDelay(4).Should().Be(TimeSpan.FromSeconds(5), "8s is capped to 5s");
		policy.GetDelay(10).Should().Be(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public void GetDelay_Jitter_AddsUpToJitterSeconds_OnTopOfTheCappedDelay()
	{
		var policy = new RetryPolicy { BackoffSeconds = 1.0, BackoffMultiplier = 2.0, MaxDelaySeconds = 4.0, JitterSeconds = 2.0 };

		// Deterministic randomness: NextDouble() in [0,1) scaled by JitterSeconds.
		var zero = new FixedRandom(0.0);
		var half = new FixedRandom(0.5);
		var almostOne = new FixedRandom(0.999);

		policy.GetDelay(2, zero).Should().Be(TimeSpan.FromSeconds(2.0));
		policy.GetDelay(2, half).Should().Be(TimeSpan.FromSeconds(3.0));
		policy.GetDelay(5, almostOne).TotalSeconds.Should().BeApproximately(4.0 + 1.998, 0.001, "capped 4s plus jitter");
	}

	[Fact]
	public void GetDelay_Jitter_IsRandomAcrossCalls_ButBounded()
	{
		var policy = new RetryPolicy { BackoffSeconds = 1.0, JitterSeconds = 1.0 };

		var samples = Enumerable.Range(0, 50).Select(_ => policy.GetDelay(1).TotalSeconds).ToArray();

		samples.Should().OnlyContain(s => s >= 1.0 && s < 2.0);
		samples.Distinct().Should().HaveCountGreaterThan(1, "jitter must actually vary");
	}

	[Fact]
	public void GetDelay_WithoutJitter_IsDeterministic()
	{
		var policy = new RetryPolicy { BackoffSeconds = 1.5 };

		Enumerable.Range(0, 10).Select(_ => policy.GetDelay(2)).Distinct().Should().ContainSingle();
	}

	/// <summary>A <see cref="Random"/> whose <see cref="Random.NextDouble"/> always returns one value.</summary>
	private sealed class FixedRandom(double value) : Random
	{
		protected override double Sample() => value;
		public override double NextDouble() => value;
	}
}
