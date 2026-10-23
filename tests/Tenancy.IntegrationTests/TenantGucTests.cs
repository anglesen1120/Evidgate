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
        var testRevisionId = Guid.CreateVersion7();
        await scope.ExecuteAsync(
            """
            insert into public.test_revision (id, org_id, case_id, revision_hash, commit_sha, file_path, framework, content_sha256, assertion_hash)
            values (@id, @org_id, @case_id, @hash, 'abcdef0', 'a', 'playwright', @hash, @hash)
            """,
            ("id", testRevisionId),
            ("org_id", orgId),
            ("case_id", caseId),
            ("hash", "sha256:" + new string('a', 64)));
        var suiteId = Guid.CreateVersion7();
        var suiteRevisionId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();
        var memberId = Guid.CreateVersion7();
        await scope.ExecuteAsync(
            "insert into public.suite (id, org_id, project_id, name, slug) values (@id, @org_id, @project_id, 'probe', 'probe-suite')",
            ("id", suiteId),
            ("org_id", orgId),
            ("project_id", projectId));
        await scope.ExecuteAsync(
            """
            insert into public.suite_revision (id, org_id, suite_id, revision_no, revision_hash, shard_strategy, retry_policy)
            values (@id, @org_id, @suite_id, 1, @hash, '{"kind":"none"}', '{"max_retries":0}')
            """,
            ("id", suiteRevisionId),
            ("org_id", orgId),
            ("suite_id", suiteId),
            ("hash", "sha256:" + new string('a', 64)));
        await scope.ExecuteAsync(
            "insert into public.variant (id, org_id, project_id, variant_hash, params) values (@id, @org_id, @project_id, @hash, '{}')",
            ("id", variantId),
            ("org_id", orgId),
            ("project_id", projectId),
            ("hash", "sha256:" + new string('b', 64)));
        await scope.ExecuteAsync(
            """
            insert into public.suite_member (id, org_id, suite_revision_id, case_revision_id, variant_id, ordinal)
            select @id, @org_id, @suite_revision_id, r.id, @variant_id, 0 from public.test_revision r limit 1
            """,
            ("id", memberId),
            ("org_id", orgId),
            ("suite_revision_id", suiteRevisionId),
            ("variant_id", variantId));
        await scope.ExecuteAsync(
            "insert into public.case_dependency (id, org_id, suite_revision_id, parent_member_id, child_member_id) values (@id, @org_id, @suite_revision_id, @member_id, @member_id)",
            ("id", Guid.CreateVersion7()),
            ("org_id", orgId),
            ("suite_revision_id", suiteRevisionId),
            ("member_id", memberId));
        var environmentId = Guid.CreateVersion7();
        var environmentRevisionId = Guid.CreateVersion7();
        await scope.ExecuteAsync(
            "insert into public.environment (id, org_id, project_id, environment_key, name) values (@id, @org_id, @project_id, 'staging', 'Staging')",
            ("id", environmentId),
            ("org_id", orgId),
            ("project_id", projectId));
        await scope.ExecuteAsync(
            """
            insert into public.environment_revision (id, org_id, project_id, environment_id, revision_no, base_urls, dependency_versions, revision_hash, created_by)
            values (@id, @org_id, @project_id, @environment_id, 1, '{}', '{}', @hash, 'probe')
            """,
            ("id", environmentRevisionId),
            ("org_id", orgId),
            ("project_id", projectId),
            ("environment_id", environmentId),
            ("hash", "sha256:" + new string('c', 64)));
        var manifestId = Guid.CreateVersion7();
        await scope.ExecuteAsync(
            """
            insert into public.manifest (id, org_id, project_id, manifest_hash, schema_version, suite_revision_id, environment_revision_id, shard_plan, matrix, env_needs, planned_count, document, verified_by)
            values (@id, @org_id, @project_id, @hash, 1, @suite_revision_id, @environment_revision_id, '{}', '{}', '{}', 0, '{}', 'probe')
            """,
            ("id", manifestId),
            ("org_id", orgId),
            ("project_id", projectId),
            ("hash", "sha256:" + new string('d', 64)),
            ("suite_revision_id", suiteRevisionId),
            ("environment_revision_id", environmentRevisionId));
        var runId = Guid.CreateVersion7();
        await scope.ExecuteAsync(
            """
            insert into public.run
                (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id, collection_deadline_at, late_window_minutes)
            values (@id, @org_id, @project_id, @manifest_id, @suite_revision_id, @environment_revision_id, now() + interval '2 hours', 1440)
            """,
            ("id", runId),
            ("org_id", orgId),
            ("project_id", projectId),
            ("manifest_id", manifestId),
            ("suite_revision_id", suiteRevisionId),
            ("environment_revision_id", environmentRevisionId));
        await scope.ExecuteAsync(
            """
            insert into public.planned_execution
                (id, org_id, run_id, execution_key, case_revision_id, variant_hash, target_env, shard_id)
            values (@id, @org_id, @run_id, @execution_key, @case_revision_id, @variant_hash, 'staging', 'shard-1')
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", orgId),
            ("run_id", runId),
            ("execution_key", new string('a', 64)),
            ("case_revision_id", testRevisionId),
            ("variant_hash", "sha256:" + new string('b', 64)));
        var policyRevisionId = Guid.CreateVersion7();
        await scope.ExecuteAsync(
            """
            insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, created_by)
            values (@id, @org_id, @project_id, 1, @hash, '{}', @created_by)
            """,
            ("id", policyRevisionId),
            ("org_id", orgId),
            ("project_id", projectId),
            ("hash", "sha256:" + new string('e', 64)),
            ("created_by", Guid.CreateVersion7()));
        await scope.ExecuteAsync(
            """
            insert into public.policy_activation (id, org_id, project_id, policy_revision_id, activated_by)
            values (@id, @org_id, @project_id, @policy_revision_id, @activated_by)
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", orgId),
            ("project_id", projectId),
            ("policy_revision_id", policyRevisionId),
            ("activated_by", Guid.CreateVersion7()));
        await scope.ExecuteAsync(
            """
            insert into public.adapter_capability (id, org_id, name, protocol_major, protocol_minor, engine_family, engine_version, platforms, supports, mode)
            values (@id, @org_id, 'playwright', 1, 0, 'playwright', '1.63.0', '{linux-x64}', '{}', 'full')
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", orgId));
        await scope.ExecuteAsync(
            """
            insert into public.waiver (id, org_id, project_id, scope, target_id, reason, requested_by, expires_at)
            values (@id, @org_id, @project_id, 'case', @target_id, 'probe', @requested_by, now() + interval '1 hour')
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", orgId),
            ("project_id", projectId),
            ("target_id", Guid.CreateVersion7()),
            ("requested_by", Guid.CreateVersion7()));
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
        await scope.ExecuteAsync(
            """
            insert into public.quarantine_entry (id, org_id, project_id, case_id, owner, reason, expires_at)
            values (@id, @org_id, @project_id, @case_id, @owner, 'probe', now() + interval '1 hour')
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", orgId),
            ("project_id", projectId),
            ("case_id", caseId),
            ("owner", membershipId));
        var artifactId = Guid.CreateVersion7();
        await scope.ExecuteAsync(
            """
            insert into public.artifact
                (id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, media_type, checksum, sensitivity)
            values (@id, @org_id, @project_id, @run_id, @execution_key, 1, 'trace', @object_key, 'application/zip', @checksum, 'normal')
            """,
            ("id", artifactId),
            ("org_id", orgId),
            ("project_id", projectId),
            ("run_id", runId),
            ("execution_key", new string('a', 64)),
            ("object_key", $"org/{orgId}/project/{projectId}/run/{runId}/attempt/{new string('a', 64)}/1/{artifactId}"),
            ("checksum", "sha256:" + new string('b', 64)));
        await scope.ExecuteAsync(
            """
            insert into public.artifact_kind_policy
                (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity)
            values (@id, @org_id, @project_id, 'trace', 1048576, array['application/zip'], 'normal')
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", orgId),
            ("project_id", projectId));
        await scope.ExecuteAsync(
            """
            insert into public.producer_slot (id, org_id, run_id, shard_index, producer_id)
            values (@id, @org_id, @run_id, 1, 'shard-1')
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", orgId),
            ("run_id", runId));
        await scope.ExecuteAsync(
            """
            insert into public.case_attempt
                (id, org_id, run_id, execution_key, case_id, attempt_no, raw_status, normalized_status)
            values (@id, @org_id, @run_id, @execution_key, @case_id, 1, 'passed', 'passed')
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", orgId),
            ("run_id", runId),
            ("execution_key", new string('a', 64)),
            ("case_id", caseId));
        // The 04.1.1-T1 tables (migration 0034): one event and its batch receipt, both naming the slot
        // inserted above, so the tenant tables this suite discovers are never empty.
        await scope.ExecuteAsync(
            """
            insert into public.event
                (id, org_id, run_id, event_id, producer_id, sequence, execution_key, attempt_no, type, payload_hash)
            values (@id, @org_id, @run_id, @event_id, 'shard-1', 1, @execution_key, 1, 'test.step', @payload_hash)
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", orgId),
            ("run_id", runId),
            ("event_id", Guid.CreateVersion7()),
            ("execution_key", new string('a', 64)),
            ("payload_hash", "sha256:" + new string('b', 64)));
        await scope.ExecuteAsync(
            """
            insert into public.event_batch
                (id, org_id, run_id, producer_id, event_count, bytes_in, bytes_expanded, accepted, duplicate, request_id)
            values (@id, @org_id, @run_id, 'shard-1', 1, 256, 256, 1, 0, 'probe-request')
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", orgId),
            ("run_id", runId));

        // The 13.1.1-T1 tables (migration 0039): one row per org-scoped authoring table, chained by
        // their composite FKs, so the discovered tenant set is never empty.
        var featureNodeId = Guid.CreateVersion7();
        await scope.ExecuteAsync(
            """
            insert into public.feature_node (id, org_id, project_id, kind, title, position)
            values (@id, @org_id, @project_id, 'module', 'Shop', 0)
            """,
            ("id", featureNodeId),
            ("org_id", orgId),
            ("project_id", projectId));
        var checklistItemId = Guid.CreateVersion7();
        await scope.ExecuteAsync(
            """
            insert into public.checklist_item
                (id, org_id, project_id, feature_node_id, title, priority, state, source, position)
            values (@id, @org_id, @project_id, @feature_node_id, 'Checkout', 'high', 'active', 'manual', 0)
            """,
            ("id", checklistItemId),
            ("org_id", orgId),
            ("project_id", projectId),
            ("feature_node_id", featureNodeId));
        var manualTestCaseId = Guid.CreateVersion7();
        await scope.ExecuteAsync(
            """
            insert into public.manual_test_case
                (id, org_id, project_id, checklist_item_id, code, title, steps, priority, state, source)
            values (@id, @org_id, @project_id, @checklist_item_id, 'SHOP-001', 'Buy a sticker', '{"schemaVersion":1,"steps":[]}', 'high', 'draft', 'manual')
            """,
            ("id", manualTestCaseId),
            ("org_id", orgId),
            ("project_id", projectId),
            ("checklist_item_id", checklistItemId));
        await scope.ExecuteAsync(
            "insert into public.authoring_code_counter (id, org_id, project_id) values (@id, @org_id, @project_id)",
            ("id", Guid.CreateVersion7()),
            ("org_id", orgId),
            ("project_id", projectId));
        await scope.ExecuteAsync(
            """
            insert into public.authoring_version
                (id, org_id, project_id, entity_kind, entity_id, version_no, batch_id, change_kind, content, content_hash, source)
            values (@id, @org_id, @project_id, 'manual_test_case', @entity_id, 1, @batch_id, 'create', '{}'::jsonb, @hash, 'manual')
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", orgId),
            ("project_id", projectId),
            ("entity_id", manualTestCaseId),
            ("batch_id", Guid.CreateVersion7()),
            ("hash", "sha256:" + new string('f', 64)));

        await scope.CommitAsync();
    }
}
