namespace Tenancy.IntegrationTests;

/// <summary>
/// The suite revision, environment revision and manifest a <c>run</c> of a second project needs, seeded as the
/// migration role: a run may only name parents of its own project, so a test that files a run under a sibling
/// project must seed these first instead of copying the original project's parents.
/// </summary>
internal static class SiblingProjectParents
{
    /// <summary>The ids of the seeded parents.</summary>
    internal sealed record Ids(Guid SuiteRevision, Guid EnvironmentRevision, Guid Manifest);

    public static async Task<Ids> SeedAsync(PostgresTenancyFixture fixture, string database, Guid org, Guid project)
    {
        var suite = Guid.CreateVersion7();
        var suiteRevision = Guid.CreateVersion7();
        var environment = Guid.CreateVersion7();
        var environmentRevision = Guid.CreateVersion7();
        var manifest = Guid.CreateVersion7();

        await fixture.ExecuteAsync(
            database,
            $$"""
            insert into public.suite (id, org_id, project_id, name, slug) values ('{{suite:D}}', '{{org:D}}', '{{project:D}}', 'probe', 'probe-suite');
            insert into public.suite_revision (id, org_id, suite_id, revision_no, revision_hash, shard_strategy, retry_policy)
              values ('{{suiteRevision:D}}', '{{org:D}}', '{{suite:D}}', 1, 'sha256:' || repeat('a', 64), '{"kind":"none"}', '{"max_retries":0}');
            insert into public.environment (id, org_id, project_id, environment_key, name) values ('{{environment:D}}', '{{org:D}}', '{{project:D}}', 'sibling-' || substr('{{environment:D}}', 1, 8), 'Sibling');
            insert into public.environment_revision (id, org_id, project_id, environment_id, revision_no, base_urls, dependency_versions, revision_hash, created_by)
              values ('{{environmentRevision:D}}', '{{org:D}}', '{{project:D}}', '{{environment:D}}', 1, '{}', '{}', 'sha256:' || repeat('c', 64), 'probe');
            insert into public.manifest (id, org_id, project_id, manifest_hash, schema_version, suite_revision_id, environment_revision_id, shard_plan, matrix, env_needs, planned_count, document, verified_by)
              values ('{{manifest:D}}', '{{org:D}}', '{{project:D}}', 'sha256:' || repeat('d', 64), 1, '{{suiteRevision:D}}', '{{environmentRevision:D}}', '{}', '{}', '{}', 0, '{}', 'probe');
            """);

        return new Ids(suiteRevision, environmentRevision, manifest);
    }
}
