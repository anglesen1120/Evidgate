using System.Diagnostics;
using Evidgate.Testing;
using Shouldly;
using Xunit;

#pragma warning disable VSTHRD003 // the awaited tasks are started by these tests with Task.Run

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
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var callers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            ContainerStart.WithHostLockAsync(LockPath, Generous, async () =>
            {
                InterlockedMax(ref maxInside, Interlocked.Increment(ref inside));
                await release.Task;
                Interlocked.Decrement(ref inside);
                Interlocked.Increment(ref done);
            }, Record(log), CancellationToken.None), TestContext.Current.CancellationToken)).ToArray();

        // The first holder blocks on the gate while the other seven poll the lock: several poll rounds must
        // pass with exactly one action inside, or the lock does not exclude.
        await Task.Delay(1200, TestContext.Current.CancellationToken);
        Volatile.Read(ref inside).ShouldBe(1);

        release.SetResult();
        await Task.WhenAll(callers);

        maxInside.ShouldBe(1);
        done.ShouldBe(8);
        log.ShouldBeEmpty();
    }

    [Fact]
    public async Task SecondProcess_WaitsForTheProcessHoldingTheFile()
    {
        using var holder = StartPythonHolder(LockPath);
        try
        {
            await WaitForLockedAsync(holder);

            var log = new List<string>();
            var ran = false;
            var waiting = Task.Run(() => ContainerStart.WithHostLockAsync(LockPath, Generous, () =>
            {
                ran = true;
                return Task.CompletedTask;
            }, Record(log), CancellationToken.None), TestContext.Current.CancellationToken);

            await Task.Delay(1000, TestContext.Current.CancellationToken);
            ran.ShouldBeFalse();
            waiting.IsCompleted.ShouldBeFalse();

            holder.StandardInput.Close();
            await waiting.WaitAsync(Generous, TestContext.Current.CancellationToken);

            ran.ShouldBeTrue();
            log.ShouldBeEmpty();
        }
        finally
        {
            KillQuietly(holder);
        }
    }

    [Fact]
    public async Task LockHeldByAKilledProcess_IsReleasedWithIt()
    {
        using var holder = StartPythonHolder(LockPath);
        try
        {
            await WaitForLockedAsync(holder);
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
        finally
        {
            KillQuietly(holder);
        }
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
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var running = Task.Run(() => ContainerStart.WithHostLockAsync(
            LockPath,
            Generous,
            () =>
            {
                entered.SetResult();
                return Task.Delay(Timeout.Infinite, cts.Token);
            },
            Record(log),
            cts.Token), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(Generous, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => running);
        await AssertLockIsFreeAsync();
    }

    [Fact]
    public async Task CallerCancelledWhileWaiting_ThrowsWithoutRunningTheAction()
    {
        var ran = false;
        await using var held = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var cts = new CancellationTokenSource();

        var waiting = Task.Run(() => ContainerStart.WithHostLockAsync(LockPath, Generous, () =>
        {
            ran = true;
            return Task.CompletedTask;
        }, _ => { }, cts.Token), TestContext.Current.CancellationToken);
        await Task.Delay(800, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => waiting);
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

    private static async Task WaitForLockedAsync(Process holder)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        (await holder.StandardOutput.ReadLineAsync(deadline.Token)).ShouldBe("locked");
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    // flock(2) is what FileShare.None takes on Unix, so a python flock holder contends like a second test
    // process. It keeps the lock until its stdin closes, so the test decides when it lets go.
    private static Process StartPythonHolder(string path)
    {
        var info = new ProcessStartInfo("python3")
        {
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(
            "import fcntl,sys\n"
            + "f=open(sys.argv[1],'a+')\n"
            + "fcntl.flock(f,fcntl.LOCK_EX)\n"
            + "print('locked',flush=True)\n"
            + "sys.stdin.read()\n");
        info.ArgumentList.Add(path);
        return Process.Start(info) ?? throw new InvalidOperationException("python3 did not start");
    }
}
