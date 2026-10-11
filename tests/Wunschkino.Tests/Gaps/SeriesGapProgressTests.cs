using Microsoft.Extensions.Time.Testing;
using Wunschkino.Gaps;

namespace Wunschkino.Tests.Gaps;

public class SeriesGapProgressTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly SeriesGapProgress _progress;

    public SeriesGapProgressTests() => _progress = new SeriesGapProgress(_time);

    [Fact]
    public void Idle_ByDefault()
    {
        var snapshot = _progress.Snapshot;

        Assert.False(snapshot.Busy);
        Assert.False(snapshot.ScanRequested);
        Assert.False(snapshot.Running);
    }

    [Fact]
    public void RequestingAFullScan_IsBusyUntilARunPicksItUp()
    {
        _progress.RequestFullScan();

        Assert.True(_progress.Snapshot.ScanRequested);
        Assert.True(_progress.Snapshot.Busy);
    }

    [Fact]
    public void BeginRun_ConsumesTheRequest_AndReportsItWasAFullScan()
    {
        _progress.RequestFullScan();

        var fullScan = _progress.BeginRun();

        Assert.True(fullScan);
        var snapshot = _progress.Snapshot;
        Assert.False(snapshot.ScanRequested);
        Assert.True(snapshot.Running);
        Assert.True(snapshot.Busy);
    }

    [Fact]
    public void BeginRun_WithoutARequest_IsNotAFullScan()
    {
        Assert.False(_progress.BeginRun());
        Assert.True(_progress.Snapshot.Running);
    }

    [Fact]
    public void Run_ReportsTotalAndDone_ThenIsIdleAgain()
    {
        _progress.BeginRun();
        _progress.SetTotal(3);
        _progress.Advance();
        _progress.Advance();

        var during = _progress.Snapshot;
        _progress.Finish();
        var after = _progress.Snapshot;

        Assert.Equal((3, 2), (during.Total, during.Done));
        Assert.False(after.Running);
        Assert.False(after.Busy);
    }

    [Fact]
    public void ABusyRunNeverLooksIdle_BetweenTheRequestAndTheStart()
    {
        _progress.RequestFullScan();
        Assert.True(_progress.Snapshot.Busy);

        _progress.BeginRun();

        Assert.True(_progress.Snapshot.Busy);
    }

    [Fact]
    public void ARequestNobodyPicksUp_ExpiresAfterTwoMinutes_AndDoesNotRunLater()
    {
        _progress.RequestFullScan();
        _time.Advance(SeriesGapProgress.RequestLifetime + TimeSpan.FromSeconds(1));

        Assert.False(_progress.Snapshot.Busy);
        Assert.False(_progress.BeginRun());
    }

    [Fact]
    public void ARequestJustBeforeExpiry_StillCounts()
    {
        _progress.RequestFullScan();
        _time.Advance(SeriesGapProgress.RequestLifetime - TimeSpan.FromSeconds(1));

        Assert.True(_progress.BeginRun());
    }

    [Fact]
    public void ANewRun_StartsCountingFromZero()
    {
        _progress.BeginRun();
        _progress.SetTotal(5);
        _progress.Advance();
        _progress.Finish();

        _progress.BeginRun();

        var snapshot = _progress.Snapshot;
        Assert.Equal((0, 0), (snapshot.Total, snapshot.Done));
    }
}
