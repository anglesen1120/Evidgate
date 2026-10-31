using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Migrator;
using Npgsql;
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
    private const string DevTokenLine = "EVIDGATE_DEV_TOKEN=";

    private readonly PostgresMigratorFixture _fixture;

    public SeedDevTokenTests(PostgresMigratorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SeedDev_Token_PrintedOnce_NotLogged_AndStoredOnlyAsHmac()
    {
        var (database, pepper) = await SeededDatabaseAsync();

        var run = RunWithToken(database, pepper);

        run.ExitCode.ShouldBe(MigratorExitCodes.Success, run.Error);

        // Exactly one token line, and it is a real token: the one-time secret never appears twice in the transcript.
        CountOf(run.Output, DevTokenLine).ShouldBe(1);
        var token = TokenOf(run.Output);
        token.ShouldStartWith("egt_");

        // Nothing but the deliberate stdout line carries the secret — not the error writer, not a log.
        run.Error.ShouldBeEmpty();
        run.Output.ShouldContain(DevSeedToken.ShownOnceNote);
        run.Error.ShouldNotContain(token);
        run.Error.ShouldNotContain("egt_");

        var (prefix, secret) = Split(token);
        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.ingest_token")).ShouldBe(1);
        (await _fixture.ScalarAsync<string>(database, "select name from public.ingest_token")).ShouldBe(DevSeed.TokenName);
        (await _fixture.ScalarAsync<string>(database, "select prefix from public.ingest_token")).ShouldBe(prefix);
        (await _fixture.ScalarAsync<string>(database, "select project_id::text from public.ingest_token")).ShouldBe(DevSeed.ProjectId.ToString("D"));
        (await _fixture.ScalarAsync<string>(
            database,
            "select string_agg(scope, ',' order by scope) from public.ingest_token t, unnest(t.scopes) as scope"))
            .ShouldBe("artifact:write,event:write,run:create");
        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.ingest_token where expires_at is null and revoked_at is null")).ShouldBe(1);

        // Only the HMAC is stored: token_hmac is exactly HMAC-SHA256(pepper, secret), and the secret half appears in no
        // stored text of the row.
        var storedHmac = await _fixture.ScalarAsync<string>(database, "select encode(token_hmac, 'hex') from public.ingest_token");
        storedHmac.ShouldBe(ExpectedHmac(pepper, secret));
        (await _fixture.ScalarAsync<string>(
            database,
            "select concat_ws('|', name, prefix, encode(token_hmac, 'hex'), scopes::text) from public.ingest_token"))
            .ShouldNotContain(secret);
    }

    [Fact]
    public async Task SeedDev_Token_Rerun_PrintsRecoveryAndMintsNothing()
    {
        var (database, pepper) = await SeededDatabaseAsync();
        RunWithToken(database, pepper).ExitCode.ShouldBe(MigratorExitCodes.Success);

        var rerun = RunWithToken(database, pepper);

        rerun.ExitCode.ShouldBe(MigratorExitCodes.Success, rerun.Error);
        rerun.Output.ShouldContain(DevSeedToken.RerunNote);
        rerun.Output.ShouldContain("scripts/dev/up.sh --reset");
        CountOf(rerun.Output, DevTokenLine).ShouldBe(0);
        rerun.Error.ShouldBeEmpty();
        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.ingest_token")).ShouldBe(1);
    }

    [Fact]
    public async Task SeedDev_SamplePolicy_SameHashOnRerun()
    {
        var (database, pepper) = await SeededDatabaseAsync();

        var first = RunWithToken(database, pepper);
        first.ExitCode.ShouldBe(MigratorExitCodes.Success, first.Error);

        var (firstId, firstHash) = await PolicyRevisionAsync(database);
        first.Output.ShouldContain($"seed-dev: sample policy revision 1 {firstHash} (created)");

        var rerun = RunWithToken(database, pepper);
        rerun.ExitCode.ShouldBe(MigratorExitCodes.Success, rerun.Error);

        var (rerunId, rerunHash) = await PolicyRevisionAsync(database);

        // One revision, same row, same content address: the replay path of the real command, not a second append.
        rerunHash.ShouldBe(firstHash);
        rerunId.ShouldBe(firstId);
        rerun.Output.ShouldContain($"seed-dev: sample policy revision 1 {firstHash} (already present)");
        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.policy_revision")).ShouldBe(1);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData(null)]
    public async Task SeedDev_WithToken_OutsideDevelopment_Refuses_AndWritesNothing(string? environment)
    {
        var (database, pepper) = await SeededDatabaseAsync();

        var run = RunWithToken(database, pepper, environment);

        run.ExitCode.ShouldBe(MigratorExitCodes.InvalidInvocation);
        run.Error.ShouldContain("refused");
        run.Output.ShouldBeEmpty();
        await AssertNothingWrittenAsync(database);
    }

    [Fact]
    public async Task SeedDev_WithToken_AgainstASharedHost_Refuses_AndWritesNothing()
    {
        var (database, pepper) = await SeededDatabaseAsync();
        var remoteApp =
            "Host=db.internal.example.com;Port=5432;Database=evidgate;Username=evidgate_app;Password=x";

        var run = RunWithToken(database, pepper, appConnectionString: remoteApp);

        run.ExitCode.ShouldBe(MigratorExitCodes.InvalidInvocation);
        run.Error.ShouldContain("application database host");
        run.Output.ShouldBeEmpty();
        await AssertNothingWrittenAsync(database);
    }

    [Fact]
    public async Task SeedDev_WithToken_AgainstASharedOwnerHost_Refuses_AndWritesNothing()
    {
        var (database, pepper) = await SeededDatabaseAsync();

        var run = RunWithToken(
            database,
            pepper,
            ownerConnectionString: "Host=db.prod.example.com;Port=5432;Database=evidgate;Username=evidgate_owner;Password=x");

        run.ExitCode.ShouldBe(MigratorExitCodes.InvalidInvocation);
        run.Error.ShouldContain("not local");
        run.Output.ShouldBeEmpty();
        await AssertNothingWrittenAsync(database);
    }

    [Fact]
    public async Task SeedDev_WithToken_WithoutPepper_FailsClosed_AndWritesNothing()
    {
        var (database, pepper) = await SeededDatabaseAsync();

        var run = RunWithToken(database, pepper, omitPepper: true);

        run.ExitCode.ShouldBe(MigratorExitCodes.InvalidInvocation);
        run.Error.ShouldContain(SecretNames.CiPepper);
        run.Error.ShouldContain("nothing was written");
        run.Output.ShouldBeEmpty();
        await AssertNothingWrittenAsync(database);
    }

    [Fact]
    public async Task SeedDev_WithToken_WithoutAppConnection_FailsClosed_AndWritesNothing()
    {
        var (database, pepper) = await SeededDatabaseAsync();

        var run = RunWithToken(database, pepper, appConnectionString: string.Empty);

        run.ExitCode.ShouldNotBe(MigratorExitCodes.Success);
        run.Error.ShouldContain(SecretNames.DatabaseConnectionString);
        run.Output.ShouldBeEmpty();
        await AssertNothingWrittenAsync(database);
    }

    public static TheoryData<string[], string> InvalidCombinations => new()
    {
        { ["--with-token"], "'--with-token' is only valid with seed-dev" },
        { ["--status", "--with-token"], "'--with-token' is only valid with seed-dev" },
        { ["seed-dev", "seed-dev"], "expected at most one argument" },
        { ["seed-dev", "--with-token", "--status"], "expected at most one argument" },
    };

    [Theory]
    [MemberData(nameof(InvalidCombinations))]
    public void SeedDev_WithToken_InvalidCombinations_AreRefused(string[] args, string expected)
    {
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var error = new StringWriter(CultureInfo.InvariantCulture);

        var code = MigratorHost.Run(args, output, error, NoEnvironment());

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

    // ------------------------------------------------------------------ helpers

    private async Task<(string Database, string Pepper)> SeededDatabaseAsync()
    {
        var database = await MigratedDatabaseAsync();

        return (database, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    /// <summary>
    /// A fresh database carrying the repository's migrations, with the application roles able to log in. Every
    /// connection string this class uses is unpooled: each seeded run opens its own app-role data source and creates
    /// its own database, so pooled connections that stay idle for the pool's idle lifetime would hold the container's
    /// remaining slots (53300) against the other classes in the collection.
    /// </summary>
    private async Task<string> MigratedDatabaseAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        MigratorHarness.Run(PostgresMigratorFixture.RepoMigrationsPath, Unpooled(_fixture.OwnerConnectionString(database)))
            .ExitCode.ShouldBe(MigratorExitCodes.Success);
        await _fixture.EnableApplicationLoginsAsync(database);

        return database;
    }

    private RunResult RunWithToken(
        string database,
        string pepper,
        string? environment = "Development",
        string? appConnectionString = null,
        bool omitPepper = false,
        string? ownerConnectionString = null)
    {
        var variables = NoEnvironment();
        variables[MigratorOptions.ConnectionStringVariable] =
            ownerConnectionString ?? Unpooled(_fixture.OwnerConnectionString(database));
        variables[DevSeed.EnvironmentVariable] = environment;
        variables[SecretNames.DatabaseConnectionString] =
            appConnectionString ?? Unpooled(_fixture.AppConnectionString(database));

        if (!omitPepper)
        {
            variables[SecretNames.CiPepper] = pepper;
        }

        return Run(["seed-dev", "--with-token"], variables);
    }

    private static Dictionary<string, string?> NoEnvironment() => new(StringComparer.Ordinal);

    /// <summary>
    /// The same connection string with pooling off: a connection is released the moment it is disposed, so this class
    /// cannot leave idle sessions on the shared container that the other classes then fail to get (53300).
    /// </summary>
    private static string Unpooled(string connectionString) => new NpgsqlConnectionStringBuilder(connectionString)
    {
        Pooling = false,
    }.ConnectionString;

    private static RunResult Run(string[] args, IReadOnlyDictionary<string, string?> variables)
    {
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var error = new StringWriter(CultureInfo.InvariantCulture);

        return new(MigratorHost.Run(args, output, error, variables), output.ToString(), error.ToString());
    }

    private async Task AssertNothingWrittenAsync(string database)
    {
        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.organization")).ShouldBe(0);
        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.project")).ShouldBe(0);
        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.ingest_token")).ShouldBe(0);
        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.policy_revision")).ShouldBe(0);
    }

    private async Task<(Guid Id, string Hash)> PolicyRevisionAsync(string database) => (
        Guid.Parse(await _fixture.ScalarAsync<string>(database, "select id::text from public.policy_revision")),
        await _fixture.ScalarAsync<string>(database, "select hash from public.policy_revision"));

    private static string TokenOf(string output) => output
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Single(line => line.StartsWith(DevTokenLine, StringComparison.Ordinal))[DevTokenLine.Length..];

    private static (string Prefix, string Secret) Split(string token)
    {
        // The secret half is base64url, so it can itself contain '_': split into three, never on every underscore.
        var parts = token.Split('_', 3);

        return (parts[1], parts[2]);
    }

    private static string ExpectedHmac(string pepper, string secret) => Convert.ToHexStringLower(
        HMACSHA256.HashData(Convert.FromBase64String(pepper), Encoding.UTF8.GetBytes(secret)));

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private sealed record RunResult(int ExitCode, string Output, string Error);
}
