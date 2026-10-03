using Evidgate.BuildingBlocks.Persistence.Isolation;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// AC3 / FR-02-11 / 00 §5.4.7 (TL T2): a tenant table read with no tenant context
/// returns zero rows — never an error and never every row. The helper returns NULL
/// for both the unset and the empty setting, which is what makes a missing
/// <c>set_config</c> a fail-closed read instead of an open one, and the seeded rows
/// are proven visible under a set tenant so the zero cannot come from an empty table.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class TenantGucTests
{
    private readonly PostgresTenancyFixture _fixture;

    public TenantGucTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task TenantGuc_Unset_OrEmpty_ReturnsZeroRows()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var orgId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();

        await SeedAsync(database, orgId, projectId);

        // The tenant tables of this database: every public table that carries `org_id`
        // and that contracts/rls-exceptions.txt does NOT excuse. A `catalogue` table
        // keeps its built-in rows readable for every org by design (02 §4.5) and a
        // `mechanism` table has no policy at all (0006-0008), so neither is under the
        // fail-closed-zero-rows rule; `organization` is the T4 platform case and is
        // asserted separately at the end.
        var exceptions = RlsExceptionFile.Read();
        var tenantTables = await DiscoverOrgScopedTablesAsync(_fixture.AppConnectionString(database), exceptions);
        tenantTables.ShouldContain("public.project");
        tenantTables.ShouldContain("public.rls_probe");
        tenantTables.ShouldContain("public.idempotency_record");

        // What the migration owner sees with no tenant setting: the permissive
        // `TO evidgate_owner` policies (0003-0007) make this the whole table, so a
        // later zero on the app connection is a policy outcome, not an empty table.
        var ownerCounts = await CountAsync(_fixture.OwnerConnectionString(database), orgId: null, tenantTables);
        foreach (var table in tenantTables)
        {
            ownerCounts[table].ShouldBeGreaterThan(0, $"{table} is seeded, so the fail-closed zero below is meaningful");
        }

        // 1. Never set: every tenant table returns zero rows through evidgate_app.
        await AssertZeroRowsAsync(database, setEmptyValue: false, tenantTables);

        // 2. Set to the empty string: the same outcome through NULLIF(…, '')::uuid — a
        //    deployed setting that is present but blank must not open the scope.
        await AssertZeroRowsAsync(database, setEmptyValue: true, tenantTables);

        // 3. Set to the tenant: the seeded rows are exactly what becomes visible, so
        //    (1) and (2) are the policies' doing and the setting is what they read.
        var scopedCounts = await CountAsync(_fixture.AppConnectionString(database), orgId, tenantTables);
        foreach (var table in tenantTables)
        {
            scopedCounts[table].ShouldBe(ownerCounts[table], $"{table} is fully visible to its own tenant");
        }

        // 4. `organization` (T4): its policy compares the row's own `id`, so an unset or
        //    empty tenant hides every org row too — an org list is served by
        //    evidgate_resolve.member_orgs (02.6.1-T3), never by this select.
        await using (var unset = await TenantScope.BeginAsync(_fixture.AppConnectionString(database)))
        {
            (await unset.ScalarAsync<bool>("select evidgate.current_org_id() is null")).ShouldBeTrue();
            (await unset.ScalarAsync<long>("select count(*) from public.organization")).ShouldBe(0);
        }

        await using (var empty = await TenantScope.BeginAsync(_fixture.AppConnectionString(database)))
        {
            await empty.SetAsync("evidgate.org_id", string.Empty);
            (await empty.ScalarAsync<bool>("select evidgate.current_org_id() is null")).ShouldBeTrue();
            (await empty.ScalarAsync<long>("select count(*) from public.organization")).ShouldBe(0);
        }

        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            (await owner.ScalarAsync<long>("select count(*) from public.organization")).ShouldBe(1);
        }
    }

    /// <summary>
    /// Reads every tenant table as <c>evidgate_app</c>, with the tenant setting never
    /// set or set to the empty string, asserting zero rows and that the helper answered
    /// NULL instead of raising.
    /// </summary>
    private async Task AssertZeroRowsAsync(
        string database,
        bool setEmptyValue,
        IReadOnlyList<string> tenantTables)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(database));

        if (setEmptyValue)
        {
            // The parameterised form of SET LOCAL (00 §5.4.2, 00 §5.7 S1).
            await scope.SetAsync("evidgate.org_id", string.Empty);
        }

        (await scope.ScalarAsync<bool>("select evidgate.current_org_id() is null"))
            .ShouldBeTrue("the helper returns NULL, never an error, when the setting is missing or empty");

        foreach (var table in tenantTables)
        {
            (await scope.ScalarAsync<long>($"select count(*) from {table}"))
                .ShouldBe(0, $"{table} must return zero rows without a tenant context");
        }
    }

    /// <summary>
    /// The org-scoped tables of one database, read as the application role: public
    /// tables that carry <c>org_id</c> and that the exception list does not excuse.
    /// </summary>
    private static async Task<IReadOnlyList<string>> DiscoverOrgScopedTablesAsync(
        string connectionString,
        RlsExceptionList exceptions)
    {
        const string sql = """
            select pg_catalog.format('%I.%I', n.nspname, c.relname)
              from pg_catalog.pg_class c
              join pg_catalog.pg_namespace n on n.oid = c.relnamespace
             where c.relkind in ('r', 'p')
               and n.nspname <> 'information_schema'
               and n.nspname not like 'pg\_%'
               and exists (select 1
                             from pg_catalog.pg_attribute a
                            where a.attrelid = c.oid
                              and a.attname = 'org_id'
                              and a.attnum > 0
                              and not a.attisdropped)
             order by 1
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))
        {
            // `format('%I.%I', …)` re-emits catalog identifiers quoted, so the name is
            // safe to interpolate into a statement (the same rule as the harness census).
            names.Add(await reader.GetFieldValueAsync<string>(0, TestContext.Current.CancellationToken).ConfigureAwait(false));
        }

        return names.Where(name => exceptions.Find(name) is null).ToList();
    }

    /// <summary>Row counts per table for one role and one tenant setting.</summary>
    private static async Task<Dictionary<string, long>> CountAsync(
        string connectionString,
        Guid? orgId,
        IReadOnlyList<string> tables)
    {
        await using var scope = await TenantScope.BeginAsync(connectionString, orgId);

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            counts[table] = await scope.ScalarAsync<long>($"select count(*) from {table}");
        }

        return counts;
    }

    private async Task SeedAsync(string database, Guid orgId, Guid projectId)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database));

        await scope.ExecuteAsync(
            "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
            ("id", orgId),
            ("name", "Acme"),
            ("slug", "acme"));
        await scope.ExecuteAsync(
            "insert into public.project (id, org_id, name, slug) values (@id, @org_id, @name, @slug)",
            ("id", projectId),
            ("org_id", orgId),
            ("name", "Checkout"),
            ("slug", "checkout"));
        await scope.ExecuteAsync(
            "insert into public.rls_probe (org_id, id, label) values (@org_id, @id, @label)",
            ("org_id", orgId),
            ("id", Guid.CreateVersion7()),
            ("label", "probe"));
        await scope.ExecuteAsync(
            """
            insert into public.idempotency_record
                (id, org_id, project_id, operation, key, request_hash, expires_at)
            values (@id, @org_id, @project_id, @operation, @key, @request_hash, now() + interval '25 hours')
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", orgId),
            ("project_id", projectId),
            ("operation", "project:create"),
            ("key", "k-1"),
            ("request_hash", "sha256:0"));
        await scope.ExecuteAsync(
            """
            insert into public.audit_log (id, org_id, seq, actor_type, actor_id, action, prev_hash, entry_hash)
            values (@id, @org_id, 1, 'system', 'system', 'probe.seed', decode(repeat('00', 32), 'hex'), decode(repeat('01', 32), 'hex'))
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", orgId));
        var bindingId = Guid.CreateVersion7();
        await scope.ExecuteAsync(
            """
            insert into public.ci_binding (id, org_id, project_id, provider, repository_id, repository_full_name, allowed_ref_patterns, allowed_workflow_refs)
            values (@id, @org_id, @project_id, 'github_actions', 1, 'acme/web', array['refs/heads/main'], array['acme/web/.github/workflows/ci.yml@refs/heads/main'])
            """,
            ("id", bindingId),
            ("org_id", orgId),
            ("project_id", projectId));
        await scope.ExecuteAsync(
            """
            insert into public.ci_token_issue (id, org_id, project_id, binding_id, provider, jti, repository_id, repository_full_name, ref, sha, run_id, run_attempt, job_workflow_ref, expires_at)
            values (@id, @org_id, @project_id, @binding_id, 'github_actions', 'jti-probe', 1, 'acme/web', 'refs/heads/main', 'abc', 1, 1, 'wf', now() + interval '1 hour')
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", orgId),
            ("project_id", projectId),
            ("binding_id", bindingId));
        await scope.ExecuteAsync(
            """
            insert into public.ingest_token (id, org_id, project_id, name, prefix, token_hmac, pepper_version, scopes)
            values (@id, @org_id, @project_id, 'probe', 'abcdefghijkl', decode(repeat('00', 32), 'hex'), 1, array['run:create'])
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", orgId),
            ("project_id", projectId));
        var caseId = Guid.CreateVersion7();
        await scope.ExecuteAsync(
            """
            insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, first_seen_commit)
            values (@id, @org_id, @project_id, @annotation_id, 'probe', 'playwright', 'abcdef0')
            """,
            ("id", caseId),
            ("org_id", orgId),
            ("project_id", projectId),
            ("annotation_id", Guid.CreateVersion7()));
        await scope.ExecuteAsync(
            "insert into public.case_identity_source (id, org_id, case_id, source, raw_key, commit_sha) values (@id, @org_id, @case_id, 'annotation', 'k', 'abcdef0')",
            ("id", Guid.CreateVersion7()),
            ("org_id", orgId),
            ("case_id", caseId));
        await scope.ExecuteAsync(
            "insert into public.case_rename_history (id, org_id, case_id, from_path, to_path, commit_sha) values (@id, @org_id, @case_id, 'a', 'b', 'abcdef0')",
            ("id", Guid.CreateVersion7()),
            ("org_id", orgId),
            ("case_id", caseId));
        var membershipId = Guid.CreateVersion7();
        await scope.ExecuteAsync(
            "insert into public.membership (id, org_id, invited_email) values (@id, @org_id, @invited_email)",
            ("id", membershipId),
            ("org_id", orgId),
            ("invited_email", "dev@acme.test"));
        await scope.ExecuteAsync(
            "insert into public.membership_role (id, org_id, membership_id, role_id) values (@id, @org_id, @membership_id, @role_id)",
            ("id", Guid.CreateVersion7()),
            ("org_id", orgId),
            ("membership_id", membershipId),
            ("role_id", Guid.CreateVersion7()));

        await scope.CommitAsync();
    }
}
