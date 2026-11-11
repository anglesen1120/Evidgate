// The gate's own credential: a real minted `ci_producer` token reads the run decision of the project it was
// minted for, and a run it may not see answers the identical unknown-run body — a run of a sibling project in the
// same org and a run of a foreign org included. A token without `decision:read` is refused before any run is read.
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The gate route through the real host with the credential the CLI actually holds: the ingest-token exchange mints the
/// <c>ci_producer</c> set, so its <c>decision:read</c> makes <c>evidgate gate</c> a single-credential caller.
/// </summary>
[Trait("Category", "Docker")]
public sealed class RunDecisionCiTokenTests
{
    private static readonly string PolicyHash = "sha256:" + new string('a', 64);
    private static readonly string ExceptionsHash = "sha256:" + new string('b', 64);

    /// <summary>The agent role's set (contracts/permissions.json): no <c>decision:read</c>, so the route refuses it.</summary>
    private static readonly string[] AgentPermissions =
        ["artifact:write", "event:write", "job:claim", "job:execute"];

    private readonly PostgresTenancyFixture _fixture;

    public RunDecisionCiTokenTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The minted token reads its own project's evaluated run; an unknown id, a run of a sibling project of the same
    /// org (read with a token scoped to that sibling) and a foreign org's run all answer the same 404 body, so the
    /// route tells the caller nothing about runs outside its scope.
    /// </summary>
    [Fact]
    public async Task MintedCiToken_SeesItsOwnRun_AndEveryOtherRunIsTheSameUnknownId()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var evaluationId = await InsertEvaluationAsync(world, runId, await SeedPolicyRevisionAsync(world), "PASS");
        var token = await MintAsync(world.Client, world);

        var own = await GetAsync(world.Client, runId, Bearer(token));

        own.Status.ShouldBe(HttpStatusCode.OK);
        own.Text("decision").ShouldBe("PASS");
        own.Text("evaluation_id").ShouldBe(evaluationId.ToString("D"));

        // The unknown-run envelope is the yardstick every out-of-scope answer has to match character for character,
        // apart from the per-request `request_id`.
        var unknown = await GetAsync(world.Client, Guid.CreateVersion7(), Bearer(token));
        unknown.Status.ShouldBe(HttpStatusCode.NotFound);
        unknown.ErrorCode.ShouldBe("not_found");

        // Another project of the same org: a real token minted for it, reading the first project's run.
        var siblingProject = await CreateProjectAsync(world.Client, world.User, world.Org, "sibling");
        var siblingScope = await MintAsync(world.Client, world with { Project = siblingProject });
        var sibling = await GetAsync(world.Client, runId, Bearer(siblingScope));
        sibling.Status.ShouldBe(HttpStatusCode.NotFound);
        ErrorEnvelope(sibling).ShouldBe(ErrorEnvelope(unknown), "a sibling project's run is not an existence oracle");

        // A foreign org's run, read with the first project's token.
        var foreignUser = Guid.CreateVersion7();
        var foreignOrg = await CreateOrgAsync(world.Client, foreignUser, Unique.Slug("rival"));
        await CreateProjectAsync(world.Client, foreignUser, foreignOrg, "rival-project");
        var foreign = (await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, foreignOrg, 1)).RunId;
        var crossOrg = await GetAsync(world.Client, foreign, Bearer(token));
        crossOrg.Status.ShouldBe(HttpStatusCode.NotFound);
        ErrorEnvelope(crossOrg).ShouldBe(ErrorEnvelope(unknown), "a foreign run is not an existence oracle");
    }

    /// <summary>
    /// The permission, not the credential: an ingest token minted without <c>decision:read</c> is refused 403 before
    /// the run is read, and an agent-role service token is refused the same way.
    /// </summary>
    [Fact]
    public async Task TokenWithoutDecisionRead_IsForbiddenBeforeTheRunIsRead()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);

        var narrow = await MintAsync(world.Client, world, scopes: ["event:write"]);
        var narrowAnswer = await GetAsync(world.Client, runId, Bearer(narrow));
        narrowAnswer.Status.ShouldBe(HttpStatusCode.Forbidden);
        narrowAnswer.ErrorCode.ShouldBe("forbidden");

        var agent = AgentToken(world);
        var agentAnswer = await GetAsync(world.Client, runId, Bearer(agent));
        agentAnswer.Status.ShouldBe(HttpStatusCode.Forbidden);
        agentAnswer.ErrorCode.ShouldBe("forbidden");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>One migrated database, an org and its project, for a host that mints real CI tokens.</summary>
    private async Task<World> StartAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.Ci(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, Unique.Slug("checkout"));

        return new World(api, client, database, user, org, project);
    }

    /// <summary>
    /// A real <c>ci_producer</c> token: an ingest token of the project carrying <paramref name="scopes"/> (the whole
    /// menu by default), exchanged at <c>POST /v1/ci/token</c> — the CLI's own path, not a hand-signed claim set.
    /// </summary>
    private static async Task<string> MintAsync(HttpClient client, World world, string[]? scopes = null)
    {
        var created = await client.SendAsync(
            HttpMethod.Post,
            $"/v1/projects/{world.Project}/ingest-tokens",
            world.Admin,
            new { name = "gate", scopes });
        created.Status.ShouldBe(HttpStatusCode.Created);

        var exchanged = await client.SendAsync(
            HttpMethod.Post, "/v1/ci/token", _ => { }, new { ingest_token = created.Text("secret") });
        exchanged.Status.ShouldBe(HttpStatusCode.OK);

        return exchanged.Text("token")!;
    }

    /// <summary>
    /// An agent-pool service token: the same claim shape a mint produces, signed with the host's key, carrying
    /// the <c>agent</c> role's set. A hand-signed token, because no route mints <c>agent</c> tokens yet.
    /// </summary>
    private static string AgentToken(World world) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = CiTestKeys.Issuer,
            Audience = CiTestKeys.Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(TenancyApiFactory.Keys.SigningKey), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["sub"] = $"agent:{world.Project:D}",
                ["jti"] = Guid.CreateVersion7().ToString("D"),
                ["token_use"] = "ci_producer",
                ["org_id"] = world.Org.ToString("D"),
                ["project_id"] = world.Project.ToString("D"),
                ["binding_id"] = Guid.CreateVersion7().ToString("D"),
                ["provider"] = "agent_pool",
                ["perm"] = AgentPermissions,
            },
        });

    /// <summary>A run of the org's first project, seeded by the migration role so its parents exist.</summary>
    private async Task<Guid> SeedRunAsync(World world) =>
        (await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, world.Org, 1)).RunId;

    /// <summary>One <c>policy_revision</c> of the world's project, written by the app role: an evaluation needs one.</summary>
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

    /// <summary>One live <c>evaluation</c> row of the run, the verdict the route serves.</summary>
    private async Task<Guid> InsertEvaluationAsync(World world, Guid runId, Guid policyRevisionId, string decision)
    {
        var id = Guid.CreateVersion7();

        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(
            """
            insert into public.evaluation
              (id, org_id, run_id, evidence_revision, policy_revision_id, policy_hash, manifest_hash, completeness,
               missing, outcome, decision, reasons, evaluator_version, mode, exceptions_hash, exceptions_as_of)
            values
              (@id, @org_id, @run_id, 1, @policy_revision_id, @policy_hash, @manifest_hash, 'complete',
               '{}', 'all_passed', @decision, '{}', '1.0.0', 'live', @exceptions_hash, now())
            """,
            ("id", id),
            ("org_id", world.Org),
            ("run_id", runId),
            ("policy_revision_id", policyRevisionId),
            ("policy_hash", PolicyHash),
            ("manifest_hash", "manifest-" + runId.ToString("N")),
            ("decision", decision),
            ("exceptions_hash", ExceptionsHash))).ShouldBe(1);
        await scope.CommitAsync();

        return id;
    }

    /// <summary>The CI credential as a request header.</summary>
    private static Action<HttpRequestHeaders> Bearer(string token) =>
        headers => headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

    /// <summary>The route's request, read with the given credential.</summary>
    private static Task<ApiAnswer> GetAsync(HttpClient client, Guid runId, Action<HttpRequestHeaders> caller) =>
        client.SendAsync(
            HttpMethod.Get,
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"/v1/runs/{runId}/decision"),
            caller);

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

    /// <summary>One migrated database and the org its cases build; disposed with the host.</summary>
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

    /// <summary>
    /// The whole error envelope as comparable text, with <c>request_id</c> normalised because it differs per request:
    /// every other member — code, message, details and retryable — must match to the character. The same shape as the
    /// normaliser in <c>RunDecisionApiTests.NotFound.cs</c>, which is private to its own partial class.
    /// </summary>
    private static string ErrorEnvelope(ApiAnswer answer)
    {
        var error = answer.Body.GetProperty("error");
        var requestId = error.GetProperty("request_id").GetString();

        return string.Join(
            "\n",
            "code=" + error.GetProperty("code").GetString(),
            "message=" + error.GetProperty("message").GetString(),
            "retryable=" + error.GetProperty("retryable").GetBoolean(),
            "details=" + error.GetProperty("details").GetRawText(),
            "request_id=" + (string.IsNullOrEmpty(requestId) ? "<absent>" : "<set>"));
    }
}
