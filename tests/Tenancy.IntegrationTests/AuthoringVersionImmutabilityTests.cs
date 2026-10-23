using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// AC6 / C6 against a real PostgreSQL: <c>authoring_version</c> is append-only, enforced in two
/// layers — the <c>REVOKE</c> speaks to <c>evidgate_app</c> (a 42501 at the ACL, before any BEFORE
/// ROW trigger runs), and the <c>authoring_version_immutable</c> trigger speaks to everyone else
/// (23514), including <c>evidgate_owner</c>. The owner's DELETE is the one allowed path, because the
/// 11.2.1 erasure sweep and 06.4.4-T1.2's purge must be able to remove a version row through an
/// owner-owned SECURITY DEFINER function.
/// <para>
/// It also pins the grant reading of the three entity tables: `update` is **column-scoped** (the
/// mutable columns — edits, the soft-delete stamp, restores) and `delete` is not granted at all, so a
/// physical delete is an owner-path operation only. `has_table_privilege(…, 'UPDATE')` is therefore
/// FALSE and `has_column_privilege(…, 'deleted_at', 'UPDATE')` is TRUE (0027:159, 0033:94).
/// </para>
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class AuthoringVersionImmutabilityTests
{
    /// <summary>A well-formed RFC 8785 content hash: <c>sha256:&lt;64 hex&gt;</c>.</summary>
    private const string ContentHash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static readonly string[] EntityTables = ["feature_node", "checklist_item", "manual_test_case"];

    private readonly PostgresTenancyFixture _fixture;

    public AuthoringVersionImmutabilityTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AppUpdateAndDelete_Refused_OwnerDeletePathWorks()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");
        var node = Guid.CreateVersion7();
        var versionId = Guid.CreateVersion7();

        await using (var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            await seed.ExecuteAsync(
                "insert into public.feature_node (id, org_id, project_id, kind, title, position) "
                + "values (@id, @org_id, @project_id, 'module', 'Shop', 0)",
                ("id", node),
                ("org_id", org),
                ("project_id", project));
            await seed.ExecuteAsync(InsertVersionSql, VersionParameters(versionId, org, project, node, 1));
            await seed.CommitAsync();
        }

        // The app appends versions (the writer's path) and only then meets the two layers below.
        await using (var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            (await app.ExecuteAsync(InsertVersionSql, VersionParameters(Guid.CreateVersion7(), org, project, node, 2)))
                .ShouldBe(1, "evidgate_app holds INSERT on the version log");
            await app.CommitAsync();
        }

        // Layer 1 — the ACL: 42501, raised before any BEFORE ROW trigger is reached.
        var appUpdate = await CaptureErrorAsync(
            _fixture.AppConnectionString(database),
            org,
            $"update public.authoring_version set reason = 'rewritten' where id = '{versionId}'");
        appUpdate.SqlState.ShouldBe("42501");

        var appDelete = await CaptureErrorAsync(
            _fixture.AppConnectionString(database),
            org,
            $"delete from public.authoring_version where id = '{versionId}'");
        appDelete.SqlState.ShouldBe("42501");

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.authoring_version where id = '{versionId}' and reason is null"))
            .ShouldBe(1, "neither refused statement changed the row");

        // Layer 2 — the trigger: the same UPDATE as evidgate_owner reaches the row and raises 23514.
        var ownerUpdate = await CaptureErrorAsync(
            _fixture.OwnerConnectionString(database),
            org,
            $"update public.authoring_version set reason = 'rewritten' where id = '{versionId}'");
        ownerUpdate.SqlState.ShouldBe("23514");
        ownerUpdate.MessageText.ShouldContain("authoring_version_immutable");

        // A third role: the operator holds SELECT only, so its DELETE never reaches the trigger.
        var operatorDelete = await CaptureErrorAsync(
            _fixture.OperatorConnectionString(database),
            org,
            $"delete from public.authoring_version where id = '{versionId}'");
        operatorDelete.SqlState.ShouldBe("42501");

        // The one allowed path: the owner's erasure DELETE.
        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            (await owner.ExecuteAsync($"delete from public.authoring_version where id = '{versionId}'"))
                .ShouldBe(1, "the owner erasure path deletes a version row");
            await owner.CommitAsync();
        }

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.authoring_version where id = '{versionId}'"))
            .ShouldBe(0);

        // The grants behind the two layers, read from the catalogue. The entity tables grant UPDATE
        // per column (0027:159, 0033:94 precedent), so `has_table_privilege(…, 'UPDATE')` is FALSE
        // and the mutable columns carry the privilege on their own.
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "authoring_version", "INSERT")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "authoring_version", "UPDATE")).ShouldBeFalse();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "authoring_version", "DELETE")).ShouldBeFalse();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_operator", "authoring_version", "SELECT")).ShouldBeTrue();
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_worker_sched", "authoring_version", "SELECT")).ShouldBeFalse();

        foreach (var table in EntityTables)
        {
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", table, "UPDATE"))
                .ShouldBeFalse($"{table}: UPDATE is column-scoped, never table-wide");
            (await ColumnPrivilegeAsync(database, table, "deleted_at"))
                .ShouldBeTrue($"{table}: the app soft-deletes in place");
            (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", table, "DELETE"))
                .ShouldBeFalse($"{table}: a physical delete is an owner-path operation only");

            foreach (var immutable in new[] { "id", "org_id", "project_id", "created_at" })
            {
                (await ColumnPrivilegeAsync(database, table, immutable))
                    .ShouldBeFalse($"{table}.{immutable} is written by the owner path only");
            }
        }

        (await ColumnPrivilegeAsync(database, "authoring_code_counter", "next_value"))
            .ShouldBeTrue("the allocator updates the counter row in place");
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "authoring_code_counter", "UPDATE"))
            .ShouldBeFalse("the counter grants only next_value");
        (await _fixture.HasTablePrivilegeAsync(database, "evidgate_app", "authoring_code_counter", "DELETE"))
            .ShouldBeFalse();
    }

    /// <summary>One column's UPDATE privilege for the application role (0027/0033's column-scoped grants).</summary>
    private Task<bool> ColumnPrivilegeAsync(string database, string table, string column) =>
        _fixture.ScalarAsync<bool>(
            database,
            $"select has_column_privilege('evidgate_app', 'public.{table}', '{column}', 'UPDATE')");

    // ------------------------------------------------------------------ helpers

    private const string InsertVersionSql =
        "insert into public.authoring_version "
        + "(id, org_id, project_id, entity_kind, entity_id, version_no, batch_id, change_kind, content, content_hash, source) "
        + "values (@id, @org_id, @project_id, 'feature_node', @entity_id, @version_no, @batch_id, 'create', '{}'::jsonb, @content_hash, 'manual')";

    private static (string Name, object Value)[] VersionParameters(Guid id, Guid org, Guid project, Guid entity, int versionNo) =>
    [
        ("id", id),
        ("org_id", org),
        ("project_id", project),
        ("entity_id", entity),
        ("version_no", versionNo),
        ("batch_id", Guid.CreateVersion7()),
        ("content_hash", ContentHash),
    ];

    /// <summary>An org and a project in it, written as the migration role.</summary>
    private async Task<(Guid Org, Guid Project)> SeedTenantAsync(string database, string orgSlug, string projectSlug)
    {
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();

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
        await seed.CommitAsync();

        return (org, project);
    }

    /// <summary>Runs one statement in a fresh transaction for <paramref name="connectionString"/> and returns the server error.</summary>
    private static async Task<PostgresException> CaptureErrorAsync(string connectionString, Guid org, string sql)
    {
        await using var scope = await TenantScope.BeginAsync(connectionString, org);

        return await scope.CaptureErrorAsync(sql);
    }
}
