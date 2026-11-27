using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Ingest-token schema and pre-tenant resolver rules (migrations 0018 and 0134): the resolver derives the token
/// status without a tenant, and the retired GitHub exchange objects are gone while the shared permission stays.
/// </summary>
[Trait("Category", "Docker")]
public sealed class CiResolverTests
{
    private readonly PostgresTenancyFixture _fixture;

    public CiResolverTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ResolveIngestToken_ReportsTheDerivedStatus_AndNothingForAnUnknownPrefix()
    {
        // Dedicated database: the token resolver answers across all orgs of the database.
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var (org, project) = await SeedOrgAsync(database, "alpha");

        var active = await SeedTokenAsync(database, org, project, "AAAAAAAAAAAA", "");
        await SeedTokenAsync(database, org, project, "BBBBBBBBBBBB", ", revoked_at = now()");
        await SeedTokenAsync(database, org, project, "CCCCCCCCCCCC", ", expires_at = now() - interval '1 minute'");
        await SeedTokenAsync(database, org, project, "DDDDDDDDDDDD", ", overlap_ends_at = now() - interval '1 minute'");
        await SeedTokenAsync(database, org, project, "EEEEEEEEEEEE", ", overlap_ends_at = now() + interval '1 hour'");

        (await ResolveTokenAsync(database, "AAAAAAAAAAAA")).ShouldBe((org, project, active, "active"));
        (await ResolveTokenAsync(database, "BBBBBBBBBBBB")).Status.ShouldBe("revoked");
        (await ResolveTokenAsync(database, "CCCCCCCCCCCC")).Status.ShouldBe("expired");
        (await ResolveTokenAsync(database, "DDDDDDDDDDDD")).Status.ShouldBe("expired");
        (await ResolveTokenAsync(database, "EEEEEEEEEEEE")).Status.ShouldBe("active");
        (await ResolveTokenRawAsync(database, "ZZZZZZZZZZZZ")).ShouldBeEmpty();
    }

    [Fact]
    public async Task TheAppRole_CannotReadTheIngestTokensWithoutATenant_AndCannotDeleteThem()
    {
        // Dedicated database: it reads the platform grant state of the table.
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var (org, project) = await SeedOrgAsync(database, "alpha");
        await SeedTokenAsync(database, org, project, "AAAAAAAAAAAA", "");

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var count = new NpgsqlCommand("select count(*) from public.ingest_token", connection);
        ((long)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe(0);

        (await _fixture.ScalarAsync<bool>(database, "select has_table_privilege('evidgate_app', 'public.ingest_token', 'delete')")).ShouldBeFalse();
        (await _fixture.ScalarAsync<bool>(database, "select has_table_privilege('evidgate_app', 'public.ingest_token', 'update')")).ShouldBeTrue();
    }

    [Fact]
    public async Task TheRetiredGithubExchangeObjectsAndPermission_DoNotExist_AndIngestPermissionSurvives()
    {
        var database = await _fixture.SharedDatabaseAsync();

        foreach (var relation in new[] { "public.ci_binding", "public.ci_token_issue", "public.ci_token_issue_run_idx" })
        {
            (await _fixture.ScalarAsync<bool>(database, $"select to_regclass('{relation}') is null")).ShouldBeTrue(relation);
        }

        foreach (var function in new[] { "evidgate_resolve.resolve_ci_binding(text, bigint, bytea[])", "evidgate.purge_expired_ci_token_issue(integer)" })
        {
            (await _fixture.ScalarAsync<bool>(database, $"select to_regprocedure('{function}') is null")).ShouldBeTrue(function);
        }

        (await _fixture.ScalarAsync<long>(
            database, "select count(*) from public.permission where code = 'ci_binding:write'")).ShouldBe(0);
        (await _fixture.ScalarAsync<long>(
            database, "select count(*) from public.role_permission where permission_code = 'ci_binding:write'")).ShouldBe(0);

        // The positive control: ingest-token administration remains catalogued and granted.
        (await _fixture.ScalarAsync<long>(
            database, "select count(*) from public.permission where code = 'ingest_token:write'")).ShouldBe(1);
        (await _fixture.ScalarAsync<long>(
            database,
            "select count(*) from public.role_permission rp join public.role r on r.id = rp.role_id "
            + "where r.org_id is null and rp.permission_code = 'ingest_token:write'")).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task IngestToken_CannotBeBothRotatedAndRevoked_AndRejectsBadShapes()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var (org, project) = await SeedOrgAsync(database, "alpha");
        var prefix = TokenPrefix();
        var first = await SeedTokenAsync(database, org, project, prefix, "");
        var second = await SeedTokenAsync(database, org, project, TokenPrefix(), "");

        var both = await Should.ThrowAsync<PostgresException>(() => _fixture.ExecuteAsync(
            database, $"update public.ingest_token set rotated_to_id = '{second}', revoked_at = now() where id = '{first}'"));
        both.ConstraintName.ShouldBe("ingest_token_rotated_or_revoked_check");

        (await Should.ThrowAsync<PostgresException>(() => SeedTokenAsync(database, org, project, "short", ""))).ConstraintName
            .ShouldBe("ingest_token_prefix_check");
        (await Should.ThrowAsync<PostgresException>(() => SeedTokenAsync(database, org, project, prefix, ""))).ConstraintName
            .ShouldBe("ingest_token_prefix_key");
    }

    /// <summary>A prefix no other test of the shared database can hold: the column's uniqueness is platform-wide.</summary>
    private static string TokenPrefix() => Guid.NewGuid().ToString("N")[..12];

    private async Task<(Guid Org, Guid Project)> SeedOrgAsync(string database, string slug)
    {
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var unique = Unique.Slug(slug);
        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.organization (id, name, slug) values ('{org}', '{unique}', '{unique}');
            insert into public.project (id, org_id, name, slug) values ('{project}', '{org}', 'pp', 'pp');
            """);

        return (org, project);
    }

    private async Task<Guid> SeedTokenAsync(string database, Guid org, Guid project, string prefix, string extraSet)
    {
        var id = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.ingest_token (id, org_id, project_id, name, prefix, token_hmac, pepper_version, scopes)
            values ('{id}', '{org}', '{project}', 't', '{prefix}', decode(repeat('00', 32), 'hex'), 1, array['run:create']);
            update public.ingest_token set name = name {extraSet} where id = '{id}';
            """);

        return id;
    }

    private async Task<(Guid? OrgId, Guid? ProjectId, Guid? SubjectId, string Status)> ResolveTokenAsync(string database, string prefix) =>
        (await ResolveTokenRawAsync(database, prefix)).ShouldHaveSingleItem();

    private async Task<List<(Guid? OrgId, Guid? ProjectId, Guid? SubjectId, string Status)>> ResolveTokenRawAsync(string database, string prefix)
    {
        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "select org_id, project_id, binding_id, status from evidgate_resolve.resolve_ingest_token(@prefix)", connection);
        command.Parameters.AddWithValue("prefix", prefix);

        return await ReadAsync(command);
    }

    private static async Task<List<(Guid? OrgId, Guid? ProjectId, Guid? SubjectId, string Status)>> ReadAsync(NpgsqlCommand command)
    {
        var rows = new List<(Guid?, Guid?, Guid?, string)>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add((reader[0] as Guid?, reader[1] as Guid?, reader[2] as Guid?, reader.GetString(3)));
        }

        return rows;
    }
}
