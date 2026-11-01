using System.Globalization;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Migrator;
using Shouldly;
using Xunit;

namespace Migrator.IntegrationTests;

/// <summary>
/// <c>seed-dev --with-token</c>: the dev ingest token and the sample policy revision are produced by the real commands,
/// the secret is printed once on stdout and nowhere else, the rerun mints nothing, and every refusal writes nothing.
/// </summary>
[Collection(MigratorPostgresSuite.Name)]
[Trait("Category", "Docker")]
public sealed class SeedDevTokenTests
{
    private readonly SeedDevTokenHarness _harness;

    public SeedDevTokenTests(PostgresMigratorFixture fixture) => _harness = new SeedDevTokenHarness(fixture);

    [Fact]
    public async Task SeedDev_Token_PrintedOnce_NotLogged_AndStoredOnlyAsHmac()
    {
        var (database, pepper) = await _harness.SeededDatabaseAsync();

        var run = _harness.RunWithToken(database, pepper);

        run.ExitCode.ShouldBe(MigratorExitCodes.Success, run.Error);

        // Exactly one token line, and it is a real token: the one-time secret never appears twice in the transcript.
        SeedDevTokenHarness.CountOf(run.Output, SeedDevTokenHarness.DevTokenLine).ShouldBe(1);
        var token = SeedDevTokenHarness.TokenOf(run.Output);
        token.ShouldStartWith("egt_");

        // Nothing but the deliberate stdout line carries the secret — not the error writer, not a log.
        run.Error.ShouldBeEmpty();
        run.Output.ShouldContain(DevSeedToken.ShownOnceNote);
        // The scopes line is printed from the minted row, so the print and the stored row cannot disagree.
        run.Output.ShouldContain("(scopes: artifact:write, decision:read, event:write, run:create)");
        run.Error.ShouldNotContain(token);
        run.Error.ShouldNotContain("egt_");

        var (prefix, secret) = SeedDevTokenHarness.Split(token);
        (await _harness.Fixture.ScalarAsync<long>(database, "select count(*) from public.ingest_token")).ShouldBe(1);
        (await _harness.Fixture.ScalarAsync<string>(database, "select name from public.ingest_token")).ShouldBe(DevSeed.TokenName);
        (await _harness.Fixture.ScalarAsync<string>(database, "select prefix from public.ingest_token")).ShouldBe(prefix);
        (await _harness.Fixture.ScalarAsync<string>(database, "select project_id::text from public.ingest_token")).ShouldBe(DevSeed.ProjectId.ToString("D"));
        (await _harness.Fixture.ScalarAsync<string>(
            database,
            "select string_agg(scope, ',' order by scope) from public.ingest_token t, unnest(t.scopes) as scope"))
            .ShouldBe("artifact:write,decision:read,event:write,run:create");
        (await _harness.Fixture.ScalarAsync<long>(database, "select count(*) from public.ingest_token where expires_at is null and revoked_at is null")).ShouldBe(1);

        // Only the HMAC is stored: token_hmac is exactly HMAC-SHA256(pepper, secret), and the secret half appears in no
        // stored text of the row.
        var storedHmac = await _harness.Fixture.ScalarAsync<string>(database, "select encode(token_hmac, 'hex') from public.ingest_token");
        storedHmac.ShouldBe(SeedDevTokenHarness.ExpectedHmac(pepper, secret));
        (await _harness.Fixture.ScalarAsync<string>(
            database,
            "select concat_ws('|', name, prefix, encode(token_hmac, 'hex'), scopes::text) from public.ingest_token"))
            .ShouldNotContain(secret);
    }

    [Fact]
    public void RerunNote_IsTheDecidedRecoveryText()
    {
        // The exact instruction the owner settled on, command included: a rerun has no login to revoke through, so the
        // reset is the only recovery the note may name.
        DevSeedToken.RerunNote.ShouldBe(SeedDevTokenHarness.DecidedRecoveryText);
        DevSeedToken.RerunNote.ShouldNotContain("revoke");
        DevSeedToken.RerunNote.ShouldNotContain("DELETE");
    }

    [Fact]
    public async Task SeedDev_Token_Rerun_PrintsRecoveryAndMintsNothing()
    {
        var (database, pepper) = await _harness.SeededDatabaseAsync();
        _harness.RunWithToken(database, pepper).ExitCode.ShouldBe(MigratorExitCodes.Success);

        var rerun = _harness.RunWithToken(database, pepper);

        rerun.ExitCode.ShouldBe(MigratorExitCodes.Success, rerun.Error);
        rerun.Output.ShouldContain(SeedDevTokenHarness.DecidedRecoveryText);
        SeedDevTokenHarness.CountOf(rerun.Output, SeedDevTokenHarness.DevTokenLine).ShouldBe(0);
        rerun.Error.ShouldBeEmpty();
        (await _harness.Fixture.ScalarAsync<long>(database, "select count(*) from public.ingest_token")).ShouldBe(1);
    }

    [Fact]
    public async Task SeedDev_Token_RevokedToken_RerunMintsANewOne()
    {
        var (database, pepper) = await _harness.SeededDatabaseAsync();
        var first = _harness.RunWithToken(database, pepper);
        first.ExitCode.ShouldBe(MigratorExitCodes.Success, first.Error);
        var revokedPrefix = SeedDevTokenHarness.Split(SeedDevTokenHarness.TokenOf(first.Output)).Prefix;

        await using (var connection = await _harness.Fixture.OpenSuperuserAsync(database))
        {
            await PostgresMigratorFixture.ExecuteAsync(
                connection,
                "update public.ingest_token set revoked_at = now()");
        }

        var rerun = _harness.RunWithToken(database, pepper);

        rerun.ExitCode.ShouldBe(MigratorExitCodes.Success, rerun.Error);
        SeedDevTokenHarness.CountOf(rerun.Output, SeedDevTokenHarness.DevTokenLine).ShouldBe(1);
        rerun.Output.ShouldNotContain(DevSeedToken.RerunNote);
        (await _harness.Fixture.ScalarAsync<long>(database, "select count(*) from public.ingest_token")).ShouldBe(2);
        (await _harness.Fixture.ScalarAsync<long>(
            database,
            "select count(*) from public.ingest_token where revoked_at is null")).ShouldBe(1);
        (await _harness.Fixture.ScalarAsync<string>(
            database,
            "select prefix from public.ingest_token where revoked_at is null")).ShouldNotBe(revokedPrefix);
    }

    /// <summary>
    /// Two first runs started together: the advisory-lock guard lets one mint and makes the other print the rerun note,
    /// so the caller gets exactly one secret line and the project exactly one live token.
    /// </summary>
    [Fact]
    public async Task SeedDev_Token_TwoConcurrentFirstRuns_MintExactlyOne()
    {
        var (database, pepper) = await _harness.SeededDatabaseAsync();
        var variables = _harness.Variables(database, pepper);

        // Threads and a gate, not Task.Run: each run blocks inside the real command, so they need their own threads to
        // overlap, and a task started elsewhere and awaited here is the pattern VSTHRD003 forbids.
        var gate = new ManualResetEventSlim(false);
        var results = new SeedDevTokenHarness.RunResult[2];
        var threads = Enumerable.Range(0, 2)
            .Select(index => new Thread(() =>
            {
                gate.Wait();
                results[index] = SeedDevTokenHarness.Run(["seed-dev", "--with-token"], variables);
            }))
            .ToArray();

        foreach (var thread in threads)
        {
            thread.Start();
        }

        gate.Set();
        foreach (var thread in threads)
        {
            thread.Join();
        }

        results.ShouldAllBe(run => run.ExitCode == MigratorExitCodes.Success);
        SeedDevTokenHarness.CountOf(results[0].Output + results[1].Output, SeedDevTokenHarness.DevTokenLine).ShouldBe(1);
        results.Count(run => run.Output.Contains(DevSeedToken.RerunNote, StringComparison.Ordinal)).ShouldBe(1);
        (await _harness.Fixture.ScalarAsync<long>(database, "select count(*) from public.ingest_token")).ShouldBe(1);
        (await _harness.Fixture.ScalarAsync<long>(
            database,
            "select count(*) from public.ingest_token where revoked_at is null")).ShouldBe(1);
    }

    [Fact]
    public async Task SeedDev_SamplePolicy_SameHashOnRerun()
    {
        var (database, pepper) = await _harness.SeededDatabaseAsync();

        var first = _harness.RunWithToken(database, pepper);
        first.ExitCode.ShouldBe(MigratorExitCodes.Success, first.Error);

        var (firstId, firstHash) = await _harness.PolicyRevisionAsync(database);
        first.Output.ShouldContain($"seed-dev: sample policy revision 1 {firstHash} (created)");

        var rerun = _harness.RunWithToken(database, pepper);
        rerun.ExitCode.ShouldBe(MigratorExitCodes.Success, rerun.Error);

        var (rerunId, rerunHash) = await _harness.PolicyRevisionAsync(database);

        // One revision, same row, same content address: the replay path of the real command, not a second append.
        rerunHash.ShouldBe(firstHash);
        rerunId.ShouldBe(firstId);
        rerun.Output.ShouldContain($"seed-dev: sample policy revision 1 {firstHash} (already present)");
        (await _harness.Fixture.ScalarAsync<long>(database, "select count(*) from public.policy_revision")).ShouldBe(1);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData(null)]
    public async Task SeedDev_WithToken_OutsideDevelopment_Refuses_AndWritesNothing(string? environment)
    {
        var (database, pepper) = await _harness.SeededDatabaseAsync();

        var run = _harness.RunWithToken(database, pepper, environment);

        run.ExitCode.ShouldBe(MigratorExitCodes.InvalidInvocation);
        run.Error.ShouldContain("refused");
        run.Output.ShouldBeEmpty();
        await _harness.AssertNothingWrittenAsync(database);
    }

    [Fact]
    public async Task SeedDev_WithToken_AgainstASharedHost_Refuses_AndWritesNothing()
    {
        var (database, pepper) = await _harness.SeededDatabaseAsync();
        var remoteApp =
            "Host=db.internal.example.com;Port=5432;Database=evidgate;Username=evidgate_app;Password=<unused>";

        var run = _harness.RunWithToken(database, pepper, appConnectionString: remoteApp);

        run.ExitCode.ShouldBe(MigratorExitCodes.InvalidInvocation);
        run.Error.ShouldContain("application database host");
        run.Output.ShouldBeEmpty();
        await _harness.AssertNothingWrittenAsync(database);
    }

    [Fact]
    public async Task SeedDev_WithToken_AgainstASharedOwnerHost_Refuses_AndWritesNothing()
    {
        var (database, pepper) = await _harness.SeededDatabaseAsync();

        var remoteOwner =
            "Host=db.prod.example.com;Port=5432;Database=evidgate;Username=evidgate_owner;Password=<unused>";
        var run = _harness.RunWithToken(database, pepper, ownerConnectionString: remoteOwner);

        run.ExitCode.ShouldBe(MigratorExitCodes.InvalidInvocation);
        run.Error.ShouldContain("not local");
        run.Output.ShouldBeEmpty();
        await _harness.AssertNothingWrittenAsync(database);
    }

    [Fact]
    public async Task SeedDev_WithToken_WithoutPepper_FailsClosed_AndWritesNothing()
    {
        var (database, pepper) = await _harness.SeededDatabaseAsync();

        var run = _harness.RunWithToken(database, pepper, omitPepper: true);

        run.ExitCode.ShouldBe(MigratorExitCodes.InvalidInvocation);
        run.Error.ShouldContain(SecretNames.CiPepper);
        run.Error.ShouldContain("nothing was written");
        run.Output.ShouldBeEmpty();
        await _harness.AssertNothingWrittenAsync(database);
    }

    /// <summary>A pepper that decodes to too few bytes, and one that is not base64 at all, are both refused up front.</summary>
    public static TheoryData<string> UnusablePeppers => new()
    {
        { "not base64 at all" },
        { Convert.ToBase64String(new byte[31]) },
    };

    [Theory]
    [MemberData(nameof(UnusablePeppers))]
    public async Task SeedDev_WithToken_UnusablePepper_Refuses_AndWritesNothing(string pepper)
    {
        var (database, _) = await _harness.SeededDatabaseAsync();

        var run = _harness.RunWithToken(database, pepper);

        run.ExitCode.ShouldBe(MigratorExitCodes.InvalidInvocation);
        run.Error.ShouldContain("pepper", Case.Insensitive);
        run.Error.ShouldContain("nothing was written");
        run.Output.ShouldBeEmpty();
        await _harness.AssertNothingWrittenAsync(database);
    }

    [Fact]
    public async Task SeedDev_WithToken_WithoutAppConnection_FailsClosed_AndWritesNothing()
    {
        var (database, pepper) = await _harness.SeededDatabaseAsync();

        var run = _harness.RunWithToken(database, pepper, appConnectionString: string.Empty);

        run.ExitCode.ShouldNotBe(MigratorExitCodes.Success);
        run.Error.ShouldContain(SecretNames.DatabaseConnectionString);
        run.Output.ShouldBeEmpty();
        await _harness.AssertNothingWrittenAsync(database);
    }

    public static TheoryData<string[], string> InvalidCombinations => new()
    {
        { ["--with-token"], "'--with-token' is only valid with seed-dev" },
        { ["--status", "--with-token"], "'--with-token' is only valid with seed-dev" },
        { ["seed-dev", "seed-dev"], "expected at most one argument" },
        { ["seed-dev", "--with-token", "--status"], "expected at most two arguments" },
    };

    [Theory]
    [MemberData(nameof(InvalidCombinations))]
    public void SeedDev_WithToken_InvalidCombinations_AreRefused(string[] args, string expected)
    {
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var error = new StringWriter(CultureInfo.InvariantCulture);

        var code = MigratorHost.Run(args, output, error, SeedDevTokenHarness.NoEnvironment());

        code.ShouldBe(MigratorExitCodes.InvalidInvocation);
        error.ToString().ShouldContain(expected);
        output.ToString().ShouldBeEmpty();
    }

    [Fact]
    public void SeedDev_Usage_Mentions_TheTokenSwitch()
    {
        MigratorHost.Usage.ShouldContain("--with-token");
        MigratorHost.Usage.ShouldContain(SecretNames.CiPepper);
    }
}
