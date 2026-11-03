using System.Net;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 02.7.1-T1/T2 through the real pipeline (RealResolver factory): every successful audited
/// command appends exactly one row to the org's hash chain in the same transaction (AC1),
/// the app role cannot rewrite history (AC2), concurrent writers leave one dense chain (AC3).
/// </summary>
[Trait("Category", "Docker")]
public sealed class AuditTrailTests
{
    private readonly PostgresTenancyFixture _fixture;

    public AuditTrailTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AuditWriter_Commit_WritesBoth_AndRecordsTheActor()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();

        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        var rows = await RowsAsync(database, org);
        rows.Select(row => (row.Seq, row.Action, row.ActorType, row.ActorId, row.ObjectType, row.ObjectId)).ShouldBe(
        [
            (1L, "organization.create", "user", user.ToString("D"), "organization", org.ToString("D")),
            (2L, "project.create", "user", user.ToString("D"), "project", project.ToString("D")),
        ]);
        rows.ShouldAllBe(row => row.RequestId != null && row.TraceId != null);

        // Both the business row and its audit row are there: one commit.
        (await _fixture.ScalarAsync<long>(database, $"select count(*) from public.project where id = '{project}'")).ShouldBe(1);

        (await VerifyAsync(api, org)).ShouldBeNull();
    }

    [Fact]
    public async Task AuditWriter_BusinessRollback_LeavesNoAuditRow()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        await CreateProjectAsync(client, user, org, "checkout");

        // A duplicate slug fails inside the handler: the project insert rolls back, and so
        // must the audit row that would have described it.
        var duplicate = await client.SendAsync(
            HttpMethod.Post,
            $"/v1/orgs/{org}/projects",
            TenancyApiFactory.Resolved(user: user),
            new { name = "Again", slug = "checkout" },
            NewKey());
        duplicate.Status.ShouldBe(HttpStatusCode.Conflict);

        // A denied caller never reaches the handler and leaves nothing either.
        var denied = await client.SendAsync(
            HttpMethod.Post,
            $"/v1/orgs/{org}/projects",
            TenancyApiFactory.Resolved(user: Guid.CreateVersion7()),
            new { name = "X", slug = "other" },
            NewKey());
        denied.Status.ShouldBe(HttpStatusCode.Forbidden);

        (await RowsAsync(database, org)).Select(row => row.Action).ShouldBe(["organization.create", "project.create"]);
    }

    [Fact]
    public async Task IdempotentReplay_DoesNotAuditTwice()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var key = NewKey();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var answer = await client.SendAsync(
                HttpMethod.Post,
                $"/v1/orgs/{org}/projects",
                TenancyApiFactory.Resolved(user: user),
                new { name = "Once", slug = "once" },
                key);
            answer.Status.ShouldBe(HttpStatusCode.Created);
        }

        (await RowsAsync(database, org)).Count(row => row.Action == "project.create").ShouldBe(1);
    }

    [Fact]
    public async Task EveryAuditedMutation_EmitsExactlyOneRow()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var founder = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, founder, "acme");
        var project = await CreateProjectAsync(client, founder, org, "checkout");
        var caller = TenancyApiFactory.Resolved(user: founder);

        (await client.SendAsync(HttpMethod.Patch, $"/v1/projects/{project}", TenancyApiFactory.Resolved(user: founder, orgHeader: org), new { name = "Renamed" }, NewKey(), "\"1\""))
            .Status.ShouldBe(HttpStatusCode.OK);
        (await client.SendAsync(HttpMethod.Patch, $"/v1/orgs/{org}", caller, new { name = "Acme Inc" }, NewKey(), "\"1\""))
            .Status.ShouldBe(HttpStatusCode.OK);

        var invited = await client.SendAsync(
            HttpMethod.Post, $"/v1/orgs/{org}/memberships", caller, new { email = "new@example.test", roles = Array.Empty<string>() }, NewKey());
        invited.Status.ShouldBe(HttpStatusCode.Created);
        var membership = invited.Id();

        var role = await _fixture.ScalarAsync<Guid>(database, "select id from public.role where code = 'release_approver' and org_id is null");
        (await client.SendAsync(HttpMethod.Post, $"/v1/orgs/{org}/memberships/{membership}/roles", caller, new { role_id = role }, NewKey()))
            .Status.ShouldBe(HttpStatusCode.OK);
        (await client.SendAsync(HttpMethod.Delete, $"/v1/orgs/{org}/memberships/{membership}/roles/{role}", caller, null, NewKey()))
            .Status.ShouldBe(HttpStatusCode.NoContent);

        (await RowsAsync(database, org)).Select(row => row.Action).ShouldBe(
        [
            "organization.create",
            "project.create",
            "project.update",
            "organization.update",
            "membership.invite",
            "membership.role_grant",
            "membership.role_revoke",
        ]);
        (await VerifyAsync(api, org)).ShouldBeNull();
    }

    [Fact]
    public async Task AuditChain_ConcurrentWriters_SingleUnbrokenSeq()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");

        const int Writers = 12;
        var answers = await Task.WhenAll(Enumerable.Range(0, Writers).Select(i => client.SendAsync(
            HttpMethod.Post,
            $"/v1/orgs/{org}/projects",
            TenancyApiFactory.Resolved(user: user),
            new { name = $"P{i}", slug = $"proj-{i}" },
            NewKey())));
        answers.ShouldAllBe(answer => answer.Status == HttpStatusCode.Created);

        var rows = await RowsAsync(database, org);
        rows.Select(row => row.Seq).ShouldBe(Enumerable.Range(1, Writers + 1).Select(i => (long)i));
        (await VerifyAsync(api, org)).ShouldBeNull();
    }

    [Fact]
    public async Task AppRole_CannotUpdateOrDelete_AuditLog()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var org = await CreateOrgAsync(client, Guid.CreateVersion7(), "acme");

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var set = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("org", org.ToString("D"));
            await set.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        foreach (var statement in new[]
                 {
                     "update public.audit_log set action = 'tampered'",
                     "delete from public.audit_log",
                     "truncate public.audit_log",
                 })
        {
            await using var command = new NpgsqlCommand("savepoint s", connection, transaction);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            await using var attack = new NpgsqlCommand(statement, connection, transaction);
            var failure = await Should.ThrowAsync<PostgresException>(() => attack.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            failure.SqlState.ShouldBe("42501", statement);
            await using var rollback = new NpgsqlCommand("rollback to savepoint s", connection, transaction);
            await rollback.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task AppRole_CanOnlyExtendTheChainHonestly()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var org = await CreateOrgAsync(client, Guid.CreateVersion7(), "acme");

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        foreach (var (seq, prev) in new[] { (99, "repeat('00', 32)"), (2, "repeat('ab', 32)"), (1, "repeat('00', 32)") })
        {
            await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await using (var set = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction))
            {
                set.Parameters.AddWithValue("org", org.ToString("D"));
                await set.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await using var forge = new NpgsqlCommand(
                $"""
                insert into public.audit_log (id, org_id, seq, actor_type, actor_id, action, prev_hash, entry_hash)
                values (gen_random_uuid(), '{org}', {seq}, 'user', 'forged', 'forged.action', decode({prev}, 'hex'), decode(repeat('ff', 32), 'hex'))
                """,
                connection,
                transaction);
            var failure = await Should.ThrowAsync<PostgresException>(() => forge.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
            failure.SqlState.ShouldBeOneOf("23514", "23505");
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        (await RowsAsync(database, org)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task OtherOrgsRows_AreInvisible()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var first = await CreateOrgAsync(client, Guid.CreateVersion7(), "first-org");
        var second = await CreateOrgAsync(client, Guid.CreateVersion7(), "second-org");

        await using var scopeFactory = api.Services.CreateAsyncScope();
        var scopes = scopeFactory.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        var db = scopeFactory.ServiceProvider.GetRequiredService<ITenantDb>();
        await using var scope = await scopes.BeginAsync(first, null, true, TestContext.Current.CancellationToken);
        var visible = await db.QueryAsync<Guid>("select org_id from public.audit_log", null, TestContext.Current.CancellationToken);

        visible.ShouldNotBeEmpty();
        visible.ShouldAllBe(id => id == first);
        visible.ShouldNotContain(second);
    }

    [Fact]
    public async Task Verifier_DetectsModifiedDeletedAndRelinkedRows()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        using var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        for (var i = 0; i < 4; i++)
        {
            await CreateProjectAsync(client, user, org, $"proj-{i}");
        }

        (await VerifyAsync(api, org)).ShouldBeNull();

        // Tamper as the superuser (the app role cannot): a modified row.
        await _fixture.ExecuteAsync(database, $"update public.audit_log set action = 'project.delete' where org_id = '{org}' and seq = 3");
        (await VerifyAsync(api, org)).ShouldBe(new AuditChainFinding(AuditChainFault.HashMismatch, 3));

        // Restore, then re-link a row to a different predecessor.
        await _fixture.ExecuteAsync(database, $"update public.audit_log set action = 'project.create' where org_id = '{org}' and seq = 3");
        (await VerifyAsync(api, org)).ShouldBeNull();
        await _fixture.ExecuteAsync(
            database, $"update public.audit_log set prev_hash = decode(repeat('ab', 32), 'hex') where org_id = '{org}' and seq = 4");
        (await VerifyAsync(api, org)).ShouldBe(new AuditChainFinding(AuditChainFault.BrokenLink, 4));
        await _fixture.ExecuteAsync(
            database,
            $"update public.audit_log set prev_hash = (select entry_hash from public.audit_log where org_id = '{org}' and seq = 3) where org_id = '{org}' and seq = 4");
        (await VerifyAsync(api, org)).ShouldBeNull();

        // A deleted middle row is a gap.
        await _fixture.ExecuteAsync(database, $"delete from public.audit_log where org_id = '{org}' and seq = 2");
        (await VerifyAsync(api, org)).ShouldBe(new AuditChainFinding(AuditChainFault.Gap, 2));
    }

    [Fact]
    public async Task ListQuery_UsesTheCoveringIndex()
    {
        var database = await _fixture.CreateDatabaseAsync();
        (await _fixture.ColumnAsync<string>(
            database,
            "select indexdef from pg_indexes where tablename = 'audit_log' and indexname = 'audit_log_org_created_id_idx'"))
            .ShouldHaveSingleItem()
            .ShouldContain("(org_id, created_at DESC, id DESC)");
    }

    private async Task<List<AuditRow>> RowsAsync(string database, Guid org)
    {
        await using var connection = await _fixture.OpenSuperuserAsync(database);
        await using var command = new NpgsqlCommand(AuditSqlForTests, connection);
        command.Parameters.AddWithValue("org", org);
        var rows = new List<AuditRow>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(new AuditRow(
                reader.GetGuid(0), org, reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader[5] as string, reader[6] as string,
                null, null, reader[7] as string, reader[8] as string,
                null, [], [], DateTimeOffset.UtcNow));
        }

        return rows;
    }

    private const string AuditSqlForTests =
        "select id, seq, actor_type, actor_id, action, object_type, object_id, request_id, trace_id from public.audit_log where org_id = @org order by seq";

    private static async Task<AuditChainFinding?> VerifyAsync(TenancyApiFactory api, Guid org)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var scopes = scope.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        var db = scope.ServiceProvider.GetRequiredService<ITenantDb>();
        await using var tenant = await scopes.BeginAsync(org, null, true, TestContext.Current.CancellationToken);

        return await new AuditChainVerifier(db).VerifyAsync(org, TestContext.Current.CancellationToken);
    }

    private static async Task<Guid> CreateOrgAsync(HttpClient client, Guid user, string slug)
    {
        var created = await client.SendAsync(
            HttpMethod.Post, "/v1/orgs", TenancyApiFactory.Resolved(user: user, email: $"{slug}@example.test"), new { name = slug, slug }, NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created);

        return created.Id();
    }

    private static async Task<Guid> CreateProjectAsync(HttpClient client, Guid user, Guid org, string slug)
    {
        var created = await client.SendAsync(
            HttpMethod.Post, $"/v1/orgs/{org}/projects", TenancyApiFactory.Resolved(user: user), new { name = slug, slug }, NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created);

        return created.Id();
    }
}
