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
        var attempt = (await SeedAsync(scope, "public.case_attempt", org)).Values["id"];
        await scope.ExecuteAsync("savepoint initial_insert");
        var error = await scope.CaptureErrorAsync(InsertFromAttempt, Link(attempt, state: state, context: context, version: version));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        await scope.ExecuteAsync("rollback to savepoint initial_insert");
        (await scope.ExecuteAsync(InsertFromAttempt, Link(attempt))).ShouldBe(1);
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
        var attempt = (await SeedAsync(scope, "public.case_attempt", org)).Values["id"];
        (await scope.ExecuteAsync(InsertFromAttempt, Link(attempt))).ShouldBe(1);
        var error = await scope.CaptureErrorAsync(InsertFromAttempt, Link(attempt));
        error.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        error.ConstraintName.ShouldBe("incident_run_execution_key");
    }

    // Each variant names a sibling that exists in the same org, so every plain key but the run's passes: the refusal
    // is the same-project key or the linkage guard, not a missing parent. The bad insert runs first and the untouched
    // statement then succeeds on the same attempt, so the override is the only difference.
    [Theory]
    [InlineData("project", "incident_run_fk", "")]
    [InlineData("case", "incident_linkage_guard", "case and revision")]
    [InlineData("revision", "incident_linkage_guard", "case and revision")]
    [InlineData("execution", "incident_linkage_guard", "run and execution key")]
    public async Task incident_linkage_must_name_one_project_run_case_and_execution(string variant, string constraint, string message)
    {
        var org = Guid.CreateVersion7();
        await using var scope = await TenantScope.BeginAsync(Fixture.AppConnectionString, org);
        var factory = await FactoryAsync(scope);
        var attempt = (await factory.CreateAsync("public.case_attempt", org, Token)).Values["id"];
        var link = variant switch
        {
            "project" => Link(attempt, project: await SiblingAsync(factory, "public.project", org)),
            "case" => Link(attempt, caseId: await SiblingAsync(factory, "public.test_case", org)),
            "revision" => Link(attempt, revision: await SiblingRevisionAsync(scope, attempt)),
            _ => Link(attempt, execution: new string('c', 64)),
        };
        await scope.ExecuteAsync("savepoint bad_link");
        var error = await scope.CaptureErrorAsync(InsertFromAttempt, link);
        error.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        error.ConstraintName.ShouldBe(constraint);
        error.MessageText.ShouldContain(message);
        await scope.ExecuteAsync("rollback to savepoint bad_link");
        (await scope.ExecuteAsync(InsertFromAttempt, Link(attempt))).ShouldBe(1);
    }

    [Fact]
    public async Task incident_attempt_must_repeat_its_attempts_execution_case_number_and_result()
    {
        var org = Guid.CreateVersion7();
        await using var scope = await TenantScope.BeginAsync(Fixture.AppConnectionString, org);
        var factory = await FactoryAsync(scope);
        var incident = (await factory.CreateAsync("public.incident", org, Token)).Values;
        var elsewhere = await factory.CreateBesideAsync("public.planned_execution", org, Token);
        var foreignAttempt = Guid.CreateVersion7();
        await scope.ExecuteAsync("""
            insert into public.case_attempt (id, org_id, run_id, execution_key, case_id, attempt_no, raw_status, normalized_status)
            select @id, org_id, @run, @key, case_id, 1, 'passed', 'passed' from public.case_attempt where id = @source
            """, ("id", foreignAttempt), ("run", elsewhere.Values["run_id"]), ("key", elsewhere.Values["execution_key"]),
            ("source", incident["attempt_id"]));
        const string insert = """
            insert into public.incident_attempt (id, org_id, incident_id, attempt_id, attempt_no, result)
            values (@new_id, @org, @incident, @attempt, @number, @result)
            """;
        var number = await RetryAsync(scope, incident["attempt_id"], 1);
        var result = await RetryAsync(scope, incident["attempt_id"], 2);
        var good = await RetryAsync(scope, incident["attempt_id"], 3);
        // Same run, key, number and result as the incident's execution, but another case of the org.
        var otherCase = await RetryAsync(scope, incident["attempt_id"], 4, await SiblingAsync(factory, "public.test_case", org));
        var refused = new[]
        {
            Retry(org, incident, number.Id, number.Number + 1, "error"),
            Retry(org, incident, result.Id, result.Number, "failed"),
            Retry(org, incident, foreignAttempt, 1, "passed"),
            Retry(org, incident, otherCase.Id, otherCase.Number, "error"),
        };
        foreach (var parameters in refused)
        {
            await scope.ExecuteAsync("savepoint before_retry");
            var error = await scope.CaptureErrorAsync(insert, parameters);
            error.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
            error.ConstraintName.ShouldBe("incident_attempt_linkage_guard");
            await scope.ExecuteAsync("rollback to savepoint before_retry");
            await scope.ExecuteAsync("release savepoint before_retry");
        }

        (await scope.ExecuteAsync(insert, Retry(org, incident, good.Id, good.Number, "error"))).ShouldBe(1);
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

    // An incident built from one case_attempt: its run, case, revision, project and key all come from the attempt's
    // own rows, so a variant changes exactly the one column it overrides.
    private const string InsertFromAttempt = """
        insert into public.incident (id, org_id, project_id, run_id, attempt_id, case_id, case_revision_id,
                                     execution_key, engine, state, context_state, version)
        select @new_id, a.org_id, coalesce(@project::uuid, r.project_id), a.run_id, a.id,
               coalesce(@case_id::uuid, a.case_id), coalesce(@revision::uuid, p.case_revision_id),
               coalesce(@execution::text, a.execution_key), 'playwright', @state, @context, @version
          from public.case_attempt a
          join public.run r on r.org_id = a.org_id and r.id = a.run_id
          join public.planned_execution p on p.org_id = a.org_id and p.run_id = a.run_id and p.execution_key = a.execution_key
         where a.id = @attempt
        """;

    private static (string, object)[] Link(
        object attempt,
        Guid? project = null,
        Guid? caseId = null,
        Guid? revision = null,
        string? execution = null,
        string state = "open",
        string context = "pending",
        int version = 1) =>
    [
        ("attempt", attempt),
        ("new_id", Guid.CreateVersion7()),
        ("project", (object?)project ?? DBNull.Value),
        ("case_id", (object?)caseId ?? DBNull.Value),
        ("revision", (object?)revision ?? DBNull.Value),
        ("execution", (object?)execution ?? DBNull.Value),
        ("state", state),
        ("context", context),
        ("version", version),
    ];

    private static (string, object)[] Retry(Guid org, IReadOnlyDictionary<string, object> incident, Guid attempt, int number, string result) =>
    [
        ("new_id", Guid.CreateVersion7()),
        ("org", org),
        ("incident", incident["id"]),
        ("attempt", attempt),
        ("number", number),
        ("result", result),
    ];

    // A later attempt of the incident's own execution: same run, key and case, the next attempt number.
    private static async Task<(Guid Id, int Number)> RetryAsync(TenantScope scope, object source, int offset, Guid? caseId = null)
    {
        var id = Guid.CreateVersion7();
        await scope.ExecuteAsync("""
            insert into public.case_attempt (id, org_id, run_id, execution_key, case_id, attempt_no, raw_status, normalized_status)
            select @id, org_id, run_id, execution_key, coalesce(@case_id::uuid, case_id), attempt_no + @offset, 'error', 'error'
              from public.case_attempt where id = @source
            """, ("id", id), ("source", source), ("offset", offset), ("case_id", (object?)caseId ?? DBNull.Value));
        return (id, await scope.ScalarAsync<int>("select attempt_no from public.case_attempt where id = @id", ("id", id)));
    }

    private static async Task RejectTransitionAsync(TenantScope scope, string sql, (string, object) id, string target)
    {
        await scope.ExecuteAsync("savepoint before_transition");
        (await scope.CaptureErrorAsync(sql, id, ("to", target))).SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        await scope.ExecuteAsync("rollback to savepoint before_transition");
        await scope.ExecuteAsync("release savepoint before_transition");
    }

    private async Task<TenantRowFactory> FactoryAsync(TenantScope scope)
    {
        await using var connection = await Fixture.AppDataSource.OpenConnectionAsync(Token);
        var schema = await TenantSchema.ReadAsync(connection, null, Token);
        return new TenantRowFactory(schema, (sql, parameters) => scope.ExecuteAsync(sql, parameters));
    }

    private async Task<GeneratedRow> SeedAsync(TenantScope scope, string table, Guid org) =>
        await (await FactoryAsync(scope)).CreateAsync(table, org, Token);

    private static async Task<Guid> SiblingAsync(TenantRowFactory factory, string table, Guid org) =>
        (Guid)(await factory.CreateBesideAsync(table, org, Token)).Values["id"];

    // The factory writes one fixed hash per column, so a second revision of the attempt's case from it would repeat
    // (org_id, case_id, revision_hash); this one is the same case with other hashes, so only the revision id differs.
    private static Task<Guid> SiblingRevisionAsync(TenantScope scope, object attempt) =>
        scope.ScalarAsync<Guid>("""
            insert into public.test_revision
                (id, org_id, case_id, revision_hash, commit_sha, file_path, framework, content_sha256, assertion_hash)
            select gen_random_uuid(), org_id, case_id, 'sha256:' || repeat('b', 64), 'bbbbbbb',
                   'sibling.spec.ts', 'playwright', 'sha256:' || repeat('c', 64), 'sha256:' || repeat('a', 64)
              from public.case_attempt where id = @attempt
            returning id
            """, ("attempt", attempt));
}
