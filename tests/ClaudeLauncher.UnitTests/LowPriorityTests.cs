using ClaudeLauncher.Infrastructure.Diagnostics;

namespace ClaudeLauncher.UnitTests;

/// <summary>
/// Background passes run on a thread of their own in Windows' background mode, so gigabytes read
/// in the background never make the window wait for the disk. Windows refuses to enter the mode
/// twice, which is how these tests see that it was entered.
/// </summary>
public sealed class LowPriorityTests
{
    [Fact]
    public async Task WorkRunsOnItsOwnLowThreadInBackgroundMode()
    {
        var (value, thread, poolThread, priority, enteredAgain) = await LowPriority.Run(() =>
        {
            var again = LowPriority.EnterThread();          // already in the mode: refused
            return (42, Thread.CurrentThread.Name, Thread.CurrentThread.IsThreadPoolThread,
                Thread.CurrentThread.Priority, again);
        }, "test pass");

        Assert.Equal(42, value);
        Assert.Equal("test pass", thread);
        Assert.False(poolThread);
        Assert.Equal(ThreadPriority.BelowNormal, priority);
        Assert.False(enteredAgain);
    }

    [Fact]
    public async Task LeavingTheModeLetsItBeEnteredAgain()
    {
        var entered = await LowPriority.Run(() =>
        {
            LowPriority.LeaveThread();
            var again = LowPriority.EnterThread();
            LowPriority.LeaveThread();
            return again;
        });
        Assert.True(entered);
    }

    [Fact]
    public async Task CancellingCancelsAndAFailureFails()
    {
        var cancelled = LowPriority.Run<int>(() => throw new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.True(cancelled.IsCanceled);

        var failed = LowPriority.Run<int>(() => throw new InvalidOperationException("坏了"));
        Assert.Equal("坏了", (await Assert.ThrowsAsync<InvalidOperationException>(() => failed)).Message);

        var ran = false;
        await LowPriority.Run(() => { ran = true; });
        Assert.True(ran);
    }

    [Fact]
    public void AProcessEntersTheModeOnceAndLeavesIt()
    {
        Assert.True(LowPriority.EnterProcess());
        try
        {
            Assert.False(LowPriority.EnterProcess());
        }
        finally
        {
            LowPriority.LeaveProcess();
        }
        Assert.True(LowPriority.EnterProcess());
        LowPriority.LeaveProcess();
    }
}
