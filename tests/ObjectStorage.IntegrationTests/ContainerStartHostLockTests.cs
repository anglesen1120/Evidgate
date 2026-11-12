using System.Diagnostics;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace ObjectStorage.IntegrationTests;

/// <summary>Contention tests of the host-wide file lock around the Ryuk reaper start; no Docker needed.</summary>
public sealed class ContainerStartHostLockTests : IDisposable
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private readonly string _dir = Directory.CreateTempSubdirectory("evidgate-hostlock-").FullName;

    private string LockPath => Path.Combine(_dir, "ryuk.lock");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task ConcurrentCallers_NeverRunTheirActionTogether()
    {
        var inside = 0;
        var maxInside = 0;
        var done = 0;
        var log = new List<string>();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            ContainerStart.WithHostLockAsync(LockPath, Generous, async () =>
            {
                var now = Interlocked.Increment(ref inside);
                InterlockedMax(ref maxInside, now);
                await Task.Delay(40, TestContext.Current.CancellationToken);
                Interlocked.Decrement(ref inside);
                Interlocked.Increment(ref done);
            }, Record(log), CancellationToken.None))));

        maxInside.ShouldBe(1);
        done.ShouldBe(8);
        log.ShouldBeEmpty();
    }

    [Fact]
    public async Task SecondProcess_WaitsForTheProcessHoldingTheFile()
    {
        var hold = TimeSpan.FromSeconds(2);
        using var holder = StartPythonHolder(LockPath, hold);
        (await holder.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken)).ShouldBe("locked");

        var log = new List<string>();
        var ran = false;
        var clock = Stopwatch.StartNew();
        await ContainerStart.WithHostLockAsync(LockPath, Generous, () =>
        {
            ran = true;
            return Task.CompletedTask;
        }, Record(log), CancellationToken.None);
        clock.Stop();

        ran.ShouldBeTrue();
        clock.Elapsed.ShouldBeGreaterThan(TimeSpan.FromSeconds(1));
        log.ShouldBeEmpty();
        await holder.WaitForExitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LockHeldByAKilledProcess_IsReleasedWithIt()
    {
        using var holder = StartPythonHolder(LockPath, TimeSpan.FromMinutes(5));
        (await holder.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken)).ShouldBe("locked");
        holder.Kill();
        await holder.WaitForExitAsync(TestContext.Current.CancellationToken);

        var log = new List<string>();
        var ran = false;
        await ContainerStart.WithHostLockAsync(LockPath, Generous, () =>
        {
            ran = true;
            return Task.CompletedTask;
        }, Record(log), CancellationToken.None);

        ran.ShouldBeTrue();
        log.ShouldBeEmpty();
    }

    [Fact]
    public async Task LockHeldPastTheWait_RunsTheActionWithoutTheLock()
    {
        var log = new List<string>();
        var ran = false;
        await using var held = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        await ContainerStart.WithHostLockAsync(LockPath, TimeSpan.FromMilliseconds(400), () =>
        {
            ran = true;
            return Task.CompletedTask;
        }, Record(log), CancellationToken.None);

        ran.ShouldBeTrue();
        log.ShouldHaveSingleItem().ShouldContain("not taken in time; starting without it");
    }

    [Fact]
    public async Task LockFileThatIsADirectory_FailsOpenAtOnce()
    {
        var log = new List<string>();
        var ran = false;
        var clock = Stopwatch.StartNew();

        await ContainerStart.WithHostLockAsync(_dir, Generous, () =>
        {
            ran = true;
            return Task.CompletedTask;
        }, Record(log), CancellationToken.None);

        ran.ShouldBeTrue();
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        log.ShouldHaveSingleItem().ShouldContain("not accessible; starting without it");
    }

    [Fact]
    public async Task LockFileInAMissingDirectory_FailsOpenAfterTheWait()
    {
        var log = new List<string>();
        var ran = false;

        await ContainerStart.WithHostLockAsync(
            Path.Combine(_dir, "missing", "ryuk.lock"),
            TimeSpan.FromMilliseconds(400),
            () =>
            {
                ran = true;
                return Task.CompletedTask;
            },
            Record(log),
            CancellationToken.None);

        ran.ShouldBeTrue();
        log.ShouldHaveSingleItem().ShouldContain("not taken in time");
    }

    [Fact]
    public async Task ActionThatThrows_ReleasesTheLockAndKeepsTheException()
    {
        var log = new List<string>();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            ContainerStart.WithHostLockAsync(LockPath, Generous, () => throw new InvalidOperationException("boom"), Record(log), CancellationToken.None));

        await AssertLockIsFreeAsync();
    }

    [Fact]
    public async Task ActionCancelled_ReleasesTheLock()
    {
        var log = new List<string>();
        using var cts = new CancellationTokenSource();

        var running = ContainerStart.WithHostLockAsync(
            LockPath, Generous, () => Task.Delay(Timeout.Infinite, cts.Token), Record(log), cts.Token);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

#pragma warning disable VSTHRD003 // the task is started above, in this test's own context
        await Should.ThrowAsync<OperationCanceledException>(() => running);
#pragma warning restore VSTHRD003
        await AssertLockIsFreeAsync();
    }

    [Fact]
    public async Task CallerCancelledWhileWaiting_ThrowsWithoutRunningTheAction()
    {
        var ran = false;
        await using var held = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var cts = new CancellationTokenSource();

        var waiting = ContainerStart.WithHostLockAsync(LockPath, Generous, () =>
        {
            ran = true;
            return Task.CompletedTask;
        }, _ => { }, cts.Token);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

#pragma warning disable VSTHRD003 // the task is started above, in this test's own context
        await Should.ThrowAsync<OperationCanceledException>(() => waiting);
#pragma warning restore VSTHRD003
        ran.ShouldBeFalse();
    }

    private async Task AssertLockIsFreeAsync()
    {
        var log = new List<string>();
        var ran = false;
        await ContainerStart.WithHostLockAsync(LockPath, TimeSpan.FromSeconds(2), () =>
        {
            ran = true;
            return Task.CompletedTask;
        }, Record(log), CancellationToken.None);

        ran.ShouldBeTrue();
        log.ShouldBeEmpty();
    }

    private static Action<string> Record(List<string> log) => message =>
    {
        lock (log)
        {
            log.Add(message);
        }
    };

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    // flock(2) is what FileShare.None takes on Unix, so a python flock holder contends like a second test process.
    private static Process StartPythonHolder(string path, TimeSpan hold)
    {
        var info = new ProcessStartInfo("python3")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(
            "import fcntl,sys,time\n"
            + "f=open(sys.argv[1],'a+')\n"
            + "fcntl.flock(f,fcntl.LOCK_EX)\n"
            + "print('locked',flush=True)\n"
            + "time.sleep(float(sys.argv[2]))\n");
        info.ArgumentList.Add(path);
        info.ArgumentList.Add(hold.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Process.Start(info) ?? throw new InvalidOperationException("python3 did not start");
    }
}
