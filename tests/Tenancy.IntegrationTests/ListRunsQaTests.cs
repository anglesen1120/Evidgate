// Independent QA (S48Qa) of 05.2.2-T2 AC6: negative/boundary/exploratory cases the lane's own suite does not cover
// (missing-identity rows, a PR number on a branch row, a SHA-256 head_sha, unicode/percent/255-char ref names,
// SQL metacharacters, duplicate query parameters, limit boundaries, a cursor reused under another filter, cursor
// tick extremes, a soft-deleted project, a service credential claiming run:read, an unauthenticated caller and a
// page walking rows that share created_at with an insert in between). It never edits production code or the lane's
// tests; a failure is a defect to report, not to fix here.
#pragma warning disable CA1861
using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>05.2.2-T2 AC6 QA matrix (S48Qa): the run list's boundaries through the real host and PostgreSQL.</summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class ListRunsQaTests
{
    private const string ShaOne = "1111111111111111111111111111111111111111";

    private readonly PostgresTenancyFixture _fixture;

    public ListRunsQaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>
    /// AC6: a row with no identity at all still matches <c>is_fork=false</c> (the column defaults false) and no
    /// other filter — the filters are exact column matches, and a null identity is never a match for a name, a
    /// number, a sha or an event. Its <c>ref</c> block is all null and it is not labelled unverified.
    /// </summary>
    [Fact]
    public async Task ListRunsQa_MissingIdentityRows_OnlyIsForkFalseMatches()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var identified = await CreateRefRunAsync(world, Branch("main", ShaOne));
        var anonymous = await CreateRefRunAsync(world, null);

        (await IdsAsync(world, "is_fork=false")).ShouldBe(new[] { identified, anonymous }, ignoreOrder: true);
        (await IdsAsync(world, "is_fork=true")).ShouldBeEmpty();
        (await IdsAsync(world, "ref_name=main")).ShouldBe(new[] { identified });
        (await IdsAsync(world, "head_sha=" + ShaOne)).ShouldBe(new[] { identified });
        (await IdsAsync(world, "pr_number=1")).ShouldBeEmpty();
        (await IdsAsync(world, "trigger_event=push")).ShouldBeEmpty();

        var item = Items(await ListAsync(world, "is_fork=false")).Single(i => i.GetProperty("run_id").GetGuid() == anonymous);
        item.GetProperty("unverified_ref").GetBoolean().ShouldBeFalse();
        item.GetProperty("ref").GetProperty("kind").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>
    /// AC6, F6: a <c>kind: branch</c> row carrying a <c>pr_number</c> is stored as given and is filterable by it —
    /// the shapes admit the combination, they do not normalise it.
    /// </summary>
    [Fact]
    public async Task ListRunsQa_PrNumberOnBranchRow_IsStoredAndFilterable()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var branchWithPr = await CreateRefRunAsync(world, """{"kind":"branch","name":"main","pr_number":9}""");

        var item = Items(await ListAsync(world, "pr_number=9")).Single();
        item.GetProperty("ref").GetProperty("kind").GetString().ShouldBe("branch");
        item.GetProperty("ref").GetProperty("name").GetString().ShouldBe("main");
        item.GetProperty("ref").GetProperty("pr_number").GetInt32().ShouldBe(9);
        (await IdsAsync(world, "pr_number=9")).ShouldBe(new[] { branchWithPr });
        (await IdsAsync(world, "ref_name=main&pr_number=9")).ShouldBe(new[] { branchWithPr });
        (await IdsAsync(world, "ref_name=main&pr_number=8")).ShouldBeEmpty();
    }

    /// <summary>AC6: <c>head_sha</c> accepts a 64-hex SHA-256 id and matches it exactly; a 40-hex filter misses it and an uppercase 64-hex filter is 422.</summary>
    [Fact]
    public async Task ListRunsQa_HeadShaSha256_ExactAndCaseSensitive()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var sha256 = string.Concat(Enumerable.Repeat("0123456789abcdef", 4)); // 64 lowercase hex chars
        var run = await CreateRefRunAsync(world, Branch("main", sha256));

        (await IdsAsync(world, "head_sha=" + sha256)).ShouldBe(new[] { run });
        (await IdsAsync(world, "head_sha=" + ShaOne)).ShouldBeEmpty();

        var upper = await ListAsync(world, "head_sha=" + sha256.ToUpperInvariant());
        upper.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        upper.ErrorCode.ShouldBe("request_invalid");
        Details(upper).ShouldContain(d => d.GetProperty("path").GetString() == "head_sha");
    }

    /// <summary>
    /// AC6, §9.1: a <c>ref_name</c> filter is a decoded, exact, byte-for-byte literal — unicode, percent-encoded
    /// and the 255-code-point maximum all round-trip; <c>%</c> is a literal, never a LIKE wildcard; 256 is 422.
    /// </summary>
    [Fact]
    public async Task ListRunsQa_RefName_UnicodePercentAndMaximumLength()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var unicode = await CreateRefRunAsync(world, Branch("nhánh-☃-2024", ShaOne));
        var percent = await CreateRefRunAsync(world, Branch("a%b", ShaOne));
        var nonPercent = await CreateRefRunAsync(world, Branch("axb", ShaOne));
        var max = new string('r', 255);
        var longest = await CreateRefRunAsync(world, Branch(max, ShaOne));

        (await IdsAsync(world, "ref_name=" + Uri.EscapeDataString("nhánh-☃-2024"))).ShouldBe(new[] { unicode });
        (await IdsAsync(world, "ref_name=" + Uri.EscapeDataString("a%b"))).ShouldBe(new[] { percent });
        (await IdsAsync(world, "ref_name=" + Uri.EscapeDataString("axb"))).ShouldBe(new[] { nonPercent });
        (await IdsAsync(world, "ref_name=" + Uri.EscapeDataString(max))).ShouldBe(new[] { longest });

        var tooLong = await ListAsync(world, "ref_name=" + Uri.EscapeDataString(new string('r', 256)));
        tooLong.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        Details(tooLong).ShouldContain(d => d.GetProperty("path").GetString() == "ref_name" && d.GetProperty("reason").GetString() == "invalid_value");
    }

    /// <summary>
    /// AC6: SQL metacharacters in <c>ref_name</c> are a literal parameter, not SQL — the exact row matches, a
    /// metacharacter filter matching nothing is an empty 200, and the total row count never changes.
    /// </summary>
    [Fact]
    public async Task ListRunsQa_RefName_SqlMetacharacters_AreLiteral()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var quoted = await CreateRefRunAsync(world, Branch("x' or '1'='1", ShaOne));
        var comment = await CreateRefRunAsync(world, Branch("main--9", ShaOne));

        (await IdsAsync(world, "ref_name=" + Uri.EscapeDataString("x' or '1'='1"))).ShouldBe(new[] { quoted });
        (await IdsAsync(world, "ref_name=" + Uri.EscapeDataString("main--9"))).ShouldBe(new[] { comment });
        (await IdsAsync(world, "ref_name=" + Uri.EscapeDataString("z' or '1'='1"))).ShouldBeEmpty();
        (await IdsAsync(world, "ref_name=" + Uri.EscapeDataString("'; drop table public.run; --"))).ShouldBeEmpty();

        (await _fixture.ScalarAsync<long>(world.Database, "select count(*) from public.run")).ShouldBe(2);
    }

    /// <summary>AC6: a repeated query parameter is resolved to its first value (ASP.NET <c>StringValues[0]</c>), never a union or an error.</summary>
    [Fact]
    public async Task ListRunsQa_DuplicateQueryParams_FirstValueWins()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var a = await CreateRefRunAsync(world, Branch("a", ShaOne));
        var b = await CreateRefRunAsync(world, Branch("b", ShaOne));

        (await IdsAsync(world, "ref_name=a&ref_name=b")).ShouldBe(new[] { a });
        (await IdsAsync(world, "ref_name=b&ref_name=a")).ShouldBe(new[] { b });
    }

    /// <summary>AC6, 00 §9.1: the exact limit boundaries — 1 and 200 are served, 0 and 201 are 422 <c>out_of_range</c>.</summary>
    [Fact]
    public async Task ListRunsQa_LimitBoundaries()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await CreateRefRunAsync(world, Branch("main", ShaOne));

        (await ListAsync(world, "limit=1")).Status.ShouldBe(HttpStatusCode.OK);
        (await ListAsync(world, "limit=200")).Status.ShouldBe(HttpStatusCode.OK);

        foreach (var raw in new[] { "0", "201" })
        {
            var answer = await ListAsync(world, $"limit={raw}");
            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, raw);
            Details(answer).ShouldContain(d => d.GetProperty("path").GetString() == "limit" && d.GetProperty("reason").GetString() == "out_of_range");
        }
    }

    /// <summary>
    /// AC6, §9.1: the cursor is only a position, so a cursor issued under one filter is served under another — the
    /// second filter is applied on top of the position, never widened or reset by it.
    /// </summary>
    [Fact]
    public async Task ListRunsQa_CursorReusedUnderAnotherFilter_IsJustAPosition()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var main = await CreateRefRunAsync(world, Branch("main", ShaOne));
        var feature = await CreateRefRunAsync(world, Branch("feature", ShaOne));

        var pageOne = await ListAsync(world, "limit=1"); // oldest row = main (created before feature)
        OrderedIds(pageOne).ShouldBe(new[] { main });
        var cursor = pageOne.Body.GetProperty("next_cursor").GetString()!;

        var crossed = await ListAsync(world, "ref_name=feature&cursor=" + Uri.EscapeDataString(cursor));
        crossed.Status.ShouldBe(HttpStatusCode.OK, crossed.ErrorMessage);
        OrderedIds(crossed).ShouldBe(new[] { feature });
    }

    /// <summary>
    /// AC6: a well-formed cursor with the zero tick is the earliest possible position (all rows), the maximum tick is
    /// past every row (empty), and a tick outside <c>[0, DateTimeOffset.MaxValue.UtcTicks]</c> is 422 <c>malformed</c>.
    /// </summary>
    [Fact]
    public async Task ListRunsQa_CursorTickExtremes()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var run = await CreateRefRunAsync(world, Branch("main", ShaOne));

        OrderedIds(await ListAsync(world, "cursor=" + Uri.EscapeDataString(Cursor(0)))).ShouldBe(new[] { run });

        var maxAnswer = await ListAsync(world, "cursor=" + Uri.EscapeDataString(Cursor(DateTimeOffset.MaxValue.UtcTicks)));
        maxAnswer.Status.ShouldBe(HttpStatusCode.OK);
        OrderedIds(maxAnswer).ShouldBeEmpty();

        foreach (var invalid in new[] { Cursor(long.MaxValue), Cursor(-1), "ffffffffffffffff-" + Guid.CreateVersion7().ToString("D") })
        {
            var answer = await ListAsync(world, "cursor=" + Uri.EscapeDataString(invalid));
            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, invalid);
            answer.ErrorCode.ShouldBe("request_invalid");
            Details(answer).ShouldContain(d => d.GetProperty("path").GetString() == "cursor" && d.GetProperty("reason").GetString() == "malformed");
        }
    }

    /// <summary>AC6, §9.3: a soft-deleted project is the same 404 <c>not_found</c> as an unknown one, never an empty page.</summary>
    [Fact]
    public async Task ListRunsQa_SoftDeletedProject_404()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await CreateRefRunAsync(world, Branch("main", ShaOne));
        await _fixture.ExecuteAsync(world.Database, $"update public.project set status = 'deleted' where id = '{world.Project:D}'");

        var answer = await ListAsync(world, string.Empty);

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("not_found");
    }

    /// <summary>AC6, §5: <c>run:read</c> is outside the service-role ceiling, so a service credential claiming it is 403 — the claim is malformed, not honoured.</summary>
    [Fact]
    public async Task ListRunsQa_ServiceClaimingRunRead_403()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await CreateRefRunAsync(world, Branch("main", ShaOne));

        var service = TenancyApiFactory.Resolved(serviceOrg: world.Org, serviceProject: world.Project, servicePermissions: ["run:read"]);
        var answer = await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/runs", service);

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>00 §9.2: no caller is 401 <c>unauthenticated</c>.</summary>
    [Fact]
    public async Task ListRunsQa_Unauthenticated_401()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);

        var answer = await world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/runs", _ => { });

        answer.Status.ShouldBe(HttpStatusCode.Unauthorized);
        answer.ErrorCode.ShouldBe("unauthenticated");
    }

    /// <summary>AC6: an unknown query parameter is ignored (the waiver-list precedent), so the page is the unfiltered one.</summary>
    [Fact]
    public async Task ListRunsQa_UnknownQueryParameter_Ignored()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var run = await CreateRefRunAsync(world, Branch("main", ShaOne));

        var answer = await ListAsync(world, "decision=pass&rerun_label=x");

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        OrderedIds(answer).ShouldBe(new[] { run });
    }

    /// <summary>
    /// AC6: rows that share <c>created_at</c> are walked by <c>(created_at, id)</c> alone — a run inserted between
    /// two pages at the same instant is neither repeated nor misordered, and the union follows the database's order.
    /// </summary>
    [Fact]
    public async Task ListRunsQa_SecondPage_InsertAtSameCreatedAt()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        await CreateRefRunAsync(world, Branch("main", ShaOne));
        await CreateRefRunAsync(world, Branch("feature", ShaOne));
        await CreateRefRunAsync(world, Branch("release", ShaOne));
        const string instant = "timestamptz '2026-03-04T05:06:07Z'";
        await _fixture.ExecuteAsync(world.Database, $"update public.run set created_at = {instant} where project_id = '{world.Project:D}'");

        var pageOne = await ListAsync(world, "limit=2");
        pageOne.Status.ShouldBe(HttpStatusCode.OK, pageOne.ErrorMessage);
        var first = OrderedIds(pageOne);
        first.Length.ShouldBe(2);
        var cursor = pageOne.Body.GetProperty("next_cursor").GetString()!;

        var late = await CreateRefRunAsync(world, Branch("late", ShaOne));
        await _fixture.ExecuteAsync(world.Database, $"update public.run set created_at = {instant} where id = '{late:D}'");

        var pageTwo = await ListAsync(world, "limit=10&cursor=" + Uri.EscapeDataString(cursor));
        pageTwo.Status.ShouldBe(HttpStatusCode.OK, pageTwo.ErrorMessage);
        var second = OrderedIds(pageTwo);
        var served = first.Concat(second).ToArray();

        // No repeat, every served id is real, and the concatenation is exactly the database's own
        // (created_at, id) order restricted to the rows the two pages served — duplicates or a misorder would fail.
        second.ShouldNotContain(id => first.Contains(id), "a row already served must never repeat");
        var expected = await OrderedIdsInDatabaseAsync(world);
        served.ShouldAllBe(id => expected.Contains(id));
        served.ShouldBe([.. expected.Where(id => served.Contains(id))], ignoreOrder: false);
    }

    // ------------------------------------------------------------------ helpers

    private static string Branch(string name, string headSha) =>
        $$"""{"kind":"branch","name":"{{name}}","head_sha":"{{headSha}}"}""";

    private static string Cursor(long ticks) => $"{ticks:x16}-{Guid.CreateVersion7():D}";

    /// <summary>One run of the world's manifest with the given <c>ref</c> object, or no identity when <paramref name="refJson"/> is null.</summary>
    private static async Task<Guid> CreateRefRunAsync(RunCreationTests.World world, string? refJson)
    {
        var body = refJson is null
            ? RunCreationTests.RunBody(world.ManifestId)
            : $$"""{"manifest_id":"{{world.ManifestId:D}}","ref":{{refJson}}}""";
        var created = await RunCreationTests.CreateRunAsync(world, body);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        return created.Id("run_id");
    }

    private static Task<ApiAnswer> ListAsync(RunCreationTests.World world, string query) =>
        world.Client.SendAsync(HttpMethod.Get, $"/v1/projects/{world.Project}/runs?{query}", world.Admin);

    private static async Task<Guid[]> IdsAsync(RunCreationTests.World world, string query)
    {
        var answer = await ListAsync(world, query);
        answer.Status.ShouldBe(HttpStatusCode.OK, $"{query}: {answer.ErrorMessage}");
        return OrderedIds(answer);
    }

    private static Guid[] OrderedIds(ApiAnswer answer) =>
        [.. Items(answer).Select(item => item.GetProperty("run_id").GetGuid())];

    private static List<JsonElement> Items(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("items").EnumerateArray()];

    private static List<JsonElement> Details(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()];

    private async Task<Guid[]> OrderedIdsInDatabaseAsync(RunCreationTests.World world) =>
        [.. (await _fixture.ScalarAsync<string>(
                world.Database,
                $"select string_agg(id::text, ',' order by created_at, id) from public.run where project_id = '{world.Project:D}'"))
            .Split(',')
            .Select(Guid.Parse)];
}
