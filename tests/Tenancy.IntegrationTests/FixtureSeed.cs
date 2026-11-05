// Rows the fixture schema and immutability tests share: one organisation and project as parents, then one
// fixture and one revision per call. Each write commits on its own, and a statement expected to be refused gets
// a scope of its own because a failed statement aborts the transaction it runs in.
using System.Globalization;
using Npgsql;
using Shouldly;

namespace Tenancy.IntegrationTests;

/// <summary>An organisation and one of its projects: the parents a fixture row's composite key names.</summary>
internal sealed record FixtureTenant(Guid Org, Guid Project);

internal static class FixtureSeed
{
    /// <summary>The key every seeded fixture holds unless a case asks for another one.</summary>
    internal const string DefaultKey = "orders-seed";

    /// <summary>A content hash of the shape the revision check accepts, varied by one hex digit.</summary>
    internal static string Hash(char digit) => "sha256:" + new string(digit, 64);

    internal static async Task<FixtureTenant> TenantAsync(PostgresTenancyFixture fixture, string database)
    {
        var org = Guid.CreateVersion7();
        var project = await ProjectAsync(fixture, database, org);

        return new FixtureTenant(org, project);
    }

    internal static async Task<Guid> ProjectAsync(PostgresTenancyFixture fixture, string database, Guid org)
    {
        var project = Guid.CreateVersion7();

        await using var scope = await TenantScope.BeginAsync(fixture.OwnerConnectionString(database));
        await scope.ExecuteAsync(
            "insert into public.organization (id, name, slug) values (@id, @name, @slug) on conflict (id) do nothing",
            ("id", org),
            ("name", Unique.Slug("fixture")),
            ("slug", Unique.Slug("fixture")));
        await scope.ExecuteAsync(
            "insert into public.project (id, org_id, name, slug) values (@id, @org, @name, @slug)",
            ("id", project),
            ("org", org),
            ("name", Unique.Slug("fixture")),
            ("slug", Unique.Slug("fixture")));
        await scope.CommitAsync();

        return project;
    }

    internal static async Task<Guid> FixtureAsync(
        PostgresTenancyFixture fixture,
        string database,
        Guid org,
        Guid project,
        string key = DefaultKey)
    {
        var id = Guid.CreateVersion7();

        await AcceptedAsync(fixture.OwnerConnectionString(database), null, FixtureRow(id, org, project, key));

        return id;
    }

    internal static async Task<Guid> RevisionAsync(
        PostgresTenancyFixture fixture,
        string database,
        Guid org,
        Guid project,
        Guid fixtureId,
        string key = DefaultKey,
        int revisionNo = 1,
        string? hash = null)
    {
        var id = Guid.CreateVersion7();

        await AcceptedAsync(
            fixture.OwnerConnectionString(database),
            null,
            RevisionRow(id, org, project, fixtureId, key, revisionNo, hash));

        return id;
    }

    internal static (string Sql, (string Name, object Value)[] Parameters) FixtureRow(
        Guid id,
        Guid org,
        Guid project,
        string key = DefaultKey) =>
        ("insert into public.fixture (id, org_id, project_id, fixture_key) values (@id, @org, @project, @key)",
            new (string Name, object Value)[]
            {
                ("id", id),
                ("org", org),
                ("project", project),
                ("key", key),
            });

    internal static (string Sql, (string Name, object Value)[] Parameters) RevisionRow(
        Guid id,
        Guid org,
        Guid project,
        Guid fixtureId,
        string key = DefaultKey,
        int revisionNo = 1,
        string? hash = null,
        int timeoutSeconds = 60,
        string cleanupPolicy = "drop",
        string? seedVersion = null,
        string? resourcePrefix = null,
        string? bodyRef = null) =>
        ("insert into public.fixture_revision "
            + "(id, org_id, fixture_id, project_id, fixture_key, revision_no, revision_hash, seed_version, "
            + "timeout_s, cleanup_policy, resource_prefix, body_ref) "
            + "values (@id, @org, @fixture, @project, @key, @revision_no, @hash, @seed_version, @timeout, "
            + "@cleanup, @prefix, @body_ref)",
            new (string Name, object Value)[]
            {
                ("id", id),
                ("org", org),
                ("fixture", fixtureId),
                ("project", project),
                ("key", key),
                ("revision_no", revisionNo),
                ("hash", hash ?? Hash('a')),
                ("seed_version", seedVersion ?? "v1"),
                ("timeout", timeoutSeconds),
                ("cleanup", cleanupPolicy),
                ("prefix", resourcePrefix ?? "fx_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..16]),
                ("body_ref", bodyRef ?? "seeds/orders.json"),
            });

    internal static (string Sql, (string Name, object Value)[] Parameters) Literal(string sql) =>
        (sql, Array.Empty<(string Name, object Value)>());

    /// <summary>Runs one statement in its own transaction and asserts the server refused it as named.</summary>
    internal static async Task<PostgresException> RefusedAsync(
        string connectionString,
        Guid? orgId,
        (string Sql, (string Name, object Value)[] Parameters) statement,
        string sqlState,
        string? constraintName = null)
    {
        await using var scope = await TenantScope.BeginAsync(connectionString, orgId);
        var error = await scope.CaptureErrorAsync(statement.Sql, statement.Parameters);

        error.SqlState.ShouldBe(sqlState, statement.Sql);
        if (constraintName is not null)
        {
            error.ConstraintName.ShouldBe(constraintName, statement.Sql);
        }

        return error;
    }

    /// <summary>Runs one statement that must write exactly one row, in its own committed transaction.</summary>
    internal static async Task AcceptedAsync(
        string connectionString,
        Guid? orgId,
        (string Sql, (string Name, object Value)[] Parameters) statement)
    {
        await using var scope = await TenantScope.BeginAsync(connectionString, orgId);

        (await scope.ExecuteAsync(statement.Sql, statement.Parameters)).ShouldBe(1, statement.Sql);
        await scope.CommitAsync();
    }
}
