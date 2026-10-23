using System.Security.Cryptography;
using System.Text;
using Shouldly;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.3.3-T2: the parents of one run's plan, written as the migration role — one committed
/// <c>run</c>, <paramref name="count"/> planned executions (each with its own <c>test_case</c>,
/// <c>test_revision</c>, <c>execution_key</c>, <c>variant_hash</c> and <c>target_env</c>) and, on
/// request, the suite members and <c>case_dependency</c> edges the blocked-dependant resolver walks.
/// <para>
/// The run's parents are seeded like <see cref="RunRowSeeder"/> (same shapes), but each call mints its own
/// suite, suite revision and manifest, keyed by the run id: migration 0022's
/// <c>catalog_suite_insert_guard</c> seals a revision when the transaction that creates it ends, so its
/// members and <c>case_dependency</c> edges can only be written in that same transaction — a second call for
/// the same tenant would otherwise be refused. The environment and its revision are shared per
/// <c>(org, project)</c>. The migration role is used because <c>case_attempt</c>/<c>planned_execution</c>
/// FORCE RLS (only the owner's permissive backfill policy sees every org) and the app role may not create
/// the catalogue parents.
/// </para>
/// <para>
/// Every execution gets its own case, so the <c>execution_key</c> of
/// <see cref="Evidgate.Modules.Orchestrator.Domain.ExecutionKey"/> is distinct per execution; the
/// variant hash is a deterministic <c>sha256:&lt;64 hex&gt;</c> per index and is written to both
/// <c>planned_execution</c> and <c>variant</c>/<c>suite_member</c>, so the resolver's Catalog walk
/// matches the planned row.
/// </para>
/// </summary>
internal static class PlannedExecutionSeeder
{
    /// <summary>The target environment the plan's executions run against unless another is named.</summary>
    internal const string DefaultTargetEnvironment = "staging";

    /// <summary>One seeded planned execution and the catalogue row it names.</summary>
    /// <param name="ExecutionId">The <c>planned_execution.id</c>.</param>
    /// <param name="ExecutionKey">The <c>execution_key</c>, <c>^[0-9a-f]{64}$</c>.</param>
    /// <param name="CaseId">The <c>test_case.id</c> the attempt's <c>case_id</c> must equal.</param>
    /// <param name="CaseRevisionId">The <c>test_revision.id</c> the planned row carries.</param>
    /// <param name="VariantHash">The <c>variant_hash</c>, <c>sha256:&lt;64 hex&gt;</c>.</param>
    /// <param name="TargetEnv">The <c>target_env</c>.</param>
    internal sealed record SeededExecution(
        Guid ExecutionId,
        string ExecutionKey,
        Guid CaseId,
        Guid CaseRevisionId,
        string VariantHash,
        string TargetEnv);

    /// <summary>One seeded run and its plan.</summary>
    /// <param name="RunId">The <c>run.id</c>.</param>
    /// <param name="Executions">The planned executions, in seed order.</param>
    /// <param name="TargetEnv">The target environment every planned execution carries unless it was overridden per execution.</param>
    internal sealed record SeededRun(
        Guid RunId,
        IReadOnlyList<SeededExecution> Executions,
        string TargetEnv);

    /// <summary>
    /// Seeds one committed run whose project is the org's first project (these tests create exactly one
    /// project per org unless a case adds another). Each call mints its own suite, suite revision, manifest
    /// and run, so a tenant can hold any number of plans; the environment and its revision are reused
    /// (<c>on conflict do nothing</c>).
    /// </summary>
    /// <param name="fixture">The assembly's PostgreSQL fixture.</param>
    /// <param name="database">The migrated database.</param>
    /// <param name="org">The tenant that owns the run.</param>
    /// <param name="count">How many planned executions to seed.</param>
    /// <param name="targetEnv">The target environment of every planned execution.</param>
    /// <param name="targetEnvs">Per-execution target environments (index-aligned with the plan); overrides <paramref name="targetEnv"/> when given.</param>
    /// <param name="edges">
    /// Dependency edges as <c>(parent, child)</c> indices into the plan (parent = setup, child = dependant);
    /// each edge becomes a <c>case_dependency</c> row between the two members' suite members. Null seeds no edges.
    /// </param>
    /// <param name="suiteMemberCount">
    /// How many suite members to seed; defaults to <paramref name="count"/>. A larger value adds members with
    /// no planned execution, which is the "edge to a member whose execution is not planned in this run" case.
    /// </param>
    /// <param name="runId">A fixed run id, or null for a fresh one.</param>
    internal static async Task<SeededRun> SeedPlanAsync(
        PostgresTenancyFixture fixture,
        string database,
        Guid org,
        int count,
        string targetEnv = DefaultTargetEnvironment,
        IReadOnlyList<(int Parent, int Child)>? edges = null,
        int? suiteMemberCount = null,
        Guid? runId = null,
        IReadOnlyList<string>? targetEnvs = null)
    {
        var id = runId ?? Guid.CreateVersion7();
        var members = suiteMemberCount ?? count;

        await using var scope = await TenantScope.BeginAsync(fixture.OwnerConnectionString(database));

        var project = await scope.ScalarAsync<Guid>(
            "select id from public.project where org_id = @org_id order by created_at, id limit 1",
            ("org_id", org));

        // Per-run parents (see the type doc): a suite revision is sealed when the transaction that creates it ends,
        // so its members and edges can only be written in that same transaction — a second call for the same tenant
        // therefore mints its own suite, revision and manifest. The environment and its revision are shared per
        // (org, project).
        var suiteId = Derived(org, project, $"suite|{id:D}");
        var suiteRevisionId = Derived(org, project, $"suite-revision|{id:D}");
        var environmentId = Derived(org, project, "environment");
        var environmentRevisionId = Derived(org, project, "environment-revision");
        var manifestId = Derived(org, project, $"manifest|{id:D}");

        await scope.ExecuteAsync(
            "insert into public.suite (id, org_id, project_id, name, slug) "
            + "values (@id, @org_id, @project_id, 'probe', @slug) on conflict do nothing",
            ("id", suiteId), ("org_id", org), ("project_id", project), ("slug", $"probe-suite-{id:N}"));
        await scope.ExecuteAsync(
            """
            insert into public.suite_revision (id, org_id, suite_id, revision_no, revision_hash, shard_strategy, retry_policy)
            values (@id, @org_id, @suite_id, 1, @hash, '{"kind":"none"}', '{"max_retries":0}')
            on conflict do nothing
            """,
            ("id", suiteRevisionId), ("org_id", org), ("suite_id", suiteId), ("hash", Sha256Of($"suite-revision|{id:D}")));
        await scope.ExecuteAsync(
            "insert into public.environment (id, org_id, project_id, environment_key, name) "
            + "values (@id, @org_id, @project_id, 'staging', 'Staging') on conflict do nothing",
            ("id", environmentId), ("org_id", org), ("project_id", project));
        await scope.ExecuteAsync(
            """
            insert into public.environment_revision (id, org_id, project_id, environment_id, revision_no, base_urls, dependency_versions, revision_hash, created_by)
            values (@id, @org_id, @project_id, @environment_id, 1, '{}', '{}', @hash, 'probe')
            on conflict do nothing
            """,
            ("id", environmentRevisionId), ("org_id", org), ("project_id", project), ("environment_id", environmentId),
            ("hash", "sha256:" + new string('c', 64)));
        await scope.ExecuteAsync(
            """
            insert into public.manifest (id, org_id, project_id, manifest_hash, schema_version, suite_revision_id, environment_revision_id, shard_plan, matrix, env_needs, planned_count, document, verified_by)
            values (@id, @org_id, @project_id, @hash, 1, @suite_revision_id, @environment_revision_id, '{}', '{}', '{}', 0, '{}', 'probe')
            on conflict do nothing
            """,
            ("id", manifestId), ("org_id", org), ("project_id", project), ("hash", Sha256Of($"manifest|{id:D}")),
            ("suite_revision_id", suiteRevisionId), ("environment_revision_id", environmentRevisionId));

        (await scope.ExecuteAsync(
            """
            insert into public.run
              (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id, collection_deadline_at, late_window_minutes)
            values (@id, @org_id, @project_id, @manifest_id, @suite_revision_id, @environment_revision_id, now() + interval '2 hours', 1440)
            """,
            ("id", id), ("org_id", org), ("project_id", project), ("manifest_id", manifestId),
            ("suite_revision_id", suiteRevisionId), ("environment_revision_id", environmentRevisionId))).ShouldBe(1);

        var executions = new List<SeededExecution>(count);
        var memberIds = new List<Guid>(members);

        for (var i = 0; i < members; i++)
        {
            var caseId = Guid.CreateVersion7();
            var revisionId = Guid.CreateVersion7();
            var variantHash = VariantHashOf(org, i);
            var revisionHash = Sha256Of($"{caseId:D}|revision");
            var variantId = Guid.CreateVersion7();
            var memberId = Guid.CreateVersion7();

            await scope.ExecuteAsync(
                """
                insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, first_seen_commit)
                values (@id, @org_id, @project_id, @annotation_id, @name, 'playwright', 'abcdef0')
                """,
                ("id", caseId), ("org_id", org), ("project_id", project), ("annotation_id", Guid.CreateVersion7()),
                ("name", $"case-{i}"));
            await scope.ExecuteAsync(
                """
                insert into public.test_revision (id, org_id, case_id, revision_hash, commit_sha, file_path, framework, content_sha256, assertion_hash)
                values (@id, @org_id, @case_id, @hash, 'abcdef0', @path, 'playwright', @hash, @hash)
                """,
                ("id", revisionId), ("org_id", org), ("case_id", caseId),
                ("hash", revisionHash), ("path", $"tests/case-{i}.spec.ts"));

            // One variant row per distinct hash (variant_hash_key is (org, project, variant_hash)).
            await scope.ExecuteAsync(
                "insert into public.variant (id, org_id, project_id, variant_hash, params) "
                + "values (@id, @org_id, @project_id, @hash, '{}') on conflict do nothing",
                ("id", variantId), ("org_id", org), ("project_id", project), ("hash", variantHash));
            variantId = await scope.ScalarAsync<Guid>(
                "select id from public.variant where org_id = @org_id and project_id = @project_id and variant_hash = @hash",
                ("org_id", org), ("project_id", project), ("hash", variantHash));

            await scope.ExecuteAsync(
                """
                insert into public.suite_member (id, org_id, suite_revision_id, case_revision_id, variant_id, ordinal)
                values (@id, @org_id, @suite_revision_id, @case_revision_id, @variant_id, @ordinal)
                """,
                ("id", memberId), ("org_id", org), ("suite_revision_id", suiteRevisionId), ("case_revision_id", revisionId),
                ("variant_id", variantId), ("ordinal", i));

            memberIds.Add(memberId);

            if (i >= count)
            {
                continue;
            }

            var executionId = Guid.CreateVersion7();
            var executionEnv = targetEnvs?[i] ?? targetEnv;
            var executionKey = ExecutionKeyOf(caseId, variantHash, executionEnv);

            await scope.ExecuteAsync(
                """
                insert into public.planned_execution
                  (id, org_id, run_id, execution_key, case_revision_id, variant_hash, target_env, shard_id)
                values (@id, @org_id, @run_id, @execution_key, @case_revision_id, @variant_hash, @target_env, @shard_id)
                """,
                ("id", executionId), ("org_id", org), ("run_id", id), ("execution_key", executionKey),
                ("case_revision_id", revisionId), ("variant_hash", variantHash), ("target_env", executionEnv),
                ("shard_id", $"shard-{i + 1}"));

            executions.Add(new SeededExecution(
                executionId, executionKey, caseId, revisionId, variantHash, executionEnv));
        }

        foreach (var (parent, child) in edges ?? [])
        {
            await scope.ExecuteAsync(
                """
                insert into public.case_dependency (id, org_id, suite_revision_id, parent_member_id, child_member_id)
                values (@id, @org_id, @suite_revision_id, @parent, @child)
                """,
                ("id", Guid.CreateVersion7()), ("org_id", org), ("suite_revision_id", suiteRevisionId),
                ("parent", memberIds[parent]), ("child", memberIds[child]));
        }

        await scope.CommitAsync();

        return new SeededRun(id, executions, targetEnv);
    }

    /// <summary>The deterministic variant hash of execution <paramref name="index"/>, <c>sha256:&lt;64 hex&gt;</c>.</summary>
    internal static string VariantHashOf(Guid org, int index) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{org:D}|variant|{index}")));

    /// <summary>A deterministic, valid <c>sha256:&lt;64 hex&gt;</c> hash of one label.</summary>
    private static string Sha256Of(string label) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(label)));

    /// <summary>The <c>execution_key</c> the writer matches the planned row on: 64 lowercase hex.</summary>
    private static string ExecutionKeyOf(Guid caseId, string variantHash, string targetEnv)
    {
        var caseBytes = Encoding.UTF8.GetBytes(caseId.ToString("D"));
        var variantBytes = Encoding.UTF8.GetBytes(variantHash);
        var envBytes = Encoding.UTF8.GetBytes(targetEnv);
        var buffer = new byte[4 + caseBytes.Length + 4 + variantBytes.Length + 4 + envBytes.Length];
        var offset = 0;

        foreach (var field in new[] { caseBytes, variantBytes, envBytes })
        {
            buffer[offset] = (byte)(field.Length >> 24);
            buffer[offset + 1] = (byte)(field.Length >> 16);
            buffer[offset + 2] = (byte)(field.Length >> 8);
            buffer[offset + 3] = (byte)field.Length;
            offset += 4;
            field.CopyTo(buffer, offset);
            offset += field.Length;
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer));
    }

    /// <summary>A stable id for one parent of <c>(org, project)</c>, so repeated calls reuse the row (RunRowSeeder's rule).</summary>
    private static Guid Derived(Guid org, Guid project, string kind)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{org:D}|{project:D}|{kind}"));

        return new Guid(digest.AsSpan(0, 16));
    }
}
