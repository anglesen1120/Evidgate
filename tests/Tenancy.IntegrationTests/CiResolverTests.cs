using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 02.4.1 / 02.4.2 schema and pre-tenant resolver rules (migrations 0016-0018, TL T1 and T70): at most one org holds
/// the active binding of a repository, the resolver prefers it, a pending binding is named only when its challenge
/// is proven, and an unproven one leaks no tenant.
/// </summary>
[Trait("Category", "Docker")]
public sealed class CiResolverTests
{
    private static readonly byte[] HashA = Enumerable.Repeat((byte)0xA1, 32).ToArray();
    private static readonly byte[] HashB = Enumerable.Repeat((byte)0xB2, 32).ToArray();

    private readonly PostgresTenancyFixture _fixture;

    public CiResolverTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ActiveBinding_IsUniquePerRepository_PlatformWide()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var (orgA, projectA) = await SeedOrgAsync(database, "alpha");
        var (orgB, projectB) = await SeedOrgAsync(database, "bravo");

        await SeedBindingAsync(database, orgA, projectA, 7, "active");
        // A pending binding by another org is fine; a second ACTIVE one is not (T70).
        await SeedBindingAsync(database, orgB, projectB, 7, "pending_verification", HashB);
        var activate = Should.ThrowAsync<PostgresException>(() =>
            _fixture.ExecuteAsync(database, $"update public.ci_binding set status = 'active' where org_id = '{orgB}'"));
        (await activate).ConstraintName.ShouldBe("ci_binding_active_repository_key");

        // One binding per (project, repository).
        var duplicate = await Should.ThrowAsync<PostgresException>(() => SeedBindingAsync(database, orgA, projectA, 7, "disabled"));
        duplicate.ConstraintName.ShouldBe("ci_binding_project_repository_key");
    }

    [Fact]
    public async Task ResolveBinding_PrefersActive_NamesPendingOnlyWhenProven_AndLeaksNoTenantOtherwise()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var (orgA, projectA) = await SeedOrgAsync(database, "alpha");
        var (orgB, projectB) = await SeedOrgAsync(database, "bravo");
        var pending = await SeedBindingAsync(database, orgA, projectA, 11, "pending_verification", HashA);
        await SeedBindingAsync(database, orgB, projectB, 11, "pending_verification", HashB);

        // Nothing proven: one row, no tenant.
        var unproven = await ResolveAsync(database, 11, null);
        unproven.ShouldBe((null, null, null, "pending_unproven"));
        (await ResolveAsync(database, 11, [new byte[32]])).ShouldBe((null, null, null, "pending_unproven"));

        // A challenge proves exactly one of the two pending bindings.
        (await ResolveAsync(database, 11, [HashA])).ShouldBe((orgA, projectA, pending, "pending_verification"));
        (await ResolveAsync(database, 11, [HashB])).Status.ShouldBe("pending_verification");
        (await ResolveAsync(database, 11, [HashB])).OrgId.ShouldBe(orgB);

        // Once one is active it wins over any proof of the other.
        await _fixture.ExecuteAsync(database, $"update public.ci_binding set status = 'active', binding_challenge_hash = null where id = '{pending}'");
        (await ResolveAsync(database, 11, [HashB])).ShouldBe((orgA, projectA, pending, "active"));
        (await ResolveAsync(database, 11, null)).Status.ShouldBe("active");

        // Unbound repository: no row at all.
        (await ResolveRawAsync(database, 99, null)).ShouldBeEmpty();

        // A disabled binding is reported as such.
        await _fixture.ExecuteAsync(database, $"update public.ci_binding set status = 'disabled' where id = '{pending}'");
        (await ResolveAsync(database, 11, null)).Status.ShouldBe("disabled");
        await _fixture.ExecuteAsync(database, $"update public.ci_binding set status = 'disabled' where org_id = '{orgB}'");
        (await ResolveAsync(database, 11, null)).Status.ShouldBe("disabled");
    }

    [Fact]
    public async Task ResolveIngestToken_ReportsTheDerivedStatus_AndNothingForAnUnknownPrefix()
    {
        var database = await _fixture.CreateDatabaseAsync();
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
    public async Task TheAppRole_CannotReadTheCiTablesWithoutATenant_AndCannotEditIssuedRecords()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var (org, project) = await SeedOrgAsync(database, "alpha");
        await SeedBindingAsync(database, org, project, 5, "active");
        await SeedTokenAsync(database, org, project, "AAAAAAAAAAAA", "");

        foreach (var table in new[] { "ci_binding", "ingest_token", "ci_token_issue" })
        {
            await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(database));
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var count = new NpgsqlCommand($"select count(*) from public.{table}", connection);
            ((long)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe(0, table);
        }

        (await _fixture.ScalarAsync<bool>(database, "select has_table_privilege('evidgate_app', 'public.ci_token_issue', 'update')")).ShouldBeFalse();
        (await _fixture.ScalarAsync<bool>(database, "select has_table_privilege('evidgate_app', 'public.ci_token_issue', 'delete')")).ShouldBeFalse();
        (await _fixture.ScalarAsync<bool>(database, "select has_table_privilege('evidgate_app', 'public.ci_binding', 'delete')")).ShouldBeFalse();
        (await _fixture.ScalarAsync<bool>(database, "select has_table_privilege('evidgate_app', 'public.ingest_token', 'delete')")).ShouldBeFalse();
    }

    [Fact]
    public async Task IngestToken_CannotBeBothRotatedAndRevoked_AndRejectsBadShapes()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var (org, project) = await SeedOrgAsync(database, "alpha");
        var first = await SeedTokenAsync(database, org, project, "AAAAAAAAAAAA", "");
        var second = await SeedTokenAsync(database, org, project, "BBBBBBBBBBBB", "");

        var both = await Should.ThrowAsync<PostgresException>(() => _fixture.ExecuteAsync(
            database, $"update public.ingest_token set rotated_to_id = '{second}', revoked_at = now() where id = '{first}'"));
        both.ConstraintName.ShouldBe("ingest_token_rotated_or_revoked_check");

        (await Should.ThrowAsync<PostgresException>(() => SeedTokenAsync(database, org, project, "short", ""))).ConstraintName
            .ShouldBe("ingest_token_prefix_check");
        (await Should.ThrowAsync<PostgresException>(() => SeedTokenAsync(database, org, project, "AAAAAAAAAAAA", ""))).ConstraintName
            .ShouldBe("ingest_token_prefix_key");
    }

    private async Task<(Guid Org, Guid Project)> SeedOrgAsync(string database, string slug)
    {
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.organization (id, name, slug) values ('{org}', '{slug}', '{slug}');
            insert into public.project (id, org_id, name, slug) values ('{project}', '{org}', 'pp', 'pp');
            """);

        return (org, project);
    }

    private async Task<Guid> SeedBindingAsync(string database, Guid org, Guid project, long repository, string status, byte[]? hash = null)
    {
        var id = Guid.CreateVersion7();
        var hashSql = hash is null ? "null" : $"decode('{Convert.ToHexStringLower(hash)}', 'hex')";
        await _fixture.ExecuteAsync(
            database,
            $"""
            insert into public.ci_binding (id, org_id, project_id, provider, repository_id, repository_full_name, allowed_ref_patterns,
                                           allowed_workflow_refs, status, binding_challenge_hash, challenge_pepper_version)
            values ('{id}', '{org}', '{project}', 'github_actions', {repository}, 'acme/web', array['refs/heads/main'],
                    array['acme/web/.github/workflows/ci.yml@refs/heads/main'], '{status}', {hashSql}, {(hash is null ? "null" : "1")})
            """);

        return id;
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

    private async Task<(Guid? OrgId, Guid? ProjectId, Guid? SubjectId, string Status)> ResolveAsync(string database, long repository, byte[][]? hashes)
    {
        var rows = await ResolveRawAsync(database, repository, hashes);

        return rows.ShouldHaveSingleItem();
    }

    private async Task<List<(Guid? OrgId, Guid? ProjectId, Guid? SubjectId, string Status)>> ResolveRawAsync(string database, long repository, byte[][]? hashes)
    {
        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "select org_id, project_id, binding_id, status from evidgate_resolve.resolve_ci_binding('github_actions', @repo, @hashes)", connection);
        command.Parameters.AddWithValue("repo", repository);
        command.Parameters.Add(new NpgsqlParameter("hashes", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bytea) { Value = hashes is null ? DBNull.Value : hashes });

        return await ReadAsync(command);
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
