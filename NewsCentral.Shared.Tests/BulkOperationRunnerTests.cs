using NewsCentral.Ui;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class BulkOperationRunnerTests
{
    [Fact]
    public async Task RunAsync_AllSucceed_ReportsSucceededCountAndNotStopped()
    {
        var items = new[] { 1, 2, 3 };

        var result = await BulkOperationRunner.RunAsync(
            items,
            _ => Task.FromResult(BulkItemOutcome.Success()),
            progress: null,
            CancellationToken.None);

        Assert.Equal(3, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);
        Assert.False(result.Stopped);
    }

    [Fact]
    public async Task RunAsync_OneItemThrows_IsRecordedAsFailedAndTheRestContinue()
    {
        var items = new[] { 1, 2, 3 };

        var result = await BulkOperationRunner.RunAsync(
            items,
            item => item == 2
                ? throw new InvalidOperationException("boom")
                : Task.FromResult(BulkItemOutcome.Success()),
            progress: null,
            CancellationToken.None);

        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(3, result.Outcomes.Count);
        Assert.Equal("boom", result.Outcomes[1].Outcome.Error);
    }

    [Fact]
    public async Task RunAsync_OneItemReturnsAsyncFaultedTask_IsAlsoRecordedAsFailed()
    {
        var items = new[] { 1, 2 };

        var result = await BulkOperationRunner.RunAsync(
            items,
            item => item == 1
                ? Task.FromException<BulkItemOutcome>(new InvalidOperationException("async boom"))
                : Task.FromResult(BulkItemOutcome.Success()),
            progress: null,
            CancellationToken.None);

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal("async boom", result.Outcomes[0].Outcome.Error);
    }

    [Fact]
    public async Task RunAsync_StopRequestedAfterNItems_ProcessesExactlyNAndReportsStopped()
    {
        var items = Enumerable.Range(1, 10).ToList();
        using var cts = new CancellationTokenSource();
        var processed = 0;

        var result = await BulkOperationRunner.RunAsync(
            items,
            _ =>
            {
                processed++;
                if (processed == 3)
                    cts.Cancel(); // requested AFTER this item completes — checked before the NEXT one starts
                return Task.FromResult(BulkItemOutcome.Success());
            },
            progress: null,
            cts.Token);

        Assert.Equal(3, result.Outcomes.Count);
        Assert.True(result.Stopped);
    }

    [Fact]
    public async Task RunAsync_NeverCancelsAnItemAlreadyInProgress()
    {
        // Stop is requested WHILE the first item's action is still running — it must still
        // complete and be recorded, since the token is only checked BETWEEN items.
        using var cts = new CancellationTokenSource();
        var items = new[] { 1, 2, 3 };

        var result = await BulkOperationRunner.RunAsync(
            items,
            async item =>
            {
                if (item == 1)
                {
                    cts.Cancel();
                    await Task.Delay(10);
                }
                return BulkItemOutcome.Success();
            },
            progress: null,
            cts.Token);

        Assert.Single(result.Outcomes);
        Assert.True(result.Outcomes[0].Outcome.Succeeded);
        Assert.True(result.Stopped);
    }

    [Fact]
    public async Task RunAsync_ReportsProgressOnceAfterEachProcessedItem()
    {
        var items = new[] { "a", "b", "c" };
        var reports = new List<(int done, int total)>();
        var progress = new Progress<(int done, int total)>(reports.Add);

        await BulkOperationRunner.RunAsync(
            items,
            _ => Task.FromResult(BulkItemOutcome.Success()),
            progress,
            CancellationToken.None);

        // Progress<T> marshals its callback via the captured SynchronizationContext, which may be
        // asynchronous — give it a moment to flush before asserting.
        await Task.Delay(50);

        Assert.Equal(3, reports.Count);
        Assert.Equal((1, 3), reports[0]);
        Assert.Equal((2, 3), reports[1]);
        Assert.Equal((3, 3), reports[2]);
    }

    [Fact]
    public async Task RunAsync_CountsWarnings_OnlyOnSucceededOutcomes()
    {
        var items = new[] { 1, 2, 3 };

        var result = await BulkOperationRunner.RunAsync(
            items,
            item => Task.FromResult(item switch
            {
                1 => BulkItemOutcome.Success("warned"),
                2 => BulkItemOutcome.Success(),
                _ => BulkItemOutcome.Failure("failed")
            }),
            progress: null,
            CancellationToken.None);

        Assert.Equal(1, result.WarningCount);
        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
    }
}
