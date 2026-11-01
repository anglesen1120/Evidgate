// Request URLs are written inline so each test reads as the request it sends.
#pragma warning disable CA1861
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// <c>GET /v1/runs/{run_id}/evidence-revisions</c> through the real host and a real PostgreSQL: the run's immutable
/// <c>evidence_revision</c> rows (<c>0049</c>) newest first (<c>revision_no</c> descending), each with the completeness
/// and reason codes it was frozen with, plus the id of its <c>live</c> <c>evaluation</c> row (<c>0059</c>) or null
/// while none exists. A <c>shadow</c> evaluation is a second opinion and is never served. The opaque cursor is the
/// single <c>revision_no</c> keyset, a bad cursor or an integer <c>limit</c> outside 1–200 (<c>-1</c> included) is
/// 422, a <c>limit</c> that is not the canonical spelling of an integer is the route's 400, and an unknown run,
/// another tenant's run and a sibling project's run are the one 404
/// <c>not_found</c> of <c>GET /v1/runs/{r}</c> (00 §5.4: no existence oracle).
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class ListEvidenceRevisionsTests
{
    private const string PolicyHash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string ExceptionsHash = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private const string ManifestHash = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    private readonly PostgresTenancyFixture _fixture;

    public ListEvidenceRevisionsTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project)
        : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> Admin => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>One frozen revision to seed: its number, verdict inputs and reason codes.</summary>
    private sealed record Revision(int RevisionNo, string Completeness, string Missing = "{}");

    // ------------------------------------------------------------------ ordering and pagination

    /// <summary>
    /// AC: a run with three revisions pages newest first — <c>limit=2</c> answers <c>[3, 2]</c> with a cursor, the next
    /// page answers <c>[1]</c> with no cursor — so the walk repeats nothing and skips nothing.
    /// </summary>
    [Fact]
    public async Task ListEvidenceRevisions_ThreeRevisions_LimitTwo_PagesNewestFirstWithoutGapOrRepeat()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertRevisionsAsync(world, runId, new Revision(1, "complete"), new Revision(2, "incomplete", "{artifact_missing}"), new Revision(3, "complete"));

        var first = await GetAsync(world, runId, "?limit=2");
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        RevisionNumbers(first).ShouldBe([3, 2]);

        var cursor = first.Body.GetProperty("next_cursor").GetString();
        cursor.ShouldNotBeNullOrEmpty("a full page with more rows left must hand back a cursor");

        var second = await GetAsync(world, runId, $"?limit=2&cursor={Uri.EscapeDataString(cursor!)}");
        second.Status.ShouldBe(HttpStatusCode.OK, second.ErrorMessage);
        RevisionNumbers(second).ShouldBe([1]);
        second.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);

        // The whole walk in one page reports the same order the paged walk did.
        var all = await GetAsync(world, runId);
        all.Status.ShouldBe(HttpStatusCode.OK, all.ErrorMessage);
        RevisionNumbers(all).ShouldBe([3, 2, 1]);
    }

    /// <summary>
    /// AC: a page carries the row's own completeness and reason codes verbatim — the codes are not summarised and a
    /// complete row is empty — plus the instant it was frozen.
    /// </summary>
    [Fact]
    public async Task ListEvidenceRevisions_ReportsTheRowsOwnCompletenessAndMissing()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertRevisionsAsync(world, runId, new Revision(1, "incomplete", "{artifact_missing,missing_shard}"), new Revision(2, "complete"));

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var rows = Items(answer).EnumerateArray().ToDictionary(item => item.GetProperty("revision_no").GetInt32(), item => item);

        rows[1].GetProperty("completeness").GetString().ShouldBe("incomplete");
        Missing(rows[1]).ShouldBe(["artifact_missing", "missing_shard"]);

        rows[2].GetProperty("completeness").GetString().ShouldBe("complete");
        Missing(rows[2]).ShouldBeEmpty("a complete revision carries no reason code");
        rows[2].GetProperty("created_at").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddHours(-1));
    }

    // ------------------------------------------------------------------ evaluation_id

    /// <summary>AC: a revision with no stored evaluation reports <c>evaluation_id: null</c>, never a fabricated id.</summary>
    [Fact]
    public async Task ListEvidenceRevisions_RevisionWithoutEvaluation_IsNull()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertRevisionsAsync(world, runId, new Revision(1, "complete"));

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        Items(answer)[0].GetProperty("evaluation_id").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>AC: a stored <c>live</c> evaluation is reported by its own id, on the revision it evaluated.</summary>
    [Fact]
    public async Task ListEvidenceRevisions_LiveEvaluation_IsItsId()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var policyRevisionId = await SeedPolicyRevisionAsync(world);
        await InsertRevisionsAsync(world, runId, new Revision(1, "complete"), new Revision(2, "incomplete", "{artifact_missing}"));
        var live = await InsertEvaluationAsync(world, runId, policyRevisionId, evidenceRevision: 1, mode: "live", decision: "PASS");

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var rows = Items(answer).EnumerateArray().ToDictionary(item => item.GetProperty("revision_no").GetInt32(), item => item);

        rows[1].GetProperty("evaluation_id").GetGuid().ShouldBe(live);
        rows[2].GetProperty("evaluation_id").ValueKind.ShouldBe(JsonValueKind.Null, "revision 2 was never evaluated");
    }

    /// <summary>
    /// AC: a <c>shadow</c> evaluation is never served — a revision with only a shadow row reports null, and a revision
    /// with both reports the <c>live</c> id, not the shadow one.
    /// </summary>
    [Fact]
    public async Task ListEvidenceRevisions_ShadowEvaluation_IsNeverServed()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        var policyRevisionId = await SeedPolicyRevisionAsync(world);
        await InsertRevisionsAsync(world, runId, new Revision(1, "complete"), new Revision(2, "complete"));
        var live = await InsertEvaluationAsync(world, runId, policyRevisionId, evidenceRevision: 1, mode: "live", decision: "PASS");
        await InsertEvaluationAsync(world, runId, policyRevisionId, evidenceRevision: 1, mode: "shadow", decision: "FAIL");
        await InsertEvaluationAsync(world, runId, policyRevisionId, evidenceRevision: 2, mode: "shadow", decision: "FAIL");

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var rows = Items(answer).EnumerateArray().ToDictionary(item => item.GetProperty("revision_no").GetInt32(), item => item);

        rows[1].GetProperty("evaluation_id").GetGuid().ShouldBe(live, "the live verdict is the run's decision, never the shadow one");
        rows[2].GetProperty("evaluation_id").ValueKind.ShouldBe(JsonValueKind.Null, "a shadow-only revision has no published verdict");
    }

    // ------------------------------------------------------------------ cursor and limit validation

    /// <summary>
    /// AC: the integer <c>limit</c> bounds are 1–200; outside them is 422 <c>request_invalid</c> at <c>limit</c> — a
    /// negative value included, because it is the integer it spells and the endpoint refuses the number, not the
    /// spelling.
    /// </summary>
    [Theory]
    [InlineData("200", HttpStatusCode.OK)]
    [InlineData("201", HttpStatusCode.UnprocessableContent)]
    [InlineData("0", HttpStatusCode.UnprocessableContent)]
    [InlineData("-1", HttpStatusCode.UnprocessableContent)]
    [InlineData("-200", HttpStatusCode.UnprocessableContent)]
    public async Task ListEvidenceRevisions_LimitBoundary(string limit, HttpStatusCode expected)
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertRevisionsAsync(world, runId, new Revision(1, "complete"));

        var answer = await GetAsync(world, runId, $"?limit={limit}");

        answer.Status.ShouldBe(expected, answer.ErrorMessage);
        if (expected == HttpStatusCode.OK)
        {
            RevisionNumbers(answer).ShouldBe([1]);
            return;
        }

        answer.ErrorCode.ShouldBe("request_invalid");
        DetailPath(answer).ShouldBe("limit");
    }

    /// <summary>
    /// AC: a <c>limit</c> that is not the canonical spelling of an integer does not bind as a page size at all, so it
    /// is the route's 400 <c>request_invalid</c> — distinct from the 422 a canonical integer outside the range gets
    /// (<c>-1</c> in the boundary test), so a client can tell "not this number" from "this number is refused". A
    /// signed-prefixed or zero-padded integer (<c>+5</c>, <c>01</c>) is refused here even though its integer value
    /// would be in range: the cursor codec refuses the same non-canonical spellings, and one route does not accept a
    /// spelling another parameter of it rejects.
    /// </summary>
    [Theory]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("+5")]
    [InlineData(" 5")]
    [InlineData("01")]
    [InlineData("1e2")]
    public async Task ListEvidenceRevisions_NonIntegerLimit_Is400AtLimit(string limit)
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertRevisionsAsync(world, runId, new Revision(1, "complete"));

        var answer = await GetAsync(world, runId, $"?limit={Uri.EscapeDataString(limit)}");

        answer.Status.ShouldBe(HttpStatusCode.BadRequest, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        DetailPath(answer).ShouldBe("limit");
    }

    /// <summary>
    /// AC: a cursor this endpoint did not issue is 422 <c>request_invalid</c> at <c>cursor</c> — a malformed value and a
    /// well-formed but non-canonical one (a padded number, a sign, a zero) alike, never a guessed page position.
    /// </summary>
    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("01")]
    [InlineData("+1")]
    [InlineData("0")]
    [InlineData("1.0")]
    [InlineData("9999999999999")]
    public async Task ListEvidenceRevisions_BadCursor_Is422AtCursor(string cursor)
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);
        await InsertRevisionsAsync(world, runId, new Revision(1, "complete"), new Revision(2, "complete"));

        var answer = await GetAsync(world, runId, $"?cursor={Uri.EscapeDataString(cursor)}");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableContent, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        DetailPath(answer).ShouldBe("cursor");
    }

    /// <summary>
    /// A cursor this endpoint did issue is bound by the query's own run predicate: passed to another run it addresses
    /// that run's rows below the number, so a well-formed cursor never widens tenancy or leaks a page position.
    /// </summary>
    [Fact]
    public async Task ListEvidenceRevisions_CursorOfAnotherRun_ReadsOnlyThatRunsRows()
    {
        await using var world = await StartAsync();
        var first = await SeedRunAsync(world);
        var second = await SeedRunAsync(world);
        await InsertRevisionsAsync(world, first, new Revision(1, "complete"), new Revision(2, "complete"));
        await InsertRevisionsAsync(world, second, new Revision(1, "complete"), new Revision(2, "complete"), new Revision(3, "complete"));

        var answer = await GetAsync(world, second, $"?cursor={Uri.EscapeDataString("2")}");

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        RevisionNumbers(answer).ShouldBe([1], "the cursor moves a position inside this run, it does not select another run's rows");
    }

    // ------------------------------------------------------------------ 404s and the empty page

    /// <summary>AC: an unknown run id is 404 <c>not_found</c>, the same answer another tenant's run gets.</summary>
    [Fact]
    public async Task ListEvidenceRevisions_UnknownRun_404()
    {
        await using var world = await StartAsync();

        var answer = await GetAsync(world, Guid.CreateVersion7());

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("not_found");
    }

    /// <summary>
    /// AC (00 §5.4): another tenant's run is invisible under RLS, and the answer is byte-for-byte the unknown-id one —
    /// so the revisions page is no existence oracle.
    /// </summary>
    [Fact]
    public async Task ListEvidenceRevisions_CrossTenant_IsTheSame404Body()
    {
        await using var world = await StartAsync();
        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, "rival");
        await CreateProjectAsync(world.Client, otherUser, otherOrg, "rival-project");
        var foreign = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, otherOrg, 1);
        await InsertRevisionsAsync(world with { Org = otherOrg }, foreign.RunId, new Revision(1, "complete"));

        var foreignAnswer = await GetAsync(world, foreign.RunId);
        var unknownAnswer = await GetAsync(world, Guid.CreateVersion7());

        foreignAnswer.Status.ShouldBe(HttpStatusCode.NotFound);
        foreignAnswer.ErrorCode.ShouldBe(unknownAnswer.ErrorCode);
        foreignAnswer.ErrorMessage.ShouldBe(unknownAnswer.ErrorMessage, "an existence oracle is a leak even when only the code matches");
    }

    /// <summary>
    /// AC: without <c>run:read</c> the route is refused — a service credential cannot hold the permission (it is
    /// outside the service-role ceiling), so the answer is 403 <c>forbidden</c>, and the same run read as the admin
    /// answers 200 (the deny test's positive control).
    /// </summary>
    [Fact]
    public async Task ListEvidenceRevisions_WithoutRunRead_IsForbidden()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);

        var denied = await GetAsync(
            world,
            runId,
            caller: TenancyApiFactory.Resolved(serviceOrg: world.Org, servicePermissions: ["run:create"]));
        var control = await GetAsync(world, runId);

        denied.Status.ShouldBe(HttpStatusCode.Forbidden);
        denied.ErrorCode.ShouldBe("forbidden");
        control.Status.ShouldBe(HttpStatusCode.OK, control.ErrorMessage);
    }

    /// <summary>AC: a run with no revision answers 200 with empty <c>items</c> and a null cursor — never a 404.</summary>
    [Fact]
    public async Task ListEvidenceRevisions_RunWithoutRevisions_IsAnEmptyPage()
    {
        await using var world = await StartAsync();
        var runId = await SeedRunAsync(world);

        var answer = await GetAsync(world, runId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        Items(answer).GetArrayLength().ShouldBe(0);
        answer.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ------------------------------------------------------------------ seeding

    /// <summary>One migrated database with an org and its project, created through the real registry routes.</summary>
    private async Task<World> StartAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
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
    /// The frozen revisions of one run, as the app role inside one committed tenant transaction: <c>revision_no</c>
    /// must be <c>max + 1</c> (<c>0049</c>'s insert guard), so the rows are written in ascending order.
    /// </summary>
    private async Task InsertRevisionsAsync(World world, Guid runId, params Revision[] revisions)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        foreach (var revision in revisions.OrderBy(revision => revision.RevisionNo))
        {
            (await scope.ExecuteAsync(
                $"""
                insert into public.evidence_revision
                  (id, org_id, run_id, revision_no, completeness, missing, event_watermark, manifest_hash)
                values (@id, @org_id, @run_id, @revision_no, @completeness,
                        '{revision.Missing}'::text[], @event_watermark, @manifest_hash)
                """,
                ("id", Guid.CreateVersion7()),
                ("org_id", world.Org),
                ("run_id", runId),
                ("revision_no", revision.RevisionNo),
                ("completeness", revision.Completeness),
                ("event_watermark", 0),
                ("manifest_hash", ManifestHash)))
                .ShouldBe(1);
        }

        await scope.CommitAsync();
    }

    /// <summary>
    /// One <c>policy_revision</c> of the world's project, written by the migration role: an <c>evaluation</c> row needs
    /// a real revision to point at, and the policy route's body schema is not what this test is about.
    /// </summary>
    private async Task<Guid> SeedPolicyRevisionAsync(World world, int revisionNo = 1)
    {
        var revision = Guid.CreateVersion7();

        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            await owner.ExecuteAsync(
                """
                insert into public.policy_revision (id, org_id, project_id, revision_no, hash, body, created_by)
                values (@id, @org_id, @project_id, @revision_no, @hash, @body::jsonb, @created_by)
                """,
                ("id", revision),
                ("org_id", world.Org),
                ("project_id", world.Project),
                ("revision_no", revisionNo),
                ("hash", PolicyHash),
                ("body", """{"flaky_budget":3}"""),
                ("created_by", world.User));
            await owner.CommitAsync();
        }

        return revision;
    }

    /// <summary>
    /// One <c>evaluation</c> row as the app role, inside one committed tenant transaction. The table is immutable, so
    /// each verdict is a fresh row and its <c>mode</c> is what decides whether the route may serve it.
    /// </summary>
    private async Task<Guid> InsertEvaluationAsync(
        World world,
        Guid runId,
        Guid policyRevisionId,
        int evidenceRevision,
        string mode,
        string decision)
    {
        var id = Guid.CreateVersion7();
        var complete = string.Equals(decision, "PASS", StringComparison.Ordinal);

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            await scope.ExecuteAsync(
                """
                insert into public.evaluation
                  (id, org_id, run_id, evidence_revision, policy_revision_id, policy_hash, manifest_hash, completeness,
                   missing, outcome, decision, reasons, evaluator_version, mode, exceptions_hash, exceptions_as_of)
                values
                  (@id, @org_id, @run_id, @evidence_revision, @policy_revision_id, @policy_hash, @manifest_hash,
                   @completeness, case when @complete then array[]::text[] else array['artifact_missing']::text[] end,
                   @outcome, @decision, @reasons, @evaluator_version, @mode, @exceptions_hash, now())
                """,
                ("id", id),
                ("org_id", world.Org),
                ("run_id", runId),
                ("evidence_revision", evidenceRevision),
                ("policy_revision_id", policyRevisionId),
                ("policy_hash", PolicyHash),
                ("manifest_hash", ManifestHash),
                ("completeness", complete ? "complete" : "incomplete"),
                ("complete", complete),
                ("outcome", complete ? "all_passed" : "has_failures"),
                ("decision", decision),
                ("reasons", complete ? "{}" : "{artifact_missing}"),
                ("evaluator_version", "v1"),
                ("mode", mode),
                ("exceptions_hash", ExceptionsHash));
            await scope.CommitAsync();
        }

        return id;
    }

    // ------------------------------------------------------------------ request and body helpers

    private static Task<ApiAnswer> GetAsync(
        World world,
        Guid runId,
        string? query = null,
        Action<HttpRequestHeaders>? caller = null) =>
        world.Client.SendAsync(
            HttpMethod.Get,
            string.Create(CultureInfo.InvariantCulture, $"/v1/runs/{runId}/evidence-revisions{query ?? string.Empty}"),
            caller ?? world.Admin);

    private static JsonElement Items(ApiAnswer answer) => answer.Body.GetProperty("items");

    private static int[] RevisionNumbers(ApiAnswer answer) =>
        [.. Items(answer).EnumerateArray().Select(item => item.GetProperty("revision_no").GetInt32())];

    private static string[] Missing(JsonElement item) =>
        [.. item.GetProperty("missing").EnumerateArray().Select(code => code.GetString()!)];

    /// <summary>The first <c>details[].path</c> of a refusal, or null when the body carries none.</summary>
    private static string? DetailPath(ApiAnswer answer) =>
        answer.Body.GetProperty("error").TryGetProperty("details", out var details) && details.GetArrayLength() > 0
            ? details[0].GetProperty("path").GetString()
            : null;
}
