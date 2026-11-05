// A fixture and its revisions are append-only for every role: the application role holds no UPDATE, DELETE or
// TRUNCATE grant, the owning role is stopped by the revision trigger, and the insert policy keeps one tenant out
// of another tenant's fixture. Each refusal gets its own transaction, because a failed statement aborts it.
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Both tables claim the same append-only property through two different mechanisms, so both are asserted: a
/// missing grant refuses the application role before any trigger runs, and the trigger refuses the role that owns
/// the table and could otherwise rewrite a pinned revision. A cross-org insert is refused by the row policy
/// while a same-org insert through the same role and connection succeeds.
/// </summary>
[Trait("Category", "Docker")]
public sealed class FixtureImmutabilityTests
{
    private const string FixtureTable = "fixture";
    private const string RevisionTable = "fixture_revision";

    private static readonly string[] MutationStatements =
    [
        "update public.fixture set fixture_key = 'orders-renamed'",
        "delete from public.fixture",
        "update public.fixture_revision set body_ref = 'seeds/other.json'",
        "delete from public.fixture_revision",
    ];

    private readonly PostgresTenancyFixture _fixture;

    public FixtureImmutabilityTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AppRole_HoldsSelectAndInsert_ButNoUpdateDeleteOrTruncate()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var tenant = await FixtureSeed.TenantAsync(_fixture, database);
        await FixtureSeed.FixtureAsync(_fixture, database, tenant.Org, tenant.Project);
        await FixtureSeed.RevisionAsync(
            _fixture, database, tenant.Org, tenant.Project,
            await FixtureSeed.FixtureAsync(_fixture, database, tenant.Org, tenant.Project, "invoices-seed"),
            "invoices-seed");

        foreach (var table in new[] { FixtureTable, RevisionTable })
        {
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", table, "SELECT")).ShouldBeTrue(
                $"the tenant reads its own {table} rows");
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", table, "INSERT")).ShouldBeTrue(
                $"a new fixture or revision is written through the application role");
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", table, "UPDATE")).ShouldBeFalse(
                $"{table} is append-only for the application role");
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", table, "DELETE")).ShouldBeFalse(
                $"{table} is append-only for the application role");
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", table, "TRUNCATE")).ShouldBeFalse(
                $"{table} must not be emptied by the application role");
        }

        // Positive control for the reads: the same role and tenant setting see the seeded rows.
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), tenant.Org);
        (await scope.ScalarAsync<long>("select count(*) from public.fixture")).ShouldBe(2);
        (await scope.ScalarAsync<long>("select count(*) from public.fixture_revision")).ShouldBe(1);
    }

    [Fact]
    public async Task AppRole_UpdateAndDelete_AreRefusedByGrantAndTheRowsSurvive()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var tenant = await FixtureSeed.TenantAsync(_fixture, database);
        var fixtureId = await FixtureSeed.FixtureAsync(_fixture, database, tenant.Org, tenant.Project);
        var revisionId = await FixtureSeed.RevisionAsync(
            _fixture, database, tenant.Org, tenant.Project, fixtureId);
        var app = _fixture.AppConnectionString(database);

        foreach (var statement in MutationStatements)
        {
            var error = await FixtureSeed.RefusedAsync(app, tenant.Org, FixtureSeed.Literal(statement), "42501");
            error.MessageText.ShouldContain("permission denied", customMessage: statement);
        }

        // Positive control: the same role appends a second revision, so the refusals above came from the
        // statement's privilege and not from a broken connection or tenant setting.
        await FixtureSeed.AcceptedAsync(
            app,
            tenant.Org,
            FixtureSeed.RevisionRow(
                Guid.CreateVersion7(), tenant.Org, tenant.Project, fixtureId,
                revisionNo: 2, hash: FixtureSeed.Hash('2')));

        (await _fixture.ScalarAsync<string>(
            database, $"select fixture_key from public.fixture where id = '{fixtureId}'"))
            .ShouldBe(FixtureSeed.DefaultKey);
        (await _fixture.ScalarAsync<string>(
            database, $"select body_ref from public.fixture_revision where id = '{revisionId}'"))
            .ShouldBe("seeds/orders.json");
        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.fixture")).ShouldBe(1);
        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.fixture_revision")).ShouldBe(2);
    }

    [Fact]
    public async Task OwnerRole_UpdateAndDelete_RaiseRevisionImmutable()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var tenant = await FixtureSeed.TenantAsync(_fixture, database);
        var fixtureId = await FixtureSeed.FixtureAsync(_fixture, database, tenant.Org, tenant.Project);
        var revisionId = await FixtureSeed.RevisionAsync(
            _fixture, database, tenant.Org, tenant.Project, fixtureId);
        var owner = _fixture.OwnerConnectionString(database);

        foreach (var statement in MutationStatements)
        {
            var error = await FixtureSeed.RefusedAsync(owner, tenant.Org, FixtureSeed.Literal(statement), "23514");
            error.MessageText.ShouldContain("revision_immutable", customMessage: statement);
        }

        // Positive control: the owner reads both tables through its backfill policy and may append.
        await using (var scope = await TenantScope.BeginAsync(owner, tenant.Org))
        {
            (await scope.ScalarAsync<long>("select count(*) from public.fixture")).ShouldBe(1);
            (await scope.ScalarAsync<long>("select count(*) from public.fixture_revision")).ShouldBe(1);
        }

        await FixtureSeed.AcceptedAsync(
            owner,
            tenant.Org,
            FixtureSeed.RevisionRow(
                Guid.CreateVersion7(), tenant.Org, tenant.Project, fixtureId,
                revisionNo: 2, hash: FixtureSeed.Hash('3')));

        (await _fixture.ScalarAsync<string>(
            database, $"select fixture_key from public.fixture where id = '{fixtureId}'"))
            .ShouldBe(FixtureSeed.DefaultKey);
        (await _fixture.ScalarAsync<string>(
            database, $"select body_ref from public.fixture_revision where id = '{revisionId}'"))
            .ShouldBe("seeds/orders.json");
        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.fixture_revision")).ShouldBe(2);
    }

    [Fact]
    public async Task AppRole_Truncate_IsRefusedAndNoRowIsRemoved()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var tenant = await FixtureSeed.TenantAsync(_fixture, database);
        var fixtureId = await FixtureSeed.FixtureAsync(_fixture, database, tenant.Org, tenant.Project);
        await FixtureSeed.RevisionAsync(_fixture, database, tenant.Org, tenant.Project, fixtureId);
        var app = _fixture.AppConnectionString(database);

        foreach (var table in new[] { FixtureTable, RevisionTable })
        {
            var error = await FixtureSeed.RefusedAsync(
                app, tenant.Org, FixtureSeed.Literal($"truncate public.{table}"), "42501");
            error.MessageText.ShouldContain("permission denied", customMessage: table);
        }

        // A refused TRUNCATE must leave the data untouched: TRUNCATE bypasses every row trigger, so the grant
        // is the only thing standing between the application role and an emptied catalog.
        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.fixture")).ShouldBe(1);
        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.fixture_revision")).ShouldBe(1);
    }

    [Fact]
    public async Task AppRole_InsertOfAnotherOrgsRow_IsRefusedByTheRowPolicy()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var own = await FixtureSeed.TenantAsync(_fixture, database);
        var other = await FixtureSeed.TenantAsync(_fixture, database);
        var otherFixture = await FixtureSeed.FixtureAsync(_fixture, database, other.Org, other.Project);
        var app = _fixture.AppConnectionString(database);

        // The other org's fixture exists, so the revision row below is refused by the policy rather than by a
        // missing parent, and the tenant setting is the only thing that changes between the two cases.
        var fixtureError = await FixtureSeed.RefusedAsync(
            app,
            own.Org,
            FixtureSeed.FixtureRow(Guid.CreateVersion7(), other.Org, other.Project),
            "42501");
        fixtureError.MessageText.ShouldContain("row-level security");

        var revisionError = await FixtureSeed.RefusedAsync(
            app,
            own.Org,
            FixtureSeed.RevisionRow(
                Guid.CreateVersion7(), other.Org, other.Project, otherFixture,
                revisionNo: 1, hash: FixtureSeed.Hash('7')),
            "42501");
        revisionError.MessageText.ShouldContain("row-level security");

        // Positive control: the same rows under this tenant's own org are accepted.
        await FixtureSeed.AcceptedAsync(
            app, own.Org, FixtureSeed.FixtureRow(Guid.CreateVersion7(), own.Org, own.Project));

        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.fixture")).ShouldBe(2);
        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.fixture_revision")).ShouldBe(0);
    }
}
