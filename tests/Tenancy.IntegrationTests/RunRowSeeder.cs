using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Shouldly;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.2.1-T1: the parents of one <c>run</c> (suite, suite revision, environment, environment
/// revision, manifest, test case, test revision) plus one committed <c>run</c> row, written as the
/// migration role.
/// <para>
/// Migration 0031 adds the two composite tenant keys 0028 and 0029 deferred — <c>artifact_run_fk</c>
/// and <c>producer_slot_run_fk</c>, both <c>(org_id, run_id) -&gt; run (org_id, id)</c> — and
/// validates them, so every <c>artifact</c>/<c>producer_slot</c> row a test writes must name a run
/// that exists in the same org. The parents hang off the org's project, which is resolved from the
/// org (these tests create exactly one project per org), and their ids are derived from
/// <c>(org, project, kind)</c>, so a second call for the same tenant reuses them
/// (<c>on conflict do nothing</c>) while every call still mints a fresh run.
/// </para>
/// </summary>
internal static class RunRowSeeder
{
    /// <summary>One committed run row for <paramref name="org"/>; the parents are seeded idempotently.</summary>
    public static async Task<Guid> SeedRunAsync(
        PostgresTenancyFixture fixture,
        string database,
        Guid org,
        Guid? runId = null)
    {
        var id = runId ?? Guid.CreateVersion7();

        await using var scope = await TenantScope.BeginAsync(fixture.OwnerConnectionString(database));

        var project = await scope.ScalarAsync<Guid>(
            "select id from public.project where org_id = @org_id order by created_at, id limit 1",
            ("org_id", org));

        var suiteId = Derived(org, project, "suite");
        var suiteRevisionId = Derived(org, project, "suite-revision");
        var environmentId = Derived(org, project, "environment");
        var environmentRevisionId = Derived(org, project, "environment-revision");
        var manifestId = Derived(org, project, "manifest");
        var caseId = Derived(org, project, "case");
        var testRevisionId = Derived(org, project, "test-revision");

        await scope.ExecuteAsync(
            "insert into public.suite (id, org_id, project_id, name, slug) "
            + "values (@id, @org_id, @project_id, 'probe', 'probe-suite') on conflict do nothing",
            ("id", suiteId), ("org_id", org), ("project_id", project));
        await scope.ExecuteAsync(
            """
            insert into public.suite_revision (id, org_id, suite_id, revision_no, revision_hash, shard_strategy, retry_policy)
            values (@id, @org_id, @suite_id, 1, @hash, '{"kind":"none"}', '{"max_retries":0}')
            on conflict do nothing
            """,
            ("id", suiteRevisionId), ("org_id", org), ("suite_id", suiteId), ("hash", "sha256:" + new string('a', 64)));
        // Reuse the `staging` environment the world may already hold (RunCreationTests.StartAsync creates one
        // through the API) and its first revision: environment_key_unique refuses a second row under that key,
        // and a plain `do nothing` insert would leave the manifest pointing at an environment revision that was
        // never inserted (environment_revision_environment_fk). The derived id is the fallback for a world
        // without an environment (a bare org/project, as EventSchemaTests builds).
        if (await scope.ScalarAsync<Guid?>(
                "select id from public.environment where org_id = @org_id and project_id = @project_id and environment_key = 'staging'",
                ("org_id", org), ("project_id", project)) is { } existingEnvironment)
        {
            environmentId = existingEnvironment;
        }
        else
        {
            await scope.ExecuteAsync(
                "insert into public.environment (id, org_id, project_id, environment_key, name) "
                + "values (@id, @org_id, @project_id, 'staging', 'Staging')",
                ("id", environmentId), ("org_id", org), ("project_id", project));
        }

        // Same for the environment's revision 1: reuse it when the world already created it, else append the
        // seeded one (environment_revision is append-only, so the existing row is never updated).
        if (await scope.ScalarAsync<Guid?>(
                "select id from public.environment_revision where org_id = @org_id and environment_id = @environment_id and revision_no = 1",
                ("org_id", org), ("environment_id", environmentId)) is { } existingEnvironmentRevision)
        {
            environmentRevisionId = existingEnvironmentRevision;
        }
        else
        {
            await scope.ExecuteAsync(
                """
                insert into public.environment_revision (id, org_id, project_id, environment_id, revision_no, base_urls, dependency_versions, revision_hash, created_by)
                values (@id, @org_id, @project_id, @environment_id, 1, '{}', '{}', @hash, 'probe')
                """,
                ("id", environmentRevisionId), ("org_id", org), ("project_id", project), ("environment_id", environmentId),
                ("hash", "sha256:" + new string('c', 64)));
        }
        await scope.ExecuteAsync(
            """
            insert into public.manifest (id, org_id, project_id, manifest_hash, schema_version, suite_revision_id, environment_revision_id, shard_plan, matrix, env_needs, planned_count, document, verified_by)
            values (@id, @org_id, @project_id, @hash, 1, @suite_revision_id, @environment_revision_id, '{}', '{}', '{}', 0, '{}', 'probe')
            on conflict do nothing
            """,
            ("id", manifestId), ("org_id", org), ("project_id", project), ("hash", "sha256:" + new string('d', 64)),
            ("suite_revision_id", suiteRevisionId), ("environment_revision_id", environmentRevisionId));
        await scope.ExecuteAsync(
            """
            insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, first_seen_commit)
            values (@id, @org_id, @project_id, @annotation_id, 'probe', 'playwright', 'abcdef0')
            on conflict do nothing
            """,
            ("id", caseId), ("org_id", org), ("project_id", project), ("annotation_id", Derived(org, project, "annotation")));
        await scope.ExecuteAsync(
            """
            insert into public.test_revision (id, org_id, case_id, revision_hash, commit_sha, file_path, framework, content_sha256, assertion_hash)
            values (@id, @org_id, @case_id, @hash, 'abcdef0', 'a', 'playwright', @hash, @hash)
            on conflict do nothing
            """,
            ("id", testRevisionId), ("org_id", org), ("case_id", caseId), ("hash", "sha256:" + new string('a', 64)));

        (await scope.ExecuteAsync(
            """
            insert into public.run
              (id, org_id, project_id, manifest_id, suite_revision_id, environment_revision_id, collection_deadline_at, late_window_minutes)
            values (@id, @org_id, @project_id, @manifest_id, @suite_revision_id, @environment_revision_id, now() + interval '2 hours', 1440)
            """,
            ("id", id), ("org_id", org), ("project_id", project), ("manifest_id", manifestId),
            ("suite_revision_id", suiteRevisionId), ("environment_revision_id", environmentRevisionId))).ShouldBe(1);

        await scope.CommitAsync();

        return id;
    }

    /// <summary>
    /// One <c>planned_execution</c> row for <paramref name="runId"/> on the test revision this seeder derives, so a
    /// run created by <see cref="SeedRunAsync"/> can carry an artifact for <paramref name="executionKey"/>.
    /// </summary>
    public static async Task PlanExecutionAsync(
        PostgresTenancyFixture fixture,
        string database,
        Guid org,
        Guid runId,
        string executionKey)
    {
        await using var scope = await TenantScope.BeginAsync(fixture.OwnerConnectionString(database));

        var project = await scope.ScalarAsync<Guid>(
            "select id from public.project where org_id = @org_id order by created_at, id limit 1",
            ("org_id", org));

        (await scope.ExecuteAsync(
            "insert into public.planned_execution (id, org_id, run_id, execution_key, case_revision_id, variant_hash, target_env, shard_id, required) "
            + "values (@id, @org_id, @run_id, @execution_key, @case_revision_id, @variant_hash, 'staging', 'shard-1', true)",
            ("id", Guid.CreateVersion7()), ("org_id", org), ("run_id", runId), ("execution_key", executionKey),
            ("case_revision_id", Derived(org, project, "test-revision")),
            ("variant_hash", "sha256:" + new string('c', 64)))).ShouldBe(1);

        await scope.CommitAsync();
    }

    /// <summary>A stable id for one parent of <c>(org, project)</c>, so repeated calls reuse the row.</summary>
    private static Guid Derived(Guid org, Guid project, string kind)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{org:D}|{project:D}|{kind}"));

        return new Guid(digest.AsSpan(0, 16));
    }
}
