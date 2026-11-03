using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// AC10 / 00 §5.4.7 (TL T2) against a real PostgreSQL: the five authoring tables of migration 0039
/// run under FORCE row level security with the tenant policy over
/// <c>evidgate.current_org_id()</c>, so a connection with no tenant setting reads **zero rows** and
/// no error, a tenant-scoped connection reads exactly its own org's rows, and a write that names
/// another org is refused by the policy's <c>WITH CHECK</c>.
/// <para>
/// The suite-level <c>TenantGucTests</c> discovers the org-scoped tables from the catalogue and
/// covers the same contract for every table; this case repeats it for the five tables of this
/// migration so the authoring lane fails on its own file rather than on a discovery rule.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
public sealed class AuthoringRlsTests
{
    /// <summary>A well-formed RFC 8785 content hash: <c>sha256:&lt;64 hex&gt;</c>.</summary>
    private const string ContentHash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static readonly string[] Tables =
    [
        "feature_node",
        "checklist_item",
        "manual_test_case",
        "authoring_code_counter",
        "authoring_version",
    ];

    private readonly PostgresTenancyFixture _fixture;

    public AuthoringRlsTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task TenantGuc_Unset_ReturnsZeroRows()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var first = await SeedTenantWithRowsAsync(database, Unique.Slug("acme"), "checkout");
        var second = await SeedTenantWithRowsAsync(database, Unique.Slug("globex"), "billing");

        // The rows exist, one per table per tenant: the superuser sees both tenants' rows.
        foreach (var table in Tables)
        {
            (await _fixture.ScalarAsync<long>(
                database,
                $"select count(*) from public.{table} where org_id = '{first.Org}'"))
                .ShouldBe(1, $"{table}: the first tenant is seeded");
            (await _fixture.ScalarAsync<long>(
                database,
                $"select count(*) from public.{table} where org_id = '{second.Org}'"))
                .ShouldBe(1, $"{table}: the second tenant is seeded");
            (await _fixture.ScalarAsync<long>(
                database,
                $"select count(*) from public.{table} where org_id in ('{first.Org}', '{second.Org}')"))
                .ShouldBe(2, $"{table}: both tenants are seeded");
        }

        // No tenant setting at all: the helper answers NULL and every table returns zero rows.
        await using (var unset = await TenantScope.BeginAsync(_fixture.AppConnectionString(database)))
        {
            (await unset.ScalarAsync<bool>("select evidgate.current_org_id() is null"))
                .ShouldBeTrue("the helper returns NULL, never an error, when the setting is missing");

            foreach (var table in Tables)
            {
                (await unset.ScalarAsync<long>($"select count(*) from public.{table}"))
                    .ShouldBe(0, $"{table} must return zero rows without a tenant context");
            }
        }

        // Scoped to the first tenant: exactly its own row in every table, and none of the second's.
        await using (var scoped = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), first.Org))
        {
            foreach (var table in Tables)
            {
                (await scoped.ScalarAsync<long>($"select count(*) from public.{table}"))
                    .ShouldBe(1, $"{table}: exactly one org-A row");
            }

            (await scoped.ScalarAsync<long>(
                $"select count(*) from public.feature_node where org_id = '{second.Org}'"))
                .ShouldBe(0, "org B's rows stay invisible to org A");
        }

        // A write that names another org is refused by the tenant policy's WITH CHECK.
        var crossTenant = await CaptureAppErrorAsync(
            database,
            first.Org,
            "insert into public.feature_node (id, org_id, project_id, kind, title, position) "
            + $"values ('{Guid.CreateVersion7()}', '{second.Org}', '{second.Project}', 'module', 'Leak', 0)");
        crossTenant.SqlState.ShouldBe("42501");
        crossTenant.MessageText.ShouldContain("row-level security");

        // …and the same insert in the caller's own org is accepted (the refusal above is the org,
        // not the shape of the statement).
        await using (var own = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), first.Org))
        {
            (await own.ExecuteAsync(
                "insert into public.feature_node (id, org_id, project_id, kind, title, position) "
                + "values (@id, @org_id, @project_id, 'module', 'Own', 1)",
                ("id", Guid.CreateVersion7()),
                ("org_id", first.Org),
                ("project_id", first.Project))).ShouldBe(1);
            await own.CommitAsync();
        }

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.feature_node where org_id = '{first.Org}'"))
            .ShouldBe(2);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>One org, one project and one row in each of the five tables, written as the migration role.</summary>
    private async Task<(Guid Org, Guid Project)> SeedTenantWithRowsAsync(string database, string orgSlug, string projectSlug)
    {
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var node = Guid.CreateVersion7();
        var item = Guid.CreateVersion7();
        var manualCase = Guid.CreateVersion7();

        await using var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));

        await seed.ExecuteAsync(
            "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
            ("id", org),
            ("name", orgSlug),
            ("slug", orgSlug));
        await seed.ExecuteAsync(
            "insert into public.project (id, org_id, name, slug) values (@id, @org_id, @name, @slug)",
            ("id", project),
            ("org_id", org),
            ("name", projectSlug),
            ("slug", projectSlug));
        await seed.ExecuteAsync(
            "insert into public.feature_node (id, org_id, project_id, kind, title, position) "
            + "values (@id, @org_id, @project_id, 'module', 'Shop', 0)",
            ("id", node),
            ("org_id", org),
            ("project_id", project));
        await seed.ExecuteAsync(
            "insert into public.checklist_item "
            + "(id, org_id, project_id, feature_node_id, title, priority, state, source, position) "
            + "values (@id, @org_id, @project_id, @feature_node_id, 'Checkout works', 'high', 'active', 'manual', 0)",
            ("id", item),
            ("org_id", org),
            ("project_id", project),
            ("feature_node_id", node));
        await seed.ExecuteAsync(
            "insert into public.manual_test_case "
            + "(id, org_id, project_id, checklist_item_id, code, title, steps, priority, state, source) "
            + "values (@id, @org_id, @project_id, @checklist_item_id, @code, 'Buy one item', '{\"steps\": []}'::jsonb, 'high', 'draft', 'manual')",
            ("id", manualCase),
            ("org_id", org),
            ("project_id", project),
            ("checklist_item_id", item),
            ("code", orgSlug.ToUpperInvariant()[..2] + "P-001"));
        await seed.ExecuteAsync(
            "insert into public.authoring_code_counter (id, org_id, project_id) values (@id, @org_id, @project_id)",
            ("id", Guid.CreateVersion7()),
            ("org_id", org),
            ("project_id", project));
        await seed.ExecuteAsync(
            "insert into public.authoring_version "
            + "(id, org_id, project_id, entity_kind, entity_id, version_no, batch_id, change_kind, content, content_hash, source) "
            + "values (@id, @org_id, @project_id, 'feature_node', @entity_id, 1, @batch_id, 'create', '{}'::jsonb, @content_hash, 'manual')",
            ("id", Guid.CreateVersion7()),
            ("org_id", org),
            ("project_id", project),
            ("entity_id", node),
            ("batch_id", Guid.CreateVersion7()),
            ("content_hash", ContentHash));

        await seed.CommitAsync();

        return (org, project);
    }

    /// <summary>Runs one statement as the app role inside a fresh tenant scope and returns the server error.</summary>
    private async Task<PostgresException> CaptureAppErrorAsync(string database, Guid org, string sql)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org);

        return await scope.CaptureErrorAsync(sql);
    }
}
