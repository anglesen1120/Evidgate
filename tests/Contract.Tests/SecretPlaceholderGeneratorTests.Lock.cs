using System.Diagnostics;
using System.Globalization;
using Shouldly;
using Xunit;

namespace Contract.Tests;

// Stale-lock takeover: only one run may win, and a lock that cannot be proven dead is never taken.
public sealed partial class SecretPlaceholderGeneratorTests
{
    private string LockDir => EnvFile + ".lock";

    [Theory]
    [InlineData("")]
    [InlineData("not-a-pid")]
    [InlineData("0")]
    [InlineData("-7")]
    [InlineData("12.5")]
    public async Task LockWithAnUnusablePid_IsTreatedAsHeld_AndIsNotRemoved(string pidFile)
    {
        Directory.CreateDirectory(LockDir);
        await File.WriteAllTextAsync(Path.Combine(LockDir, "pid"), pidFile, TestContext.Current.CancellationToken);

        var run = await RunAsync("dev");

        run.ExitCode.ShouldNotBe(0);
        run.Output.ShouldContain("another run is in progress");
        Directory.Exists(LockDir).ShouldBeTrue();
        File.Exists(EnvFile).ShouldBeFalse();
    }

    [Fact]
    public async Task FreshLockTakenWhileAHolderIsJudgedDead_IsNeverRemoved()
    {
        // The hook plays a rival: right after the run judges the holder dead, the stale lock is
        // replaced by a fresh one owned by a live process (this test process).
        var deadPid = await DeadPidAsync();
        Directory.CreateDirectory(LockDir);
        await File.WriteAllTextAsync(Path.Combine(LockDir, "pid"), deadPid.ToString(CultureInfo.InvariantCulture), TestContext.Current.CancellationToken);
        var hook = Path.Combine(_root, "rival-hook.mjs");
        await File.WriteAllTextAsync(hook, $$"""
            import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
            const realKill = process.kill.bind(process);
            let swapped = false;
            process.kill = (pid, signal) => {
              if (pid === {{deadPid}} && signal === 0 && !swapped) {
                swapped = true;
                rmSync({{System.Text.Json.JsonSerializer.Serialize(LockDir)}}, { recursive: true, force: true });
                mkdirSync({{System.Text.Json.JsonSerializer.Serialize(LockDir)}});
                writeFileSync({{System.Text.Json.JsonSerializer.Serialize(Path.Combine(LockDir, "pid"))}}, '{{Environment.ProcessId}}');
                const error = new Error('no such process');
                error.code = 'ESRCH';
                throw error;
              }
              return realKill(pid, signal);
            };
            """, TestContext.Current.CancellationToken);

        var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "--import", new Uri(hook).AbsoluteUri, Script, "--env", "dev", "--root", _root })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken)
            + await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        process.ExitCode.ShouldNotBe(0, output);
        output.ShouldContain("another run is in progress");
        File.Exists(EnvFile).ShouldBeFalse();
        (await File.ReadAllTextAsync(Path.Combine(LockDir, "pid"), TestContext.Current.CancellationToken))
            .ShouldBe(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task LockWithoutAPidFile_IsTreatedAsHeld()
    {
        Directory.CreateDirectory(LockDir);

        var run = await RunAsync("dev");

        run.ExitCode.ShouldNotBe(0);
        Directory.Exists(LockDir).ShouldBeTrue();
        File.Exists(EnvFile).ShouldBeFalse();
    }

    [Fact]
    public async Task ConcurrentTakeoversOfOneStaleLock_LetExactlyOneRunGenerate()
    {
        var deadPid = await DeadPidAsync();
        for (var round = 0; round < 4; round++)
        {
            File.Delete(EnvFile);
            Directory.CreateDirectory(LockDir);
            await File.WriteAllTextAsync(Path.Combine(LockDir, "pid"), deadPid.ToString(CultureInfo.InvariantCulture), TestContext.Current.CancellationToken);

            var runs = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => RunAsync("dev")));

            runs.Count(run => run.Output.Contains("created ", StringComparison.Ordinal))
                .ShouldBe(1, $"round {round}: {string.Join(" | ", runs.Select(run => run.Output.Trim()))}");
            foreach (var run in runs.Where(run => run.ExitCode != 0))
            {
                run.Output.ShouldContain("another run is in progress");
            }

            File.Exists(EnvFile).ShouldBeTrue();
            Directory.Exists(LockDir).ShouldBeFalse();
            Directory.Exists(LockDir + ".takeover").ShouldBeFalse();
        }
    }

    [Fact]
    public async Task LockHeldByAKilledGenerator_IsTakenOverByTheNextRun()
    {
        // A FIFO in place of the template parks the generator inside the lock until it is killed.
        var template = Path.Combine(_root, ".env.dev.template");
        var saved = template + ".saved";
        File.Move(template, saved);
        (await RunShellAsync($"mkfifo '{template}'")).ShouldBeEmpty();

        var start = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { Script, "--env", "dev", "--root", _root })
        {
            start.ArgumentList.Add(argument);
        }

        using var holder = Process.Start(start)!;
        try
        {
            var pidFile = Path.Combine(LockDir, "pid");
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!File.Exists(pidFile) || new FileInfo(pidFile).Length == 0)
            {
                DateTime.UtcNow.ShouldBeLessThan(deadline, "the generator never took its lock");
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            holder.Kill();
            await holder.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            if (!holder.HasExited)
            {
                holder.Kill();
            }
        }

        Directory.Exists(LockDir).ShouldBeTrue("SIGKILL leaves the lock behind");
        File.Delete(template);
        File.Move(saved, template);

        var run = await RunAsync("dev");

        run.ExitCode.ShouldBe(0, run.Output);
        File.Exists(EnvFile).ShouldBeTrue();
        Directory.Exists(LockDir).ShouldBeFalse();
    }

    private async Task<string> StaleLockWithGateAsync(string? gatePid, TimeSpan gateAge)
    {
        var deadPid = await DeadPidAsync();
        Directory.CreateDirectory(LockDir);
        await File.WriteAllTextAsync(Path.Combine(LockDir, "pid"), deadPid.ToString(CultureInfo.InvariantCulture), TestContext.Current.CancellationToken);
        var gate = LockDir + ".takeover";
        Directory.CreateDirectory(gate);
        if (gatePid is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(gate, "pid"), gatePid, TestContext.Current.CancellationToken);
        }

        Directory.SetLastWriteTimeUtc(gate, DateTime.UtcNow - gateAge);
        return gate;
    }

    [Theory]
    [InlineData("dead")]
    [InlineData("live")]
    [InlineData("")]
    public async Task TakeoverGate_IsNeverReclaimed_WhateverItsOwnerOrAge(string owner)
    {
        var gatePid = owner switch
        {
            "dead" => (await DeadPidAsync()).ToString(CultureInfo.InvariantCulture),
            "live" => Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
        var gate = await StaleLockWithGateAsync(gatePid, TimeSpan.FromMinutes(5));

        var run = await RunAsync("dev");

        run.ExitCode.ShouldNotBe(0);
        run.Output.ShouldContain("another run is in progress");
        Directory.Exists(gate).ShouldBeTrue("a gate held by anyone is left to its owner or the operator");
        Directory.Exists(LockDir).ShouldBeTrue("the lock judged dead stays until the gate owner decides");
        File.Exists(EnvFile).ShouldBeFalse();
    }
}
