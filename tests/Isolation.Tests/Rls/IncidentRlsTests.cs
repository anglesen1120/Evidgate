using System.Globalization;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Isolation.Tests;

[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class IncidentRlsTests(DatabaseFixture fixture) : HarnessTest(fixture)
{
    [Fact]
    public async Task cross_tenant_incident_invisible()
    {
        var org = Guid.CreateVersion7();
        await using var scope = await TenantScope.BeginAsync(Fixture.AppConnectionString, org);
        var attempt = await SeedAsync(scope, "public.incident_attempt", org);
        const string count = """
            select (select count(*) from public.incident where id = @incident) +
                   (select count(*) from public.incident_attempt where id = @attempt)
            """;
        var keys = new[] { ("incident", attempt.Values["incident_id"]), ("attempt", attempt.Values["id"]) };
        (await scope.ScalarAsync<long>(count, keys)).ShouldBe(2);
        await scope.SetAsync("evidgate.org_id", Guid.CreateVersion7().ToString("D", CultureInfo.InvariantCulture));
        (await scope.ScalarAsync<long>(count, keys)).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task incident_linkage_update_rejected(bool asOwner)
    {
        var org = Guid.CreateVersion7();
        await using var scope = await TenantScope.BeginAsync(asOwner ? Fixture.OwnerConnectionString : Fixture.AppConnectionString, org);
        var row = await SeedAsync(scope, "public.incident", org);
        var key = ("id", row.Values["id"]);
        (await scope.ScalarAsync<int>("update public.incident set state = 'triaged' where id = @id returning version", key)).ShouldBe(2);

        foreach (var assignment in new[]
        {
            "id = gen_random_uuid()", "org_id = gen_random_uuid()", "project_id = gen_random_uuid()", "run_id = gen_random_uuid()", "attempt_id = gen_random_uuid()",
            "case_id = gen_random_uuid()", "case_revision_id = gen_random_uuid()", "execution_key = repeat('f', 64)",
            "engine = 'other-engine'", "created_at = created_at + interval '1 second'",
        })
        {
            await scope.ExecuteAsync("savepoint before_update");
            var error = await scope.CaptureErrorAsync($"update public.incident set {assignment} where id = @id", key);
            error.SqlState.ShouldBe(asOwner ? PostgresErrorCodes.CheckViolation : PostgresErrorCodes.InsufficientPrivilege, assignment);
            if (asOwner)
            {
                error.MessageText.Contains("linkage", StringComparison.Ordinal).ShouldBeTrue(assignment);
            }

            await scope.ExecuteAsync("rollback to savepoint before_update");
            await scope.ExecuteAsync("release savepoint before_update");
        }
    }

    [Theory]
    [InlineData("partial", "complete")]
    [InlineData("complete", "partial")]
    public async Task incident_states_only_advance_along_declared_edges(string settled, string other)
    {
        var org = Guid.CreateVersion7();
        await using var scope = await TenantScope.BeginAsync(Fixture.AppConnectionString, org);
        var row = await SeedAsync(scope, "public.incident", org);
        var id = ("id", row.Values["id"]);
        (await scope.ScalarAsync<bool>("select state = 'open' and context_state = 'pending' and version = 1 from public.incident where id = @id", id)).ShouldBeTrue();
        const string state = "update public.incident set state = @to where id = @id returning version";
        const string context = "update public.incident set context_state = @to where id = @id returning version";
        await RejectTransitionAsync(scope, state, id, "closed");
        (await scope.ScalarAsync<int>(state, id, ("to", "triaged"))).ShouldBe(2);
        await RejectTransitionAsync(scope, state, id, "open");
        (await scope.ScalarAsync<int>(context, id, ("to", settled))).ShouldBe(3);
        await RejectTransitionAsync(scope, context, id, other);
        await RejectTransitionAsync(scope, context, id, "pending");
        (await scope.ScalarAsync<int>(state, id, ("to", "closed"))).ShouldBe(4);
        await RejectTransitionAsync(scope, state, id, "triaged");
    }

    [Theory]
    [InlineData("triaged", "pending", 1)]
    [InlineData("open", "partial", 1)]
    [InlineData("open", "pending", 2)]
    public async Task incident_insert_requires_initial_states_and_version(string state, string context, int version)
    {
        var org = Guid.CreateVersion7();
        await using var scope = await TenantScope.BeginAsync(Fixture.AppConnectionString, org);
        var source = await SeedAsync(scope, "public.incident", org);
        const string insert = """
            insert into public.incident (id, org_id, project_id, run_id, attempt_id, case_id, case_revision_id,
                                         execution_key, engine, state, context_state, version)
            select @new_id, org_id, project_id, run_id, attempt_id, case_id, case_revision_id,
                   @execution, engine, @state, @context, @version from public.incident where id = @source
            """;
        var key = ("source", source.Values["id"]);
        var execution = ("execution", (object)new string('a', 64));
        await scope.ExecuteAsync("savepoint initial_insert");
        var error = await scope.CaptureErrorAsync(insert, key, execution, ("new_id", Guid.CreateVersion7()),
            ("state", state), ("context", context), ("version", version));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        await scope.ExecuteAsync("rollback to savepoint initial_insert");
        (await scope.ExecuteAsync(insert, key, execution, ("new_id", Guid.CreateVersion7()),
            ("state", "open"), ("context", "pending"), ("version", 1))).ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task incident_attempt_is_append_only(bool asOwner)
    {
        var org = Guid.CreateVersion7();
        await using var scope = await TenantScope.BeginAsync(asOwner ? Fixture.OwnerConnectionString : Fixture.AppConnectionString, org);
        var attempt = await SeedAsync(scope, "public.incident_attempt", org);
        var id = ("id", attempt.Values["id"]);
        (await scope.ScalarAsync<bool>("""
            select created_at is not null and not (to_jsonb(a) ?| array['updated_at', 'version'])
            from public.incident_attempt a where id = @id
            """, id)).ShouldBeTrue();
        foreach (var sql in new[] { "update public.incident_attempt set result = 'passed' where id = @id", "delete from public.incident_attempt where id = @id" })
        {
            await scope.ExecuteAsync("savepoint before_mutation");
            var error = await scope.CaptureErrorAsync(sql, id);
            error.SqlState.ShouldBe(asOwner ? PostgresErrorCodes.CheckViolation : PostgresErrorCodes.InsufficientPrivilege);
            await scope.ExecuteAsync("rollback to savepoint before_mutation");
            await scope.ExecuteAsync("release savepoint before_mutation");
        }
    }

    [Fact]
    public async Task incident_run_execution_key_duplicate_rejected()
    {
        var org = Guid.CreateVersion7();
        await using var scope = await TenantScope.BeginAsync(Fixture.AppConnectionString, org);
        var source = await SeedAsync(scope, "public.incident", org);
        const string insert = """
            insert into public.incident (id, org_id, project_id, run_id, attempt_id, case_id, case_revision_id, execution_key, engine)
            select @new_id, org_id, project_id, run_id, attempt_id, case_id, case_revision_id, @execution, engine
              from public.incident where id = @source
            """;
        var key = ("source", source.Values["id"]);
        var execution = (string)source.Values["execution_key"];
        (await scope.ExecuteAsync(insert, key, ("new_id", Guid.CreateVersion7()), ("execution", new string('b', 64)))).ShouldBe(1);
        var error = await scope.CaptureErrorAsync(insert, key, ("new_id", Guid.CreateVersion7()), ("execution", execution));
        error.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        error.ConstraintName.ShouldBe("incident_run_execution_key");
    }

    [Fact]
    public async Task incident_tables_grant_nothing_to_workers_and_operators()
    {
        await using var scope = await TenantScope.BeginAsync(Fixture.OwnerConnectionString, Guid.CreateVersion7());
        const string privileges = """
            select count(*) from unnest(array['evidgate_worker', 'evidgate_worker_sched', 'evidgate_platform_operator', 'evidgate_operator']) as role_name,
                                 unnest(array['public.incident', 'public.incident_attempt']) as table_name,
                                 unnest(array['select', 'insert', 'update', 'references', 'delete', 'truncate', 'trigger']) as privilege
             where has_table_privilege(role_name, table_name, privilege)
                or (privilege in ('select', 'insert', 'update', 'references')
                    and has_any_column_privilege(role_name, table_name, privilege))
            """;
        (await scope.ScalarAsync<long>(privileges)).ShouldBe(0);
        (await scope.ScalarAsync<bool>("select has_table_privilege('evidgate_app', 'public.incident', 'select') and has_table_privilege('evidgate_app', 'public.incident_attempt', 'insert')")).ShouldBeTrue();
        (await scope.ScalarAsync<bool>("select has_any_column_privilege('evidgate_app', 'public.incident_attempt', 'update') or has_table_privilege('evidgate_app', 'public.incident_attempt', 'delete')")).ShouldBeFalse();
    }

    private static async Task RejectTransitionAsync(TenantScope scope, string sql, (string, object) id, string target)
    {
        await scope.ExecuteAsync("savepoint before_transition");
        (await scope.CaptureErrorAsync(sql, id, ("to", target))).SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        await scope.ExecuteAsync("rollback to savepoint before_transition");
        await scope.ExecuteAsync("release savepoint before_transition");
    }

    private async Task<GeneratedRow> SeedAsync(TenantScope scope, string table, Guid org)
    {
        await using var connection = await Fixture.AppDataSource.OpenConnectionAsync(Token);
        var schema = await TenantSchema.ReadAsync(connection, null, Token);
        var factory = new TenantRowFactory(schema, (sql, parameters) => scope.ExecuteAsync(sql, parameters));
        return await factory.CreateAsync(table, org, Token);
    }
}
