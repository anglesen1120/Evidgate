using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Migrator;
using Shouldly;
using Xunit;

namespace Migrator.IntegrationTests;

/// <summary><c>seed-dev</c> writes the synthetic dev tenant idempotently, then the dev catalog suite, and refuses outside dev.</summary>
[Collection(MigratorPostgresSuite.Name)]
[Trait("Category", "Docker")]
public sealed class SeedDevTests
{
    private readonly PostgresMigratorFixture _fixture;
    private readonly SeedDevTokenHarness _harness;

    public SeedDevTests(PostgresMigratorFixture fixture)
    {
        _fixture = fixture;
        _harness = new SeedDevTokenHarness(fixture);
    }

    [Fact]
    public async Task SeedDev_RunTwice_SameCounts()
    {
        var database = await _harness.MigratedDatabaseAsync();

        var first = RunSeed(database, "Development");
        first.ExitCode.ShouldBe(MigratorExitCodes.Success, first.Error);
        var counts = await CountsAsync(database);

        counts.ShouldBe(new SeedCounts(1, 1, 1, 2, 1));

        var second = RunSeed(database, "Development");
        second.ExitCode.ShouldBe(MigratorExitCodes.Success);
        second.Output.ShouldContain("0 row(s) written");
        (await CountsAsync(database)).ShouldBe(counts);
    }

    [Fact]
    public async Task SeedDev_SeedsTheRolesOfTheCatalogue_AndAnActiveBinding()
    {
        var database = await _harness.MigratedDatabaseAsync();
        RunSeed(database, "Development").ExitCode.ShouldBe(MigratorExitCodes.Success);

        (await _fixture.ScalarAsync<string>(
            database,
            $"select string_agg(r.code, ',' order by r.code) from public.membership_role mr join public.role r on r.id = mr.role_id where mr.membership_id = '{DevSeed.MembershipId}'"))
            .ShouldBe("release_approver,tenant_admin");
        (await _fixture.ScalarAsync<string>(database, "select status from public.ci_binding")).ShouldBe("active");
        (await _fixture.ScalarAsync<long>(database, "select repository_id from public.ci_binding")).ShouldBe(DevSeed.DevRepositoryId);
        (await _fixture.ScalarAsync<string>(database, "select status from public.membership")).ShouldBe("active");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("development")]
    [InlineData(null)]
    public async Task SeedDev_OutsideDevelopment_Refuses_AndWritesNothing(string? environment)
    {
        var database = await _harness.MigratedDatabaseAsync();

        var run = RunSeed(database, environment);

        run.ExitCode.ShouldBe(MigratorExitCodes.InvalidInvocation);
        run.Error.ShouldContain("refused");
        (await CountsAsync(database)).ShouldBe(new SeedCounts(0, 0, 0, 0, 0));
    }

    [Theory]
    [InlineData("db.internal.example.com", false)]
    [InlineData("203.0.113.9", false)]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("postgres", true)]
    [InlineData("", false)]
    [InlineData("localhost,prodhost", false)]
    [InlineData("prod-db", false)]
    [InlineData("/var/run/postgresql", true)]
    public void SeedDev_OnlyLocalHostsAreAccepted(string host, bool expected) => DevSeed.IsLocalHost(host).ShouldBe(expected);

    [Fact]
    public void SeedDev_AgainstASharedHost_Refuses_BeforeConnecting()
    {
        // Both connections name the shared host: the owner guard fires first, and the app-host guard behind it would
        // refuse the same way, so the run never reaches either database.
        var variables = SeedDevTokenHarness.NoEnvironment();
        variables[MigratorOptions.ConnectionStringVariable] = SharedHost("evidgate_owner");
        variables[DevSeed.EnvironmentVariable] = "Development";
        variables[SecretNames.DatabaseConnectionString] = SharedHost("evidgate_app");

        var run = SeedDevTokenHarness.Run(["seed-dev"], variables);

        run.ExitCode.ShouldBe(MigratorExitCodes.InvalidInvocation);
        run.Error.ShouldContain("not local");
    }

    [Fact]
    public void SeedDev_Usage_Mentions_TheCommand() => MigratorHost.Usage.ShouldContain("seed-dev");

    private static string SharedHost(string role) =>
        $"Host=db.prod.example.com;Port=5432;Database=evidgate;Username={role}";

    private SeedDevTokenHarness.RunResult RunSeed(string database, string? environment) =>
        SeedDevTokenHarness.Run(
            ["seed-dev"],
            _harness.Variables(database, pepper: string.Empty, environment, omitPepper: true));

    private async Task<SeedCounts> CountsAsync(string database) => new(
        await _fixture.ScalarAsync<long>(database, "select count(*) from public.organization"),
        await _fixture.ScalarAsync<long>(database, "select count(*) from public.project"),
        await _fixture.ScalarAsync<long>(database, "select count(*) from public.membership"),
        await _fixture.ScalarAsync<long>(database, "select count(*) from public.membership_role"),
        await _fixture.ScalarAsync<long>(database, "select count(*) from public.ci_binding"));

    private sealed record SeedCounts(long Orgs, long Projects, long Memberships, long Grants, long Bindings);
}
