// Request URLs are written inline so each test reads as the request it sends.
#pragma warning disable CA1861
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Orchestrator.Domain;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// F52 (S143) through the real host and a real PostgreSQL: <c>GET /v1/runs/{r}/attempts</c> reports one run's
/// <c>case_attempt</c> rows (0032) in <c>(execution_key, attempt_no)</c> ascending order — so one execution's attempts
/// are adjacent and in attempt order — with the producer's <c>raw_status</c> beside the server's
/// <c>normalized_status</c>, the row's own timestamps and duration (null where unrecorded), its count of <c>ready</c>
/// artifacts and <c>healing_candidate</c> null (no candidate row exists until 08.3.1). The opaque cursor is the
/// 00 §9.1 keyset over that pair, and an unknown run, another tenant's run and a sibling project's run are the one
/// 404 <c>not_found</c> of <c>GET /v1/runs/{r}</c> (00 §5.4: no existence oracle).
/// </summary>
[Trait("Category", "Docker")]
public sealed class ListRunAttemptsTests
{
    private readonly PostgresTenancyFixture _fixture;

    /// <summary>A well-formed artifact checksum, <c>sha256:&lt;64 hex&gt;</c> (0028).</summary>
    private const string Checksum = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public ListRunAttemptsTests(PostgresTenancyFixture fixture) => _fixture = fixture;

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

    /// <summary>
    /// One <c>case_attempt</c> row to seed. <paramref name="Normalized"/> defaults to the server's normalisation of
    /// <paramref name="Raw"/>; the <c>blocked</c> rows pass both explicitly, because only the blocked-attempt resolver
    /// assigns that value (05.3.3-T2.2) and a producer's <c>blocked</c> raw status normalises to <c>error</c>.
    /// </summary>
    private sealed record Attempt(
        string ExecutionKey,
        Guid CaseId,
        int AttemptNo,
        string Raw,
        string? Normalized = null,
        DateTimeOffset? StartedAt = null,
        DateTimeOffset? FinishedAt = null,
        int? DurationMs = null);

    // ------------------------------------------------------------------ ordering and the retry chain

    /// <summary>
    /// AC: rows are ordered <c>(execution_key, attempt_no)</c> ascending, so one execution's attempts are adjacent and
    /// in attempt order — the retry chain IS the pair, since there is no <c>retry_of</c> column.
    /// </summary>
    [Fact]
    public async Task ListRunAttempts_OrdersByExecutionKeyThenAttemptNo_RetryAdjacent()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2);
        var first = plan.Executions[0];
        var second = plan.Executions[1];

        await InsertAttemptsAsync(
            world,
            plan.RunId,
            new Attempt(first.ExecutionKey, first.CaseId, 1, "passed"),
            new Attempt(first.ExecutionKey, first.CaseId, 2, "passed"),
            new Attempt(second.ExecutionKey, second.CaseId, 1, "failed"));

        var answer = await GetAsync(world, plan.RunId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var items = Items(answer);
        items.GetArrayLength().ShouldBe(3);

        var positions = items.EnumerateArray()
            .Select(item => (Key: item.GetProperty("execution_key").GetString()!, AttemptNo: item.GetProperty("attempt_no").GetInt32()))
            .ToList();

        // The whole page is ascending on the pair the unique constraint keys on.
        positions.ShouldBe(
            [.. positions.OrderBy(position => position.Key, StringComparer.Ordinal).ThenBy(position => position.AttemptNo)],
            "the page must be ordered (execution_key, attempt_no) ascending");

        // One execution's attempts are adjacent, attempt 1 before attempt 2: the retry of the first execution.
        var retryIndex = positions.FindIndex(position => position.Key == first.ExecutionKey && position.AttemptNo == 2);
        retryIndex.ShouldBeGreaterThan(0, "attempt 2 of the first execution must be on the page");
        positions[retryIndex - 1].ShouldBe(
            (first.ExecutionKey, 1),
            "attempt 2 of one execution must follow attempt 1 of the same execution");

        positions.Count(position => position.Key == first.ExecutionKey).ShouldBe(2);
        items.EnumerateArray().ShouldAllBe(item => item.GetProperty("attempt_id").GetGuid() != Guid.Empty);
        items.EnumerateArray().ShouldAllBe(item => item.GetProperty("case_id").GetGuid() != Guid.Empty);
    }

    // ------------------------------------------------------------------ raw vs normalised status

    /// <summary>
    /// AC3 (00 §8.5): the producer's <c>raw_status</c> is reported verbatim beside the server's normalised value — an
    /// unknown raw spelling is <c>error</c>, never silently the raw string, and a server-assigned <c>blocked</c> row is
    /// never reported as <c>passed</c>.
    /// </summary>
    [Fact]
    public async Task ListRunAttempts_ReportsRawBesideNormalisedStatus()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2);
        var first = plan.Executions[0];
        var second = plan.Executions[1];

        CaseAttemptStatus.Normalize("PASSED").ShouldBe(CaseAttemptStatus.Error);

        await InsertAttemptsAsync(
            world,
            plan.RunId,
            new Attempt(first.ExecutionKey, first.CaseId, 1, "PASSED"),
            new Attempt(second.ExecutionKey, second.CaseId, 1, "blocked", Normalized: CaseAttemptStatus.Blocked));

        var answer = await GetAsync(world, plan.RunId);
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);

        var rows = Items(answer).EnumerateArray()
            .ToDictionary(item => item.GetProperty("execution_key").GetString()!, item => item);

        var unknownRaw = rows[first.ExecutionKey];
        unknownRaw.GetProperty("raw_status").GetString().ShouldBe("PASSED");
        unknownRaw.GetProperty("normalized_status").GetString().ShouldBe(CaseAttemptStatus.Error);

        var blocked = rows[second.ExecutionKey];
        blocked.GetProperty("raw_status").GetString().ShouldBe("blocked");
        blocked.GetProperty("normalized_status").GetString().ShouldBe(CaseAttemptStatus.Blocked);
        blocked.GetProperty("normalized_status").GetString().ShouldNotBe(CaseAttemptStatus.Passed);
    }

    // ------------------------------------------------------------------ duration / timestamps

    /// <summary>AC: <c>duration_ms</c> is the row's own value and null when unrecorded, as are the two instants.</summary>
    [Fact]
    public async Task ListRunAttempts_UnrecordedDurationAndInstants_AreNull()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2);
        var first = plan.Executions[0];
        var second = plan.Executions[1];
        var started = new DateTimeOffset(2026, 10, 29, 9, 30, 0, TimeSpan.Zero);

        await InsertAttemptsAsync(
            world,
            plan.RunId,
            new Attempt(first.ExecutionKey, first.CaseId, 1, "passed"),
            new Attempt(
                second.ExecutionKey,
                second.CaseId,
                1,
                "passed",
                StartedAt: started,
                FinishedAt: started.AddMilliseconds(1500),
                DurationMs: 1500));

        var answer = await GetAsync(world, plan.RunId);
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);

        var rows = Items(answer).EnumerateArray()
            .ToDictionary(item => item.GetProperty("execution_key").GetString()!, item => item);

        var unrecorded = rows[first.ExecutionKey];
        unrecorded.GetProperty("duration_ms").ValueKind.ShouldBe(JsonValueKind.Null);
        unrecorded.GetProperty("started_at").ValueKind.ShouldBe(JsonValueKind.Null);
        unrecorded.GetProperty("finished_at").ValueKind.ShouldBe(JsonValueKind.Null);

        var recorded = rows[second.ExecutionKey];
        recorded.GetProperty("duration_ms").GetInt32().ShouldBe(1500);
        recorded.GetProperty("started_at").GetDateTimeOffset().ShouldBe(started);
        recorded.GetProperty("finished_at").GetDateTimeOffset().ShouldBe(started.AddMilliseconds(1500));
    }

    // ------------------------------------------------------------------ artifact count

    /// <summary>
    /// AC: <c>artifact_count</c> counts the attempt's <c>artifact</c> rows in state <c>ready</c> only — a pending,
    /// rejected or deleted artifact is not counted — and it is the count of the row's own
    /// <c>(execution_key, attempt_no)</c> slot, not of the run.
    /// </summary>
    [Fact]
    public async Task ListRunAttempts_ArtifactCount_CountsOnlyReadyArtifactsOfTheSlot()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);
        var execution = plan.Executions[0];

        await InsertAttemptsAsync(
            world,
            plan.RunId,
            new Attempt(execution.ExecutionKey, execution.CaseId, 1, "passed"),
            new Attempt(execution.ExecutionKey, execution.CaseId, 2, "failed"));

        await InsertArtifactsAsync(world, plan.RunId, execution.ExecutionKey, 1, ArtifactState.Ready, count: 2);
        await InsertArtifactsAsync(world, plan.RunId, execution.ExecutionKey, 1, ArtifactState.PendingUpload);
        await InsertArtifactsAsync(world, plan.RunId, execution.ExecutionKey, 1, ArtifactState.Deleted);
        await InsertArtifactsAsync(world, plan.RunId, execution.ExecutionKey, 2, ArtifactState.Ready);

        var answer = await GetAsync(world, plan.RunId);
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);

        var rows = Items(answer).EnumerateArray()
            .ToDictionary(item => item.GetProperty("attempt_no").GetInt32(), item => item);

        rows[1].GetProperty("artifact_count").GetInt32().ShouldBe(2, "only the two ready artifacts of attempt 1 count");
        rows[2].GetProperty("artifact_count").GetInt32().ShouldBe(1, "attempt 2's count never includes attempt 1's artifacts");
        Items(answer).EnumerateArray().ShouldAllBe(
            item => item.GetProperty("healing_candidate").ValueKind == JsonValueKind.Null,
            "no candidate row exists until 08.3.1, so every row reports null rather than a marker");
    }

    // ------------------------------------------------------------------ pagination

    /// <summary>
    /// AC: the page walks the whole run at <c>limit=1</c>, one row per page, in the same order a single page reports,
    /// and <c>next_cursor</c> is null exactly on the last page.
    /// </summary>
    [Fact]
    public async Task ListRunAttempts_LimitOne_WalksEveryRowInOneOrder()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);
        var execution = plan.Executions[0];

        await InsertAttemptsAsync(
            world,
            plan.RunId,
            new Attempt(execution.ExecutionKey, execution.CaseId, 1, "passed"),
            new Attempt(execution.ExecutionKey, execution.CaseId, 2, "failed"),
            new Attempt(execution.ExecutionKey, execution.CaseId, 3, "passed"));

        var all = await GetAsync(world, plan.RunId);
        all.Status.ShouldBe(HttpStatusCode.OK, all.ErrorMessage);
        var expected = Items(all).EnumerateArray()
            .Select(item => item.GetProperty("attempt_id").GetGuid())
            .ToList();
        expected.Count.ShouldBe(3);

        var walked = new List<Guid>();
        string? cursor = null;
        for (var page = 0; page < 5; page++)
        {
            var answer = await GetAsync(world, plan.RunId, query: $"?limit=1{(cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}")}");
            answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);

            var items = Items(answer);
            items.GetArrayLength().ShouldBe(1, "limit=1 is one row per page");
            walked.Add(items[0].GetProperty("attempt_id").GetGuid());

            cursor = answer.Body.GetProperty("next_cursor").GetString();
            if (cursor is null)
            {
                break;
            }
        }

        walked.ShouldBe(expected, "the cursor walk must repeat the single-page order, skipping and repeating nothing");
        cursor.ShouldBeNull("the last page's next_cursor ends the walk");
    }

    /// <summary>AC: <c>limit</c> is bounded — 200 is accepted and 201 (or 0) is 422 <c>request_invalid</c> at <c>limit</c>.</summary>
    [Theory]
    [InlineData("200", HttpStatusCode.OK)]
    [InlineData("201", HttpStatusCode.UnprocessableContent)]
    [InlineData("0", HttpStatusCode.UnprocessableContent)]
    public async Task ListRunAttempts_LimitBoundary(string limit, HttpStatusCode expected)
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);
        var execution = plan.Executions[0];
        await InsertAttemptsAsync(world, plan.RunId, new Attempt(execution.ExecutionKey, execution.CaseId, 1, "passed"));

        var answer = await GetAsync(world, plan.RunId, query: $"?limit={limit}");

        answer.Status.ShouldBe(expected, answer.ErrorMessage);
        if (expected == HttpStatusCode.OK)
        {
            Items(answer).GetArrayLength().ShouldBe(1);
        }
        else
        {
            answer.ErrorCode.ShouldBe("request_invalid");
            DetailPath(answer).ShouldBe("limit");
        }
    }

    /// <summary>
    /// AC: a cursor this endpoint did not issue is 422 <c>request_invalid</c> at <c>cursor</c> — a malformed value and a
    /// well-formed but non-canonical one (uppercase hex, padded attempt number) alike, never a guessed page position.
    /// </summary>
    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz-1")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA-1")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-01")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-0")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task ListRunAttempts_BadCursor_Is422AtCursor(string cursor)
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);

        var answer = await GetAsync(world, plan.RunId, query: $"?cursor={Uri.EscapeDataString(cursor)}");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableContent, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        DetailPath(answer).ShouldBe("cursor");
    }

    // ------------------------------------------------------------------ 404s and the empty page

    /// <summary>AC: an unknown run id is 404 <c>not_found</c>, the same answer another tenant's run gets.</summary>
    [Fact]
    public async Task ListRunAttempts_UnknownRun_404()
    {
        await using var world = await StartAsync();

        var answer = await GetAsync(world, Guid.CreateVersion7());

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("not_found");
    }

    /// <summary>
    /// AC (00 §5.4): another tenant's run is invisible under RLS, and the answer is byte-for-byte the unknown-id one —
    /// the same code, message and body — so the attempts page is no existence oracle.
    /// </summary>
    [Fact]
    public async Task ListRunAttempts_CrossTenant_IsTheSame404Body()
    {
        await using var world = await StartAsync();

        var otherUser = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(world.Client, otherUser, Unique.Slug("rival"));
        await CreateProjectAsync(world.Client, otherUser, otherOrg, Unique.Slug("rival-project"));
        var foreign = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, otherOrg, 1);

        var foreignAnswer = await GetAsync(world, foreign.RunId);
        var unknownAnswer = await GetAsync(world, Guid.CreateVersion7());

        foreignAnswer.Status.ShouldBe(HttpStatusCode.NotFound);
        foreignAnswer.ErrorCode.ShouldBe(unknownAnswer.ErrorCode);
        foreignAnswer.ErrorMessage.ShouldBe(unknownAnswer.ErrorMessage, "an existence oracle is a leak even when only the code matches");
    }

    /// <summary>
    /// §5: a service/CI credential can never hold <c>run:read</c> (outside the service-role ceiling), so a token
    /// scoped to a sibling project is 403 <c>forbidden</c> — exactly <c>GET /v1/runs/{r}</c>'s answer. The handler's
    /// scope mismatch, which would map to the 404, is therefore defensive and unreachable over HTTP (GetRun.cs).
    /// </summary>
    [Fact]
    public async Task ListRunAttempts_SiblingProjectCredential_Forbidden()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 1);
        var sibling = await CreateProjectAsync(world.Client, world.User, world.Org, "sibling");

        var answer = await GetAsync(
            world,
            plan.RunId,
            TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: sibling, servicePermissions: ["run:read"]));

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        answer.ErrorCode.ShouldBe("forbidden");
    }

    /// <summary>AC: a run with no attempt row answers 200 with empty <c>items</c> and a null cursor — never a 404.</summary>
    [Fact]
    public async Task ListRunAttempts_RunWithoutAttempts_IsAnEmptyPage()
    {
        await using var world = await StartAsync();
        var plan = await SeedPlanAsync(world, 2);

        var answer = await GetAsync(world, plan.RunId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        Items(answer).GetArrayLength().ShouldBe(0);
        answer.Body.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ------------------------------------------------------------------ seeding

    /// <summary>One shared migrated database with its own org and project, created through the real registry routes.</summary>
    private async Task<World> StartAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, Unique.Slug("checkout"));

        return new World(api, client, database, user, org, project);
    }

    /// <summary>
    /// The run and its planned executions, seeded as the migration role into the org's first project (the project
    /// <see cref="StartAsync"/> creates), so every attempt row below has its <c>planned_execution</c> and
    /// <c>test_case</c> parents.
    /// </summary>
    private Task<PlannedExecutionSeeder.SeededRun> SeedPlanAsync(World world, int count) =>
        PlannedExecutionSeeder.SeedPlanAsync(_fixture, world.Database, world.Org, count);

    /// <summary>Writes the attempt rows as the app role inside one committed tenant transaction.</summary>
    private async Task InsertAttemptsAsync(World world, Guid runId, params Attempt[] attempts)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        foreach (var attempt in attempts)
        {
            (await scope.ExecuteAsync(
                """
                insert into public.case_attempt
                  (id, org_id, run_id, execution_key, case_id, attempt_no, raw_status, normalized_status, started_at, finished_at, duration_ms)
                values (@id, @org_id, @run_id, @execution_key, @case_id, @attempt_no, @raw_status, @normalized_status, @started_at, @finished_at, @duration_ms)
                """,
                ("id", Guid.CreateVersion7()),
                ("org_id", world.Org),
                ("run_id", runId),
                ("execution_key", attempt.ExecutionKey),
                ("case_id", attempt.CaseId),
                ("attempt_no", attempt.AttemptNo),
                ("raw_status", attempt.Raw),
                ("normalized_status", attempt.Normalized ?? CaseAttemptStatus.Normalize(attempt.Raw)),
                ("started_at", attempt.StartedAt is { } started ? started : DBNull.Value),
                ("finished_at", attempt.FinishedAt is { } finished ? finished : DBNull.Value),
                ("duration_ms", attempt.DurationMs is { } duration ? duration : DBNull.Value)))
                .ShouldBe(1);
        }

        await scope.CommitAsync();
    }

    /// <summary>
    /// <paramref name="count"/> artifacts of one attempt slot in <paramref name="state"/>, as the app role: the staging
    /// object key shape 0028 admits, so a row in any state satisfies the column CHECK.
    /// </summary>
    private async Task InsertArtifactsAsync(World world, Guid runId, string executionKey, int attemptNo, string state, int count = 1)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);

        for (var index = 0; index < count; index++)
        {
            var id = Guid.CreateVersion7();
            (await scope.ExecuteAsync(
                """
                insert into public.artifact
                  (id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, media_type, checksum, sensitivity, scan_state, state)
                values (@id, @org_id, @project_id, @run_id, @execution_key, @attempt_no, 'trace', @object_key, 'application/zip', @checksum, 'normal', 'pending', @state)
                """,
                ("id", id),
                ("org_id", world.Org),
                ("project_id", world.Project),
                ("run_id", runId),
                ("execution_key", executionKey),
                ("attempt_no", attemptNo),
                ("object_key", string.Create(CultureInfo.InvariantCulture, $"pending/{world.Org}/{id}")),
                ("checksum", Checksum),
                ("state", state)))
                .ShouldBe(1);
        }

        await scope.CommitAsync();
    }

    // ------------------------------------------------------------------ request and body helpers

    private static Task<ApiAnswer> GetAsync(
        World world,
        Guid runId,
        Action<HttpRequestHeaders>? caller = null,
        string? query = null) =>
        world.Client.SendAsync(
            HttpMethod.Get,
            $"/v1/runs/{runId}/attempts{query ?? string.Empty}",
            caller ?? world.Admin);

    private static JsonElement Items(ApiAnswer answer) => answer.Body.GetProperty("items");

    /// <summary>The first <c>details[].path</c> of a 422, or null when the body carries none.</summary>
    private static string? DetailPath(ApiAnswer answer) =>
        answer.Body.GetProperty("error").TryGetProperty("details", out var details) && details.GetArrayLength() > 0
            ? details[0].GetProperty("path").GetString()
            : null;
}
