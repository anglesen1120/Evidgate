// The `fixture` and `fixture_revision` tables as PostgreSQL enforces them: the two composite keys, the unique
// keys a fixture key and a content hash rely on, the closed values a revision may carry, and the tenant boundary
// of both tables (ENABLE + FORCE row level security, every policy naming exactly one role).
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// A fixture names one project's set of test data and a revision pins one immutable version of it, so the
/// database has to refuse a row that names another org's fixture, another project's fixture, or a key its
/// fixture does not hold, and has to refuse a second row under a key or a content hash the fixture already holds.
/// </summary>
[Trait("Category", "Docker")]
public sealed class FixtureSchemaTests
{
    private const string FixtureTable = "fixture";
    private const string RevisionTable = "fixture_revision";

    private static readonly string[] ExpectedPolicies =
    [
        "fixture|fixture_app_tenant|evidgate_app|ALL",
        "fixture|fixture_owner_backfill|evidgate_owner|ALL",
        "fixture_revision|fixture_app_tenant|evidgate_app|ALL",
        "fixture_revision|fixture_revision_owner_backfill|evidgate_owner|ALL",
    ];

    private readonly PostgresTenancyFixture _fixture;

    public FixtureSchemaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Migration_IsAppliedToTheMigratedTemplate()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var journal = await _fixture.ReadJournalAsync(database);

        journal.ShouldContain(
            row => string.Equals(row.Name, "0074_catalog_fixtures.sql", StringComparison.Ordinal),
            "the fixture tables must come from the migration, not from a test's own DDL");

        (await _fixture.TableExistsAsync(database, FixtureTable)).ShouldBeTrue();
        (await _fixture.TableExistsAsync(database, RevisionTable)).ShouldBeTrue();
    }

    [Fact]
    public async Task Fixture_ProjectOfAnotherOrgOrOfNone_IsRefusedByTheCompositeKey()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var own = await FixtureSeed.TenantAsync(_fixture, database);
        var other = await FixtureSeed.TenantAsync(_fixture, database);
        var owner = _fixture.OwnerConnectionString(database);

        // The key is (org_id, project_id) as a pair: neither a real project of another org nor an unknown
        // project can stand in for this org's project.
        await FixtureSeed.RefusedAsync(
            owner,
            null,
            FixtureSeed.FixtureRow(Guid.CreateVersion7(), own.Org, other.Project),
            "23503",
            "fixture_project_fk");
        await FixtureSeed.RefusedAsync(
            owner,
            null,
            FixtureSeed.FixtureRow(Guid.CreateVersion7(), other.Org, own.Project),
            "23503",
            "fixture_project_fk");
        await FixtureSeed.RefusedAsync(
            owner,
            null,
            FixtureSeed.FixtureRow(Guid.CreateVersion7(), own.Org, Guid.CreateVersion7()),
            "23503",
            "fixture_project_fk");

        // The same statement with only the project corrected is accepted: the org above was real.
        await FixtureSeed.AcceptedAsync(
            owner,
            null,
            FixtureSeed.FixtureRow(Guid.CreateVersion7(), own.Org, own.Project));

        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.fixture")).ShouldBe(1);
    }

    [Fact]
    public async Task Revision_OrgProjectAndKey_ArePinnedTogetherByOneCompositeKey()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var own = await FixtureSeed.TenantAsync(_fixture, database);
        var other = await FixtureSeed.TenantAsync(_fixture, database);
        var owner = _fixture.OwnerConnectionString(database);
        var fixtureId = await FixtureSeed.FixtureAsync(_fixture, database, own.Org, own.Project);

        await FixtureSeed.AcceptedAsync(
            owner,
            null,
            FixtureSeed.RevisionRow(
                Guid.CreateVersion7(), own.Org, own.Project, fixtureId, revisionNo: 1, hash: FixtureSeed.Hash('1')));

        // Another org's row named by this org's scope: the referenced fixture exists, under another org.
        await FixtureSeed.RefusedAsync(
            owner,
            null,
            FixtureSeed.RevisionRow(
                Guid.CreateVersion7(), other.Org, other.Project, fixtureId, revisionNo: 2, hash: FixtureSeed.Hash('2')),
            "23503",
            "fixture_revision_fixture_fk");
        await FixtureSeed.RefusedAsync(
            owner,
            null,
            FixtureSeed.RevisionRow(
                Guid.CreateVersion7(), other.Org, own.Project, fixtureId, revisionNo: 3, hash: FixtureSeed.Hash('3')),
            "23503",
            "fixture_revision_fixture_fk");

        // The same org and fixture, but a project the fixture is not in.
        await FixtureSeed.RefusedAsync(
            owner,
            null,
            FixtureSeed.RevisionRow(
                Guid.CreateVersion7(), own.Org, other.Project, fixtureId, revisionNo: 4, hash: FixtureSeed.Hash('4')),
            "23503",
            "fixture_revision_fixture_fk");

        // The denormalised key cannot drift from the fixture's own key.
        await FixtureSeed.RefusedAsync(
            owner,
            null,
            FixtureSeed.RevisionRow(
                Guid.CreateVersion7(), own.Org, own.Project, fixtureId, "orders-other",
                revisionNo: 5, hash: FixtureSeed.Hash('5')),
            "23503",
            "fixture_revision_fixture_fk");

        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.fixture_revision")).ShouldBe(1);
    }

    [Fact]
    public async Task FixtureKey_IsUniquePerProject_AndReusableInAnotherProject()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var own = await FixtureSeed.TenantAsync(_fixture, database);
        var sibling = await FixtureSeed.ProjectAsync(_fixture, database, own.Org);
        var other = await FixtureSeed.TenantAsync(_fixture, database);
        var owner = _fixture.OwnerConnectionString(database);

        await FixtureSeed.AcceptedAsync(owner, null, FixtureSeed.FixtureRow(Guid.CreateVersion7(), own.Org, own.Project));
        await FixtureSeed.RefusedAsync(
            owner,
            null,
            FixtureSeed.FixtureRow(Guid.CreateVersion7(), own.Org, own.Project),
            "23505",
            "fixture_project_key");

        // The key belongs to a project, not to the org: a sibling project and another org both take it.
        await FixtureSeed.AcceptedAsync(owner, null, FixtureSeed.FixtureRow(Guid.CreateVersion7(), own.Org, sibling));
        await FixtureSeed.AcceptedAsync(owner, null, FixtureSeed.FixtureRow(Guid.CreateVersion7(), other.Org, other.Project));

        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.fixture")).ShouldBe(3);
    }

    [Fact]
    public async Task RevisionNumberAndHash_AreUniquePerFixture_AndRepeatAcrossFixtures()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var own = await FixtureSeed.TenantAsync(_fixture, database);
        var owner = _fixture.OwnerConnectionString(database);
        var orders = await FixtureSeed.FixtureAsync(_fixture, database, own.Org, own.Project);
        var invoices = await FixtureSeed.FixtureAsync(
            _fixture, database, own.Org, own.Project, "invoices-seed");

        await FixtureSeed.AcceptedAsync(
            owner,
            null,
            FixtureSeed.RevisionRow(
                Guid.CreateVersion7(), own.Org, own.Project, orders, revisionNo: 1, hash: FixtureSeed.Hash('1')));

        // One fixture holds each number and each hash once, so a retried create answers the stored revision
        // instead of appending a second row with the same number or the same content.
        await FixtureSeed.RefusedAsync(
            owner,
            null,
            FixtureSeed.RevisionRow(
                Guid.CreateVersion7(), own.Org, own.Project, orders, revisionNo: 1, hash: FixtureSeed.Hash('2')),
            "23505",
            "fixture_revision_no_key");
        await FixtureSeed.RefusedAsync(
            owner,
            null,
            FixtureSeed.RevisionRow(
                Guid.CreateVersion7(), own.Org, own.Project, orders, revisionNo: 2, hash: FixtureSeed.Hash('1')),
            "23505",
            "fixture_revision_hash_key");

        // Both are scoped to the fixture: another fixture takes the same first number and the same content.
        await FixtureSeed.AcceptedAsync(
            owner,
            null,
            FixtureSeed.RevisionRow(
                Guid.CreateVersion7(), own.Org, own.Project, invoices, "invoices-seed",
                revisionNo: 1, hash: FixtureSeed.Hash('1')));
        await FixtureSeed.AcceptedAsync(
            owner,
            null,
            FixtureSeed.RevisionRow(
                Guid.CreateVersion7(), own.Org, own.Project, invoices, "invoices-seed",
                revisionNo: 2, hash: FixtureSeed.Hash('2')));

        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.fixture_revision")).ShouldBe(3);
    }

    [Fact]
    public async Task FixtureKey_OutsideItsShape_IsRefused()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var own = await FixtureSeed.TenantAsync(_fixture, database);
        var owner = _fixture.OwnerConnectionString(database);

        foreach (var key in new[] { "Orders", "orders_seed", "-orders", new string('a', 65) })
        {
            await FixtureSeed.RefusedAsync(
                owner,
                null,
                FixtureSeed.FixtureRow(Guid.CreateVersion7(), own.Org, own.Project, key),
                "23514",
                "fixture_key_format_check");
        }

        // The longest accepted key and the one-character key bracket the two bounds.
        await FixtureSeed.AcceptedAsync(
            owner, null, FixtureSeed.FixtureRow(Guid.CreateVersion7(), own.Org, own.Project, new string('a', 64)));
        await FixtureSeed.AcceptedAsync(
            owner, null, FixtureSeed.FixtureRow(Guid.CreateVersion7(), own.Org, own.Project, "9"));

        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.fixture")).ShouldBe(2);
    }

    [Fact]
    public async Task RevisionValues_OutsideTheirChecks_AreRefused()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var own = await FixtureSeed.TenantAsync(_fixture, database);
        var owner = _fixture.OwnerConnectionString(database);
        var fixtureId = await FixtureSeed.FixtureAsync(_fixture, database, own.Org, own.Project);

        (string Constraint, (string Sql, (string Name, object Value)[] Parameters) Row)[] refused =
        [
            ("fixture_revision_hash_check", FixtureSeed.RevisionRow(
                Guid.CreateVersion7(), own.Org, own.Project, fixtureId, revisionNo: 2, hash: "sha256:" + new string('A', 64))),
            ("fixture_revision_hash_check", FixtureSeed.RevisionRow(
                Guid.CreateVersion7(), own.Org, own.Project, fixtureId, revisionNo: 3, hash: "sha256:" + new string('a', 63))),
            ("fixture_revision_no_check", FixtureSeed.RevisionRow(
                Guid.CreateVersion7(), own.Org, own.Project, fixtureId, revisionNo: 0, hash: FixtureSeed.Hash('b'))),
            ("fixture_revision_timeout_check", FixtureSeed.RevisionRow(
                Guid.CreateVersion7(), own.Org, own.Project, fixtureId, revisionNo: 4, hash: FixtureSeed.Hash('c'), timeoutSeconds: 0)),
            ("fixture_revision_timeout_check", FixtureSeed.RevisionRow(
                Guid.CreateVersion7(), own.Org, own.Project, fixtureId, revisionNo: 5, hash: FixtureSeed.Hash('d'), timeoutSeconds: 86401)),
            ("fixture_revision_cleanup_check", FixtureSeed.RevisionRow(
                Guid.CreateVersion7(), own.Org, own.Project, fixtureId, revisionNo: 6, hash: FixtureSeed.Hash('e'), cleanupPolicy: "keep")),
            ("fixture_revision_seed_check", FixtureSeed.RevisionRow(
                Guid.CreateVersion7(), own.Org, own.Project, fixtureId, revisionNo: 7, hash: FixtureSeed.Hash('f'), seedVersion: string.Empty)),
            ("fixture_revision_seed_check", FixtureSeed.RevisionRow(
                Guid.CreateVersion7(), own.Org, own.Project, fixtureId, revisionNo: 8, hash: FixtureSeed.Hash('0'), seedVersion: new string('v', 65))),
            ("fixture_revision_prefix_check", FixtureSeed.RevisionRow(
                Guid.CreateVersion7(), own.Org, own.Project, fixtureId, revisionNo: 9, hash: FixtureSeed.Hash('1'), resourcePrefix: "fx_not-hex")),
            ("fixture_revision_body_ref_check", FixtureSeed.RevisionRow(
                Guid.CreateVersion7(), own.Org, own.Project, fixtureId, revisionNo: 10, hash: FixtureSeed.Hash('2'), bodyRef: "seeds/orders seed.json")),
            ("fixture_revision_body_ref_check", FixtureSeed.RevisionRow(
                Guid.CreateVersion7(), own.Org, own.Project, fixtureId, revisionNo: 11, hash: FixtureSeed.Hash('3'), bodyRef: string.Empty)),
            ("fixture_revision_body_ref_check", FixtureSeed.RevisionRow(
                Guid.CreateVersion7(), own.Org, own.Project, fixtureId, revisionNo: 12, hash: FixtureSeed.Hash('4'), bodyRef: new string('a', 1025))),
        ];

        foreach (var (constraint, row) in refused)
        {
            await FixtureSeed.RefusedAsync(owner, null, row, "23514", constraint);
        }

        // The bounds themselves and the other two cleanup policies are inside the vocabulary.
        await FixtureSeed.AcceptedAsync(
            owner,
            null,
            FixtureSeed.RevisionRow(
                Guid.CreateVersion7(), own.Org, own.Project, fixtureId, revisionNo: 2, hash: FixtureSeed.Hash('5'),
                timeoutSeconds: 1, cleanupPolicy: "none", seedVersion: new string('v', 64)));
        await FixtureSeed.AcceptedAsync(
            owner,
            null,
            FixtureSeed.RevisionRow(
                Guid.CreateVersion7(), own.Org, own.Project, fixtureId, revisionNo: 3, hash: FixtureSeed.Hash('6'),
                timeoutSeconds: 86400, cleanupPolicy: "truncate", bodyRef: "seeds/" + new string('a', 1018)));

        (await _fixture.ScalarAsync<long>(database, "select count(*) from public.fixture_revision")).ShouldBe(2);
    }

    [Fact]
    public async Task BothTables_ForceRowSecurity_UnderPoliciesThatNameOneRoleEach()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();

        foreach (var table in new[] { FixtureTable, RevisionTable })
        {
            var rowSecurity = await _fixture.RowSecurityAsync(database, table);
            rowSecurity.Enabled.ShouldBeTrue($"{table} carries ENABLE row level security");
            rowSecurity.Forced.ShouldBeTrue(
                $"{table} carries FORCE row level security, so the owning role cannot bypass the tenant boundary");

            // A policy without `TO` targets PUBLIC, and pg_policies reports that as the role `public`.
            var policiesWithoutARole = await _fixture.ScalarAsync<long>(
                database,
                $"select count(*) from pg_catalog.pg_policy where polrelid = 'public.{table}'::regclass and 0 = any(polroles)");
            policiesWithoutARole.ShouldBe(0, $"{table} must not hold a policy that applies to every role");

            var policiesWithoutACheck = await _fixture.ScalarAsync<long>(
                database,
                $"select count(*) from pg_catalog.pg_policy where polrelid = 'public.{table}'::regclass and polwithcheck is null");
            policiesWithoutACheck.ShouldBe(0, $"{table} writes are constrained by WITH CHECK, not only USING");
        }

        var policies = await _fixture.ColumnAsync<string>(
            database,
            """
            select tablename || '|' || policyname || '|' || array_to_string(roles, ',') || '|' || cmd
              from pg_catalog.pg_policies
             where schemaname = 'public' and tablename in ('fixture', 'fixture_revision')
            """);

        policies.ShouldBe(ExpectedPolicies, ignoreOrder: true);
    }
}
