// Row seeding and reads of the release aggregation cases: every statement runs as the superuser, so a case writes the
// exact rows it needs (a verdict, a binding the route would refuse, a replay run) and reads the stored aggregate back.
using Shouldly;

namespace Tenancy.IntegrationTests;

public sealed partial class ReleaseAggregationTests
{
    private const string EmptyJson = "{}";

    /// <summary>One policy revision of the world's project, numbered after the ones it already holds.</summary>
    private async Task<Guid> PolicyAsync(RunCreationTests.World world, char fill)
    {
        var id = Guid.CreateVersion7();
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, created_by)
            select '{id}', '{world.Org}', '{world.Project}', coalesce(max(revision_no), 0) + 1,
                   'sha256:{new string(fill, 64)}', '{EmptyJson}', '{Guid.CreateVersion7()}'
              from public.policy_revision where org_id = '{world.Org}' and project_id = '{world.Project}'
            """);

        return id;
    }

    /// <summary>A run through the served route whose components map holds the given digests.</summary>
    private static Task<Guid> RunAsync(RunCreationTests.World world, params (string Key, char Fill)[] components) =>
        ReleaseIntegrationTests.RunWithAsync(world, ReleaseIntegrationTests.Components(components));

    /// <summary>A run bound to the release for one key through the bind route, with a live verdict.</summary>
    private async Task<Guid> BoundRunAsync(
        RunCreationTests.World world,
        Guid release,
        string key,
        char fill,
        Guid policy,
        string decision)
    {
        var run = await RunAsync(world, (key, fill));
        var bound = await ReleaseIntegrationTests.BindAsync(world, release, run);
        bound.Status.ShouldBe(System.Net.HttpStatusCode.Created, bound.ErrorMessage);
        await InsertEvaluationAsync(world, run, policy, decision);

        return run;
    }

    private Task InsertBindingAsync(RunCreationTests.World world, Guid release, Guid run, string key) =>
        _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.release_run (id, org_id, release_id, run_id, component_key)
            values ('{Guid.CreateVersion7()}', '{world.Org}', '{release}', '{run}', '{key}')
            """);

    /// <summary>One live evaluation row; a FAIL has failures, a PASS is complete with nothing missing.</summary>
    private Task InsertEvaluationAsync(RunCreationTests.World world, Guid run, Guid policy, string decision) =>
        _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.evaluation
              (id, org_id, run_id, evidence_revision, policy_revision_id, policy_hash, manifest_hash, completeness,
               missing, outcome, decision, reasons, evaluator_version, mode, exceptions_hash, exceptions_as_of)
            select '{Guid.CreateVersion7()}', '{world.Org}', '{run}', 1, p.id, p.hash, 'sha256:{new string('e', 64)}',
                   'complete', '{EmptyJson}', '{(decision == "FAIL" ? "has_failures" : "all_passed")}', '{decision}', '{EmptyJson}',
                   '1.0.0', 'live', 'sha256:{new string('f', 64)}', now()
              from public.policy_revision p where p.org_id = '{world.Org}' and p.id = '{policy}'
            """);

    /// <summary>Turns a run into a replay run; triggers are off for the statement because the create route never writes one this way.</summary>
    private Task MakeReplayAsync(RunCreationTests.World world, Guid run) =>
        _fixture.ExecuteAsync(
            world.Database,
            $"""
            set session_replication_role = replica;
            update public.run set purpose = 'replay', replay_grant_id = '{Guid.CreateVersion7()}'
             where org_id = '{world.Org}' and id = '{run}';
            """);

    private Task<T> ScalarAsync<T>(RunCreationTests.World world, Guid release, string column) =>
        _fixture.ScalarAsync<T>(
            world.Database,
            $"""
            select {column} from public.release_decision
             where org_id = '{world.Org}' and release_id = '{release}' order by created_at desc, id desc limit 1
            """);

    private Task<IReadOnlyList<string>> ReasonsAsync(RunCreationTests.World world, Guid release) =>
        _fixture.ColumnAsync<string>(
            world.Database,
            $"""
            select unnest(reasons) as r from public.release_decision
             where org_id = '{world.Org}' and release_id = '{release}' order by 1
            """);

    private Task<long> DecisionCountAsync(RunCreationTests.World world, Guid release) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.release_decision where org_id = '{world.Org}' and release_id = '{release}'");
}
