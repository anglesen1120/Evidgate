using System.Globalization;
using System.Net.Http.Headers;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The seeding and request half of <see cref="RunDecisionApiTests"/>: one migrated database per world, the run, its
/// policy revision and the stored <c>evaluation</c> rows a case needs written as the app role in one committed tenant
/// transaction, so the route reads exactly the rows the case arranged.
/// </summary>
public sealed partial class RunDecisionApiTests
{
    private const string PolicyHash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string ExceptionsHash = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private const string ManifestHash = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    private readonly PostgresTenancyFixture _fixture;

    public RunDecisionApiTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project) : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>One stored <c>evaluation</c> row to seed: the verdict inputs and the two closed code lists.</summary>
    private sealed record Verdict(
        int EvidenceRevision,
        string Mode,
        string Decision,
        string Completeness = "complete",
        string Outcome = "all_passed",
        string Missing = "{}",
        string Reasons = "{}");

    /// <summary>One migrated database with an org and its project, created through the real registry routes.</summary>
    private async Task<World> StartAsync(IReadOnlyDictionary<string, string?>? configuration = null)
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database), configuration);
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

    /// <summary>A run of the org's first project, seeded by the migration role so its parents exist.</summary>
    private async Task<Guid> SeedRunAsync(World world) =>
        (await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, world.Org, 1)).RunId;

    /// <summary>
    /// One <c>policy_revision</c> of the world's project, written by the app role: an <c>evaluation</c> row needs a
    /// real revision to point at, and the policy route's body schema is not what these tests are about.
    /// </summary>
    private async Task<Guid> SeedPolicyRevisionAsync(World world)
    {
        var revision = Guid.CreateVersion7();

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(
            """
            insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, created_by)
            select @id, @org_id, @project_id, coalesce(max(revision_no), 0) + 1, @hash, '{}', @created_by
              from public.policy_revision where org_id = @org_id and project_id = @project_id
            """,
            ("id", revision),
            ("org_id", world.Org),
            ("project_id", world.Project),
            ("hash", PolicyHash),
            ("created_by", world.User))).ShouldBe(1);
        await scope.CommitAsync();

        return revision;
    }

    /// <summary>
    /// One <c>evaluation</c> row as the app role, in one committed tenant transaction. The table is immutable, so each
    /// verdict is a fresh row and its <c>mode</c> is what decides whether the route may serve it.
    /// </summary>
    private async Task<Guid> InsertEvaluationAsync(World world, Guid runId, Guid policyRevisionId, Verdict verdict)
    {
        var id = Guid.CreateVersion7();

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(
            """
            insert into public.evaluation
              (id, org_id, run_id, evidence_revision, policy_revision_id, policy_hash, manifest_hash, completeness,
               missing, outcome, decision, reasons, evaluator_version, mode, exceptions_hash, exceptions_as_of)
            values
              (@id, @org_id, @run_id, @evidence_revision, @policy_revision_id, @policy_hash, @manifest_hash,
               @completeness, @missing::text[], @outcome, @decision, @reasons::text[], @evaluator_version, @mode,
               @exceptions_hash, now())
            """,
            ("id", id),
            ("org_id", world.Org),
            ("run_id", runId),
            ("evidence_revision", verdict.EvidenceRevision),
            ("policy_revision_id", policyRevisionId),
            ("policy_hash", PolicyHash),
            ("manifest_hash", ManifestHash),
            ("completeness", verdict.Completeness),
            ("missing", verdict.Missing),
            ("outcome", verdict.Outcome),
            ("decision", verdict.Decision),
            ("reasons", verdict.Reasons),
            ("evaluator_version", "1.0.0"),
            ("mode", verdict.Mode),
            ("exceptions_hash", ExceptionsHash))).ShouldBe(1);
        await scope.CommitAsync();

        return id;
    }

    /// <summary>A PostgreSQL text-array literal of <paramref name="codes"/>, the spelling the insert binds with a cast.</summary>
    private static string ArrayLiteral(IEnumerable<string> codes) => "{" + string.Join(",", codes) + "}";

    /// <summary>The route's request, as the world's admin unless a case names another caller.</summary>
    private static Task<ApiAnswer> GetAsync(World world, Guid runId, Action<HttpRequestHeaders>? caller = null) =>
        GetAsync(world.Client, runId, caller ?? world.Admin);

    /// <summary>The route's request through a given host's client, with the caller's headers.</summary>
    private static Task<ApiAnswer> GetAsync(HttpClient client, Guid runId, Action<HttpRequestHeaders> caller) =>
        client.SendAsync(
            HttpMethod.Get,
            string.Create(CultureInfo.InvariantCulture, $"/v1/runs/{runId}/decision"),
            caller);
}
