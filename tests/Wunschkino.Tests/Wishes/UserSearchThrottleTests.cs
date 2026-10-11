using Microsoft.Extensions.Time.Testing;
using Wunschkino.Wishes;

namespace Wunschkino.Tests.Wishes;

public class UserSearchThrottleTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
    private readonly UserSearchThrottle _throttle;

    public UserSearchThrottleTests() => _throttle = new UserSearchThrottle(_time);

    [Fact]
    public void AllowsUpToTheLimit_ThenAsksToWait()
    {
        for (var i = 0; i < 3; i++)
            Assert.Null(_throttle.TryAcquire(1, 3));

        var wait = _throttle.TryAcquire(1, 3);

        Assert.NotNull(wait);
        Assert.InRange(wait.Value.TotalSeconds, 59, 60);
    }

    [Fact]
    public void TheWaitShrinks_AsTheWindowPasses()
    {
        _throttle.TryAcquire(1, 1);
        _time.Advance(TimeSpan.FromSeconds(45));

        var wait = _throttle.TryAcquire(1, 1);

        Assert.InRange(wait!.Value.TotalSeconds, 14, 15);
    }

    [Fact]
    public void AllowsAgain_AfterAMinute()
    {
        for (var i = 0; i < 2; i++)
            _throttle.TryAcquire(1, 2);
        Assert.NotNull(_throttle.TryAcquire(1, 2));

        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.Null(_throttle.TryAcquire(1, 2));
    }

    [Fact]
    public void CountsEachUserSeparately()
    {
        _throttle.TryAcquire(1, 1);

        Assert.NotNull(_throttle.TryAcquire(1, 1));
        Assert.Null(_throttle.TryAcquire(2, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ALimitOfZeroOrLess_MeansNoLimit(int limit)
    {
        for (var i = 0; i < 500; i++)
            Assert.Null(_throttle.TryAcquire(1, limit));

        Assert.Equal(0, _throttle.TrackedUsers);
    }

    [Fact]
    public void ALoweredLimit_AppliesToTheAlreadyCountedRequests()
    {
        for (var i = 0; i < 5; i++)
            _throttle.TryAcquire(1, 10);

        Assert.NotNull(_throttle.TryAcquire(1, 3));
    }

    [Fact]
    public void ExpiredUsers_AreSweptOnceManyAreTracked()
    {
        for (var user = 0; user < 150; user++)
            _throttle.TryAcquire(user, 5);
        _time.Advance(TimeSpan.FromMinutes(2));

        _throttle.TryAcquire(9999, 5);

        Assert.True(_throttle.TrackedUsers < 20, $"Abgelaufene Zähler müssen aufgeräumt werden, es sind noch {_throttle.TrackedUsers}.");
    }

    [Fact]
    public void ParallelRequests_NeverExceedTheLimit()
    {
        var allowed = 0;
        Parallel.For(0, 200, _ =>
        {
            if (_throttle.TryAcquire(1, 10) is null)
                Interlocked.Increment(ref allowed);
        });

        Assert.Equal(10, allowed);
    }
}
