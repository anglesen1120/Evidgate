using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.Modules.Authoring.Application.Queries;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1-T3 AC9: the version timeline of a project (route 22) and one stored version with its content (route 23).
/// </summary>
/// <remarks>
/// <para>
/// <b>Seeded, not driven.</b> The timeline's contract is about ordering, bounds and cursor exactness, so the rows are
/// inserted directly into <c>public.authoring_version</c> (the table is append-only and the writer's own paths are proven
/// by the tree, checklist and manual case suites): every expected order is read back from the database with its own
/// <c>order by created_at desc, id desc</c>, so no assertion restates the API's ordering as an assumption.
/// </para>
/// <para>
/// <b>What the rows prove.</b> Timestamps repeat on purpose (five rows share one microsecond) and five more differ by
/// one microsecond each, so a page boundary inside a tie group and a cursor that rounds instead of truncating are both
/// observable; two rows live in a sibling project of the same org and one in another org, so a missing
/// <c>project_id</c>/<c>org_id</c> predicate cannot pass unnoticed (and that other org's own page must still serve its
/// row). The <c>authoring_version.content_hash</c> check
/// constraint fixes the digest spelling (<c>sha256:&lt;64 hex&gt;</c>), which is what route 23 quotes as its
/// <c>ETag</c>.
/// </para>
/// <para>
/// The plan is asserted against the served statements (<see cref="AuthoringVersionReadSql"/>, through
/// <c>InternalsVisibleTo</c>) with the handler's parameters bound, mirroring <c>ListRunsIndexPlanTests</c>.
/// </para>
/// </remarks>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class AuthoringTimelineTests
{
    /// <summary>The digest every seeded row carries: the column's <c>2026</c>-style check needs 64 lower-case hex.</summary>
    private const string SeededHash = "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    private readonly PostgresTenancyFixture _fixture;

    public AuthoringTimelineTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project,
        Guid SiblingProject,
        Guid OtherOrg,
        Guid OtherOrgProject,
        Guid OtherUser,
        Guid Membership) : IAsyncDisposable
    {
        public Action<HttpRequestHeaders> As => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        public string Versions => $"/v1/projects/{Project}/authoring/versions";

        public string Version(Guid v) => $"{Versions}/{v:D}";

        public string InSibling(Guid v) => $"/v1/projects/{SiblingProject}/authoring/versions/{v:D}";

        public string InOtherOrg(Guid v) => $"/v1/projects/{OtherOrgProject}/authoring/versions/{v:D}";

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private async Task<World> StartAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "acme");
        var project = await CreateProjectAsync(client, user, org, "checkout");
        var sibling = await CreateProjectAsync(client, user, org, "returns");

        var stranger = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(client, stranger, "globex");
        var otherProject = await CreateProjectAsync(client, stranger, otherOrg, "warehouse");

        // The founder's own membership: the only row the seeded `author_membership_id` foreign key accepts.
        var membership = await _fixture.ScalarAsync<Guid>(
            database,
            $"select id from public.membership where org_id = '{org:D}' and user_id = '{user:D}' and status = 'active'");

        return new World(api, client, database, user, org, project, sibling, otherOrg, otherProject, stranger, membership);
    }

    /// <summary>One seeded <c>authoring_version</c> row: the columns route 22 projects, plus the content route 23 serves.</summary>
    /// <param name="Id">The row's id, which ties the keyset order.</param>
    /// <param name="Project">The owning project.</param>
    /// <param name="Entity">The entity the version describes.</param>
    /// <param name="Kind"><c>feature_node</c>, <c>checklist_item</c> or <c>manual_test_case</c>.</param>
    /// <param name="Version">The entity's version number (unique per entity).</param>
    /// <param name="Batch">The change the row belongs to.</param>
    /// <param name="Change"><c>create</c>, <c>update</c>, <c>delete</c>, <c>restore</c> or <c>import</c>.</param>
    /// <param name="Source"><c>manual</c>, <c>ai</c> or <c>import</c>.</param>
    /// <param name="Author">The authoring membership, or null.</param>
    /// <param name="CreatedAt">When the row was appended, to the microsecond.</param>
    /// <param name="Content">The stored document.</param>
    /// <param name="Org">The owning organization, or null for the world's own — a row of another tenant for the
    /// cross-org case.</param>
    private sealed record Seed(
        Guid Id,
        Guid Project,
        Guid Entity,
        string Kind,
        int Version,
        Guid Batch,
        string Change,
        string Source,
        Guid? Author,
        DateTimeOffset CreatedAt,
        string Content = """{"title":"seed"}""",
        Guid? Org = null);

    /// <summary>The RFC 3339 spelling of an instant with all six fractional digits, for SQL and for the query parameter.</summary>
    private static string Instant(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    private static string Literal(Guid? value) => value is { } present ? $"'{present:D}'" : "null";

    /// <summary>Inserts the seeded rows as the superuser (the append-only table's own writer is exercised elsewhere).</summary>
    private async Task SeedAsync(World world, params Seed[] rows)
    {
        if (rows.Length == 0)
        {
            return;
        }

        var values = rows.Select(row => $"""
            ('{row.Id:D}', '{(row.Org ?? world.Org):D}', '{row.Project:D}', '{row.Kind}', '{row.Entity:D}', {row.Version},
             '{row.Batch:D}', '{row.Change}', '{row.Content}'::jsonb, '{SeededHash}', '{row.Source}',
             {Literal(row.Author)}, null, null, '{Instant(row.CreatedAt)}')
            """);

        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.authoring_version
              (id, org_id, project_id, entity_kind, entity_id, version_no, batch_id, change_kind, content, content_hash,
               source, author_membership_id, reason, ai_run_id, created_at)
            values
            {string.Join(",\n", values)};
            """);
    }

    /// <summary>GET route 22 with a raw query string (empty for the unfiltered first page).</summary>
    private static Task<ApiAnswer> VersionsAsync(World world, string query = "") =>
        world.Client.SendAsync(HttpMethod.Get, query.Length == 0 ? world.Versions : $"{world.Versions}?{query}", world.As);

    /// <summary>The <c>version_id</c> values of a page, in the order the API answered them.</summary>
    private static IReadOnlyList<Guid> Ids(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("version_id").GetGuid())];

    private static string? NextCursor(ApiAnswer answer) =>
        answer.Body.GetProperty("next_cursor").ValueKind == JsonValueKind.Null
            ? null
            : answer.Body.GetProperty("next_cursor").GetString();

    /// <summary>
    /// True when a wire <c>created_at</c> is an instant at UTC. The timeline's DTO carries a
    /// <see cref="DateTimeOffset"/>, whose System.Text.Json form spells the offset (<c>+00:00</c>) rather than a
    /// <c>Z</c> suffix — the spelling every other authoring DTO emits and <c>contracts/openapi.yaml</c> types as
    /// <c>format: date-time</c>, so the assertion is the instant (offset zero, for the seeded UTC rows), not a literal.
    /// </summary>
    /// <param name="row">One item of a route 22 page.</param>
    private static bool IsUtcInstant(JsonElement row) =>
        DateTimeOffset.TryParse(
            row.GetProperty("created_at").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var stamp)
        && stamp.Offset == TimeSpan.Zero;

    [Fact]
    public async Task Pagination_Filters_Ordering()
    {
        await using var world = await StartAsync();

        var feature = Guid.CreateVersion7();
        var item = Guid.CreateVersion7();
        var caseEntity = Guid.CreateVersion7();
        var batchA = Guid.CreateVersion7();
        var batchB = Guid.CreateVersion7();

        // 2026-10-01T10:00:00.123456Z exactly: five rows share it, so a page boundary falls inside a tie group.
        var shared = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero).AddTicks(1_234_560);

        var rows = new List<Seed>();

        for (var version = 1; version <= 5; version++)
        {
            rows.Add(new Seed(
                Guid.CreateVersion7(), world.Project, feature, "feature_node", version,
                version <= 2 ? batchA : batchB, "create", "manual",
                version == 3 ? world.Membership : null, shared));
        }

        // Five more versions of the same entity, one microsecond apart: a cursor that rounds (instead of truncating)
        // would skip rows here, so paging with limit 1 across this group is the precision proof.
        for (var step = 1; step <= 5; step++)
        {
            rows.Add(new Seed(
                Guid.CreateVersion7(), world.Project, feature, "feature_node", 5 + step,
                batchB, "update", "manual", null, shared.AddTicks(step * 10)));
        }

        rows.Add(new Seed(
            Guid.CreateVersion7(), world.Project, item, "checklist_item", 1,
            Guid.CreateVersion7(), "create", "ai", world.Membership,
            new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero).AddTicks(10)));

        rows.Add(new Seed(
            Guid.CreateVersion7(), world.Project, caseEntity, "manual_test_case", 1,
            Guid.CreateVersion7(), "create", "import", null,
            new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero)));

        // Newer than every row above and outside the project: a missing predicate would put these first.
        rows.Add(new Seed(
            Guid.CreateVersion7(), world.SiblingProject, Guid.CreateVersion7(), "feature_node", 1,
            Guid.CreateVersion7(), "create", "manual", null,
            new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero)));

        rows.Add(new Seed(
            Guid.CreateVersion7(), world.SiblingProject, Guid.CreateVersion7(), "feature_node", 1,
            Guid.CreateVersion7(), "create", "manual", null,
            new DateTimeOffset(2026, 10, 4, 0, 0, 1, TimeSpan.Zero)));

        rows.Add(new Seed(
            Guid.CreateVersion7(), world.OtherOrgProject, Guid.CreateVersion7(), "feature_node", 1,
            Guid.CreateVersion7(), "create", "import", null,
            new DateTimeOffset(2026, 10, 4, 0, 0, 2, TimeSpan.Zero), Org: world.OtherOrg));

        await SeedAsync(world, rows.ToArray());

        // The expected order is the database's own: `order by created_at desc, id desc` over this project only.
        var expected = await _fixture.ColumnAsync<Guid>(
            world.Database,
            $"""
            select id
              from public.authoring_version
             where org_id = '{world.Org:D}'
               and project_id = '{world.Project:D}'
             order by created_at desc, id desc
            """);

        expected.Count.ShouldBe(12, "twelve rows belong to the project under test");

        // --- the whole page -------------------------------------------------------------------------------------------
        var whole = await VersionsAsync(world);

        whole.Status.ShouldBe(HttpStatusCode.OK);
        Ids(whole).ShouldBe(expected);
        NextCursor(whole).ShouldBeNull("a page shorter than `limit` is the last page");

        var items = whole.Body.GetProperty("items").EnumerateArray().ToArray();
        items.ShouldAllBe(row => row.GetProperty("batch_id").GetGuid() != Guid.Empty);
        items.ShouldAllBe(row => row.GetProperty("entity_kind").GetString()!.Length > 0);
        items.ShouldAllBe(row => row.GetProperty("change_kind").GetString()!.Length > 0);
        items.ShouldAllBe(row => row.GetProperty("source").GetString()!.Length > 0);
        items.Count(row => row.EnumerateObject().Any(property => property.Name == "content")).ShouldBe(0);
        items.ShouldAllBe(row => IsUtcInstant(row));

        // Newest first after JSON normalisation, and never a row of the sibling project or the other org.
        var stamps = items.Select(row => DateTimeOffset.Parse(
            row.GetProperty("created_at").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)).ToArray();
        stamps.ShouldBe(stamps.OrderByDescending(stamp => stamp).ToArray(), "route 22 is newest first");
        Ids(whole).ShouldBeSubsetOf(expected);

        // The other tenant's own timeline still serves its row, so the org predicate is a scope and not a data loss:
        // the same page on this tenant's side saw nothing of it.
        var otherTimeline = await world.Client.SendAsync(
            HttpMethod.Get,
            $"/v1/projects/{world.OtherOrgProject:D}/authoring/versions",
            TenancyApiFactory.Resolved(user: world.OtherUser, orgHeader: world.OtherOrg));

        otherTimeline.Status.ShouldBe(HttpStatusCode.OK, otherTimeline.ErrorMessage);
        Ids(otherTimeline).ShouldBe([rows[^1].Id]);

        // --- paging, limit 2 through a tie group ----------------------------------------------------------------------
        var paged = new List<Guid>();
        string? cursor = null;

        for (var page = 0; page < 12; page++)
        {
            var answer = await VersionsAsync(world, cursor is null ? "limit=2" : $"limit=2&cursor={cursor}");

            answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
            paged.AddRange(Ids(answer));

            cursor = NextCursor(answer);

            if (cursor is null)
            {
                break;
            }
        }

        cursor.ShouldBeNull("the walk ends on a page with no next_cursor");
        paged.ShouldBe(expected, "limit 2 walks every row exactly once, tie group included");

        // --- paging, limit 1 across one-microsecond steps --------------------------------------------------------------
        var micro = new List<Guid>();
        cursor = null;

        for (var page = 0; page < 12; page++)
        {
            var answer = await VersionsAsync(world, cursor is null ? "limit=1" : $"limit=1&cursor={cursor}");

            answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
            micro.AddRange(Ids(answer));

            cursor = NextCursor(answer);

            if (cursor is null)
            {
                break;
            }
        }

        micro.ShouldBe(expected, "the cursor round-trips to the microsecond: no row skipped, none repeated");

        // --- filters, each alone and combined ------------------------------------------------------------
        async Task<IReadOnlyList<Guid>> Expect(string query, IReadOnlyList<Guid> wanted)
        {
            var answer = await VersionsAsync(world, query);
            answer.Status.ShouldBe(HttpStatusCode.OK, $"{query}: {answer.ErrorMessage}");
            Ids(answer).ShouldBe(wanted, query);

            return Ids(answer);
        }

        var featureIds = expected.Where(id => idsOf(feature).Contains(id)).ToArray();
        HashSet<Guid> idsOf(Guid entity) => [.. rows.Where(row => row.Project == world.Project && row.Entity == entity).Select(row => row.Id)];

        (await Expect("entity_kind=feature_node", featureIds)).Count.ShouldBe(10);
        (await Expect("entity_kind=checklist_item", [idOf(item)])).Count.ShouldBe(1);
        (await Expect("entity_kind=manual_test_case", [idOf(caseEntity)])).Count.ShouldBe(1);

        // The two halves of the entity filter are ANDed and the route serves only the pair (`entity_id` alone is the
        // 422 of CursorAndLimit_Refusals): a kind that does not match the id answers nothing, the matching pair answers
        // the entity's history.
        (await Expect($"entity_kind=feature_node&entity_id={item:D}", [])).ShouldBeEmpty();
        (await Expect($"entity_kind=feature_node&entity_id={feature:D}", featureIds)).Count.ShouldBe(10);

        (await Expect("source=ai", [idOf(item)])).Count.ShouldBe(1);
        (await Expect("source=import", [idOf(caseEntity)])).Count.ShouldBe(1);
        (await Expect("source=manual", featureIds)).Count.ShouldBe(10);
        // Two seeded rows carry this membership: the feature's version 3 and the item's create, which was seeded with the
        // same author (and is the newer of the two, so it leads).
        (await Expect(
            $"actor={world.Membership:D}",
            expected.Where(id => id == rows[2].Id || id == idOf(item)).ToArray())).Count.ShouldBe(2);

        // `from` inclusive, `to` exclusive, both to the microsecond.
        var from = Instant(shared);
        var afterShared = Instant(shared.AddTicks(10));

        // The five rows at `shared` are the oldest ones, so they are the tail of the newest-first order.
        var tie = expected.Skip(expected.Count - 5).ToArray();

        (await Expect($"from={Uri.EscapeDataString(from)}", expected)).Count.ShouldBe(12);
        (await Expect($"from={Uri.EscapeDataString(afterShared)}", expected.Take(7).ToArray())).Count.ShouldBe(7);
        (await Expect($"to={Uri.EscapeDataString(from)}", [])).ShouldBeEmpty("`to` is exclusive, so a row exactly at it is out");
        (await Expect($"to={Uri.EscapeDataString(afterShared)}", tie)).Count.ShouldBe(5);
        (await Expect(
            $"from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(afterShared)}",
            tie)).Count.ShouldBe(5);

        (await Expect(
            $"entity_kind=feature_node&source=manual&actor={world.Membership:D}&from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(afterShared)}",
            [rows[2].Id])).Count.ShouldBe(1);

        // --- one batch, one id ------------------------------------------------------------------------------------
        var batchOf = items.ToDictionary(
            row => row.GetProperty("version_id").GetGuid(),
            row => row.GetProperty("batch_id").GetGuid());

        batchOf[rows[0].Id].ShouldBe(batchA);
        batchOf[rows[1].Id].ShouldBe(batchA);
        batchOf[rows[2].Id].ShouldBe(batchB);
        batchOf[rows[3].Id].ShouldBe(batchB);
        batchOf[rows[4].Id].ShouldBe(batchB);

        Guid idOf(Guid entity) => rows.Single(row => row.Entity == entity).Id;
    }

    [Fact]
    public async Task CursorAndLimit_Refusals()
    {
        await using var world = await StartAsync();

        await SeedAsync(
            world,
            new Seed(Guid.CreateVersion7(), world.Project, Guid.CreateVersion7(), "feature_node", 1,
                Guid.CreateVersion7(), "create", "manual", null, new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero)),
            new Seed(Guid.CreateVersion7(), world.Project, Guid.CreateVersion7(), "feature_node", 1,
                Guid.CreateVersion7(), "create", "manual", null, new DateTimeOffset(2026, 10, 1, 10, 0, 1, TimeSpan.Zero)));

        // A live cursor first: the negative cases below must be recognised as tampering, not as an absent cursor.
        var first = await VersionsAsync(world, "limit=1");
        var issued = NextCursor(first);
        issued.ShouldNotBeNull();

        var live = issued!;

        // The live cursor, tampered in the two ways a client can: re-padded with the Base64 filler, and with the unused
        // low bits of its last base64url digit set (the issued text has them zero, so the result decodes to the SAME bytes
        // and only the codec's canonical re-encode refuses it). Both prove the route refuses a tampered cursor instead of
        // reading a position out of it or treating it as absent. The 56-byte payload leaves two unused bits in the last
        // digit, so `index | 1` is never the issued digit.
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var rePadded = live + "=";
        var swapped = live[..^1] + alphabet[alphabet.IndexOf(live[^1], StringComparison.Ordinal) | 1];

        var cases = new (string Query, string Path, string Reason)[]
        {
            ("limit=0", "/limit", "out_of_range"),
            ("limit=201", "/limit", "out_of_range"),
            ("limit=abc", "/limit", "out_of_range"),
            ("cursor=not-a-cursor", "/cursor", "malformed"),
            ($"cursor={rePadded}", "/cursor", "malformed"),
            ($"cursor={swapped}", "/cursor", "malformed"),
            ("cursor=%00", "/cursor", "malformed"),
            ("entity_kind=bogus", "/entity_kind", "value_invalid"),
            ("source=bogus", "/source", "value_invalid"),
            ("entity_id=nope", "/entity_id", "value_invalid"),
            // The pair is the only entity filter the route serves: an id without its kind cannot use the entity key
            // index (`org_id, project_id, entity_kind, entity_id, version_no`), so it is refused instead of scanning.
            ($"entity_id={Guid.CreateVersion7():D}", "/entity_kind", "required_with_entity_id"),
            ("actor=nope", "/actor", "value_invalid"),
            ("from=nope", "/from", "value_invalid"),
            ("to=2026-10-01", "/to", "value_invalid"),
            ("to=2026-10-01T10:00:00", "/to", "value_invalid"),
            ("from=2026-10-02T00:00:00Z&to=2026-10-01T00:00:00Z", "/from", "invalid_range"),
        };

        foreach (var (query, path, reason) in cases)
        {
            var refused = await VersionsAsync(world, query);

            refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, $"{query}: {refused.Body}");
            refused.ErrorCode.ShouldBe("request_invalid", query);
            refused.Body.GetProperty("error").GetProperty("details").EnumerateArray()
                .ShouldContain(
                    detail => detail.GetProperty("path").GetString() == path
                        && detail.GetProperty("reason").GetString() == reason,
                    query);
        }

        // The controls: the issued cursor is served, the bounds are 200, and an empty instant pair is not a range error.
        (await VersionsAsync(world, $"limit=1&cursor={issued}")).Status.ShouldBe(HttpStatusCode.OK);
        (await VersionsAsync(world, "limit=200")).Status.ShouldBe(HttpStatusCode.OK);
        (await VersionsAsync(world, "from=2026-10-01T10:00:00Z&to=2026-10-01T10:00:00Z")).Status.ShouldBe(HttpStatusCode.OK);
        (await VersionsAsync(world, "entity_kind=")).Status.ShouldBe(HttpStatusCode.OK, "an empty filter is an absent filter");
        (await VersionsAsync(world, "entity_kind=feature_node")).Status.ShouldBe(
            HttpStatusCode.OK,
            "the kind alone is a served filter");
        (await VersionsAsync(world, $"entity_kind=feature_node&entity_id={Guid.CreateVersion7():D}")).Status.ShouldBe(
            HttpStatusCode.OK,
            "the pair is served even when no row matches the id");
        (await VersionsAsync(world, "entity_kind=&entity_id=")).Status.ShouldBe(
            HttpStatusCode.OK,
            "two empty values are two absent filters, so no kind is required");
    }

    /// <summary>
    /// AC9's <c>from</c>/<c>to</c> are instants, not local times: a <c>+07:00</c> or <c>-05:30</c> bound names the same
    /// window as the <c>Z</c> spelling. The bound reaches the statement as <c>timestamptz</c>, which stores no zone, and
    /// Npgsql refuses to bind a <see cref="DateTimeOffset"/> with a non-zero offset to that type — so without
    /// normalisation the same window answers 500 instead of the same page.
    /// </summary>
    [Fact]
    public async Task FromTo_NonUtcOffsets_AreTheSameWindow()
    {
        await using var world = await StartAsync();

        var entity = Guid.CreateVersion7();
        var atTen = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        var halfAfter = atTen.AddTicks(5_000_000);
        var atOneSecond = atTen.AddSeconds(1);

        var rows = new[]
        {
            new Seed(Guid.CreateVersion7(), world.Project, entity, "feature_node", 1, Guid.CreateVersion7(),
                "create", "manual", null, atTen),
            new Seed(Guid.CreateVersion7(), world.Project, entity, "feature_node", 2, Guid.CreateVersion7(),
                "update", "manual", null, halfAfter),
            new Seed(Guid.CreateVersion7(), world.Project, entity, "feature_node", 3, Guid.CreateVersion7(),
                "update", "manual", null, atOneSecond),
        };

        await SeedAsync(world, rows);

        // The window is [10:00:00Z, 10:00:01Z): the two rows below the exclusive bound, newest first.
        var expected = new[] { rows[1].Id, rows[0].Id };

        // The `+` of a positive offset is percent-encoded (`%2B`), because a raw `+` in a query string is a space; the
        // `-05:30` spelling needs no encoding. Both directions are covered as a client library would send them.
        var windows = new[]
        {
            "from=2026-10-01T10:00:00Z&to=2026-10-01T10:00:01Z",
            "from=2026-10-01T17:00:00%2B07:00&to=2026-10-01T17:00:01%2B07:00",
            "from=2026-10-01T04:30:00-05:30&to=2026-10-01T04:30:01-05:30",
        };

        foreach (var window in windows)
        {
            var answer = await VersionsAsync(world, window);

            answer.Status.ShouldBe(HttpStatusCode.OK, $"{window}: {answer.Body}");
            Ids(answer).ShouldBe(expected, window);
        }

        // The bound is the instant, not its spelling: 17:00:00+07:00 is the row at 10:00:00Z, which `to` excludes.
        var exclusive = await VersionsAsync(world, "from=2026-10-01T10:00:00Z&to=2026-10-01T17:00:00%2B07:00");

        exclusive.Status.ShouldBe(HttpStatusCode.OK, exclusive.ErrorMessage);
        Ids(exclusive).ShouldBeEmpty("to is exclusive whichever offset spells it");

        var late = await VersionsAsync(world, "from=2026-10-01T17:00:01%2B07:00");

        late.Status.ShouldBe(HttpStatusCode.OK, late.ErrorMessage);
        Ids(late).ShouldBe([rows[2].Id], "a +07:00 lower bound is the 10:00:01Z instant");
    }

    [Fact]
    public async Task VersionDetail_ContentAndETag_Passthrough()
    {
        await using var world = await StartAsync();

        var entity = Guid.CreateVersion7();
        var row = Guid.CreateVersion7();
        const string Content = """{"title":"Checkout","nested":{"steps":[{"no":1,"action":"open","expected":"shown"}]},"position":3}""";

        await SeedAsync(world, new Seed(
            row, world.Project, entity, "manual_test_case", 1, Guid.CreateVersion7(), "create", "manual",
            null, new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), Content));

        var detail = await world.Client.SendAsync(HttpMethod.Get, world.Version(row), world.As);

        detail.Status.ShouldBe(HttpStatusCode.OK);
        detail.Body.GetProperty("version_id").GetGuid().ShouldBe(row);
        detail.Body.GetProperty("entity_id").GetGuid().ShouldBe(entity);
        detail.Body.GetProperty("content_hash").GetString().ShouldBe(SeededHash);

        // The ETag is the stored digest in quotes, exactly: the tag a client compares after a re-read.
        detail.ETag!.Trim('"').ShouldBe(SeededHash);

        // The content is the stored document, not a re-serialisation: compare it with the column's own text after JSON
        // normalisation, which ignores member order and formatting but not a value.
        var stored = await _fixture.ScalarAsync<string>(
            world.Database, $"select content::text from public.authoring_version where id = '{row:D}'");

        JsonNode.DeepEquals(
            JsonNode.Parse(detail.Body.GetProperty("content").GetRawText()),
            JsonNode.Parse(stored)).ShouldBeTrue();

        // Route 22 and route 23 describe the same row with the same members (23 adds `content`).
        var list = await VersionsAsync(world);
        var listed = list.Body.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("version_id").GetGuid() == row);

        foreach (var member in new[] { "entity_kind", "entity_id", "version_no", "batch_id", "change_kind", "source", "content_hash", "created_at" })
        {
            listed.GetProperty(member).GetRawText().ShouldBe(detail.Body.GetProperty(member).GetRawText(), member);
        }

        listed.TryGetProperty("content", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task VersionOfAnotherScope_Is404()
    {
        await using var world = await StartAsync();

        var sibling = Guid.CreateVersion7();
        var foreign = Guid.CreateVersion7();
        var deleted = await CreateProjectAsync(world.Client, world.User, world.Org, "archive");
        var deletedVersion = Guid.CreateVersion7();

        await SeedAsync(
            world,
            new Seed(sibling, world.SiblingProject, Guid.CreateVersion7(), "feature_node", 1,
                Guid.CreateVersion7(), "create", "manual", null, new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero)),
            new Seed(foreign, world.OtherOrgProject, Guid.CreateVersion7(), "feature_node", 1,
                Guid.CreateVersion7(), "create", "manual", null,
                new DateTimeOffset(2026, 10, 1, 10, 0, 1, TimeSpan.Zero), Org: world.OtherOrg),
            new Seed(deletedVersion, deleted, Guid.CreateVersion7(), "feature_node", 1,
                Guid.CreateVersion7(), "create", "manual", null, new DateTimeOffset(2026, 10, 1, 10, 0, 2, TimeSpan.Zero)));

        // A version of another project of the same org, addressed under this project: absence, not a partial answer.
        var addressedWrong = await world.Client.SendAsync(HttpMethod.Get, world.Version(sibling), world.As);

        addressedWrong.Status.ShouldBe(HttpStatusCode.NotFound);
        addressedWrong.ErrorCode.ShouldBe("not_found");

        // Another org's version, a random id, and an id of a project that is being deleted.
        (await world.Client.SendAsync(HttpMethod.Get, world.Version(foreign), world.As)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await world.Client.SendAsync(HttpMethod.Get, world.Version(Guid.CreateVersion7()), world.As)).Status.ShouldBe(HttpStatusCode.NotFound);

        // The same org's sibling project addresses its own version: the id is not secret, the project scope is.
        (await world.Client.SendAsync(HttpMethod.Get, world.InSibling(sibling), world.As)).Status.ShouldBe(HttpStatusCode.OK);

        // A soft-deleted project answers its version's route and its list route the module's project 404.
        await _fixture.ExecuteAsync(world.Database, $"update public.project set status = 'deleted' where id = '{deleted:D}'");

        var ofDeletedProject = await world.Client.SendAsync(
            HttpMethod.Get, $"/v1/projects/{deleted:D}/authoring/versions/{deletedVersion:D}", world.As);
        ofDeletedProject.Status.ShouldBe(HttpStatusCode.NotFound);

        var listOfDeletedProject = await world.Client.SendAsync(
            HttpMethod.Get, $"/v1/projects/{deleted:D}/authoring/versions", world.As);
        listOfDeletedProject.Status.ShouldBe(HttpStatusCode.NotFound);

        // The other org's caller cannot reach this org's version through its own project either.
        var asStranger = TenancyApiFactory.Resolved(user: world.OtherUser, orgHeader: world.OtherOrg);
        (await world.Client.SendAsync(HttpMethod.Get, world.InOtherOrg(sibling), asStranger)).Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reads_ServeViewer_AndRefuseRolelessMember()
    {
        await using var world = await StartAsync();

        var row = Guid.CreateVersion7();
        await SeedAsync(world, new Seed(
            row, world.Project, Guid.CreateVersion7(), "feature_node", 1, Guid.CreateVersion7(), "create", "manual",
            null, new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero)));

        var viewer = await AddMemberAsync(world, "viewer");
        var stranger = await AddMemberAsync(world, roleCode: null);
        var asViewer = TenancyApiFactory.Resolved(user: viewer, orgHeader: world.Org);
        var asStranger = TenancyApiFactory.Resolved(user: stranger, orgHeader: world.Org);

        foreach (var url in new[] { world.Versions, world.Version(row) })
        {
            var served = await world.Client.SendAsync(HttpMethod.Get, url, asViewer);

            served.Status.ShouldBe(HttpStatusCode.OK, $"{url}: a viewer reads the timeline, not {served.Status}");
            served.ErrorCode.ShouldBeNull();

            var denied = await world.Client.SendAsync(HttpMethod.Get, url, asStranger);

            denied.Status.ShouldBe(HttpStatusCode.Forbidden, $"{url}: a member without authoring:read must be refused");
            denied.ErrorCode.ShouldBe("forbidden");
        }
    }

    /// <summary>An active member of the org, optionally holding one built-in role, granted by the founder through the API.</summary>
    private async Task<Guid> AddMemberAsync(World world, string? roleCode)
    {
        var user = Guid.CreateVersion7();
        var membership = Guid.CreateVersion7();

        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.membership (id, org_id, user_id, invited_email, status)
            values ('{membership:D}', '{world.Org:D}', '{user:D}', 'member-{user:N}@example.test', 'active')
            """);

        if (roleCode is not null)
        {
            var role = await _fixture.ScalarAsync<Guid>(
                world.Database, $"select id from public.role where code = '{roleCode}' and org_id is null");
            var granted = await world.Client.SendAsync(
                HttpMethod.Post, $"/v1/orgs/{world.Org:D}/memberships/{membership:D}/roles", world.As, new { role_id = role }, NewKey());

            granted.Status.ShouldBe(HttpStatusCode.OK, granted.ErrorMessage);
        }

        return user;
    }

    /// <summary>
    /// AC9's <c>batch_id</c> is not decorative: one command that appends several rows stamps them all with one id, so the
    /// UI can group them into a single version. The reorder route is the one command that moves more than one entity at
    /// once (13.1.1 §5 route 4), so it is driven through its own route and the timeline is read back.
    /// </summary>
    [Fact]
    public async Task Batches_GroupRowsOfOneCommand()
    {
        await using var world = await StartAsync();

        var first = await CreateNodeAsync(world, "A");
        var second = await CreateNodeAsync(world, "B");
        var third = await CreateNodeAsync(world, "C");

        var tree = await AuthoringCalls.TreeAsync(world.Client, world.As, world.Project);

        tree.Status.ShouldBe(HttpStatusCode.OK);

        var reordered = await AuthoringCalls.SendAsync(
            world.Client,
            world.As,
            HttpMethod.Post,
            $"/v1/projects/{world.Project:D}/authoring/nodes/{first:D}/reorder",
            JsonSerializer.Serialize(new { ordered_ids = new[] { second, first, third } }),
            ifMatch: tree.ETag);

        reordered.Status.ShouldBe(HttpStatusCode.OK, reordered.Body?.ToString());

        var answer = await VersionsAsync(world, "entity_kind=feature_node&limit=200");

        answer.Status.ShouldBe(HttpStatusCode.OK);

        var rows = answer.Body.GetProperty("items").EnumerateArray().ToArray();

        // The renumber is AC2's two position statements per moved node (park, then assign, AuthoringSql.ReorderPark/
        // ReorderAssign), and the before-update trigger bumps `version` on each, so a moved node leaves version 1 for
        // version 3 and its one reorder row carries version_no 3 (AuthoringSql.cs:389-392; the writer refuses any other
        // delta). The history therefore reads 1, 3 with no 2: the park is not a user-visible change, so no row is owed
        // for it and AC5's one-row-per-changed-entity holds — the number a caller reads is the final one. The third
        // node kept its position, so it has no update row at all.
        var renumbered = rows.Where(row => row.GetProperty("change_kind").GetString() == "update").ToArray();

        renumbered.Length.ShouldBe(2, "the reorder moved two of the three siblings");
        renumbered.ShouldAllBe(row => row.GetProperty("version_no").GetInt32() == 3);

        var batch = renumbered[0].GetProperty("batch_id").GetGuid();

        renumbered[1].GetProperty("batch_id").GetGuid().ShouldBe(batch, "one command, one batch id");
        renumbered.Select(row => row.GetProperty("entity_id").GetGuid()).OrderBy(id => id)
            .ShouldBe(new[] { first, second }.OrderBy(id => id));
        rows.Count(row => row.GetProperty("entity_id").GetGuid() == third)
            .ShouldBe(1, "the unmoved sibling gained no row");

        // And the batch is the reorder's own: no create of this project shares it.
        var creates = rows.Where(row => row.GetProperty("version_no").GetInt32() == 1)
            .Select(row => row.GetProperty("batch_id").GetGuid())
            .ToArray();

        creates.Length.ShouldBe(3);
        creates.ShouldNotContain(batch);
    }

    private static async Task<Guid> CreateNodeAsync(World world, string title)
    {
        var created = await AuthoringCalls.CreateNodeAsync(world.Client, world.As, world.Project, null, "module", title);

        created.Status.ShouldBe(HttpStatusCode.Created, created.Body?.ToString());

        return created.Member("node_id").GetGuid();
    }

    /// <summary>
    /// 13.1.1 §5 route 22/23 P4: the timeline's newest-first order is served by
    /// <c>authoring_version_timeline_idx</c> and a per-entity history by <c>authoring_version_entity_key</c>
    /// (<c>migrations/0039</c>), with no sequential scan. Seeded with 3 000 rows in the project under test and 1 500 in
    /// its sibling, spread over 50 entities and one distinct <c>created_at</c> per second, then <c>ANALYZE</c>d, so the
    /// planner chooses without <c>enable_seqscan = off</c>.
    /// </summary>
    /// <remarks>
    /// [DECISION] The plan says "the two index-backed orders"; they are read here as the timeline order
    /// (<c>created_at desc, id desc</c>) and the per-entity history order (one entity's rows, newest first). The entity
    /// index is a <b>lookup</b> index (<c>org_id, project_id, entity_kind, entity_id, version_no</c>), so a per-entity page
    /// still sorts the handful of rows it found — the assertion is the index and the absence of a sequential scan, not
    /// the absence of a sort.
    /// </remarks>
    [Fact]
    public async Task Timeline_UsesDeclaredIndexes()
    {
        await using var world = await StartAsync();

        var entity = await SeedTimelineAsync(world);

        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(world.Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);

        await using (var set = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("org", world.Org.ToString());
            await set.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        // The unfiltered first page: the timeline order, the seek as one row comparison, every filter absent.
        var unfiltered = await ExplainAsync(connection, transaction, AuthoringVersionReadSql.TimelinePage, world,
            entityKind: null, entityId: null, from: null, to: null);

        unfiltered.IndexNames.ShouldContain("authoring_version_timeline_idx");
        unfiltered.NodeTypes.ShouldNotContain("Seq Scan");
        unfiltered.NodeTypes.ShouldNotContain("Sort", "the order comes from the index");

        // The per-entity history: the entity prefix of `authoring_version_entity_key` is the index condition.
        var perEntity = await ExplainAsync(connection, transaction, AuthoringVersionReadSql.EntityPage, world,
            entityKind: "feature_node", entityId: entity, from: null, to: null);

        perEntity.IndexNames.ShouldContain("authoring_version_entity_key");
        perEntity.NodeTypes.ShouldNotContain("Seq Scan");

        // The bounded range: a one-minute window of the timeline is a short backward range scan.
        var window = await ExplainAsync(connection, transaction, AuthoringVersionReadSql.TimelinePage, world,
            entityKind: null, entityId: null,
            from: new DateTimeOffset(2026, 1, 1, 0, 20, 0, TimeSpan.Zero),
            to: new DateTimeOffset(2026, 1, 1, 0, 21, 0, TimeSpan.Zero));

        window.IndexNames.ShouldContain("authoring_version_timeline_idx");
        window.NodeTypes.ShouldNotContain("Seq Scan");

        // And the detail route is one unique-key lookup of the version row: `authoring_version_pkey` (id) or, when the
        // planner prefers it, `authoring_version_org_id_key` (org_id, id) — both are exact lookups on the id. Its
        // project-liveness `exists` is a subplan on a handful of project rows, which the planner legitimately reads
        // sequentially, so only the version table is held to the index.
        var detail = await ExplainDetailAsync(connection, transaction, world);

        detail.IndexNames.ShouldContain(
            index => index == "authoring_version_pkey" || index == "authoring_version_org_id_key",
            "the version row is fetched by one of its unique keys");
        detail.SequentialScanRelations.ShouldNotContain("authoring_version");
    }

    /// <summary>Seeds 3 000 rows in the project under test and 1 500 in its sibling; returns the dense entity's id.</summary>
    private async Task<Guid> SeedTimelineAsync(World world)
    {
        // The entity id is built from `g % 50`, so 50 entities hold 60 versions each; `version_no = g / 50 + 1` keeps the
        // (entity, version) key unique. `created_at` is one second apart, which is 3 000 distinct timeline positions.
        const string SeedSql = """
            insert into public.authoring_version
              (id, org_id, project_id, entity_kind, entity_id, version_no, batch_id, change_kind, content, content_hash,
               source, author_membership_id, reason, ai_run_id, created_at)
            select gen_random_uuid(), '{0}', '{1}', 'feature_node',
                   ('00000000-0000-7000-8000-' || lpad((g % 50)::text, 12, '0'))::uuid,
                   g / 50 + 1, gen_random_uuid(), 'create', '{{"title":"seed"}}'::jsonb,
                   'sha256:1111111111111111111111111111111111111111111111111111111111111111', 'manual', null, null, null,
                   timestamptz '2026-01-01 00:00:00+00' + g * interval '1 second'
              from generate_series(1, 3000) g;
            """;

        await _fixture.ExecuteAsync(
            world.Database,
            string.Format(CultureInfo.InvariantCulture, SeedSql, world.Org, world.Project));

        await _fixture.ExecuteAsync(
            world.Database,
            string.Format(CultureInfo.InvariantCulture, SeedSql, world.Org, world.SiblingProject));

        await _fixture.ExecuteAsync(world.Database, "analyze public.authoring_version");

        return new Guid("00000000-0000-7000-8000-000000000007");
    }

    /// <summary>Explains one timeline statement with the handler's own parameter set bound and returns its plan nodes.</summary>
    private static async Task<(List<string> NodeTypes, List<string> IndexNames, List<string> SequentialScanRelations)> ExplainAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        World world,
        string? entityKind,
        Guid? entityId,
        DateTimeOffset? from,
        DateTimeOffset? to)
    {
        await using var command = new NpgsqlCommand("explain (format json) " + sql, connection, transaction);

        Bind(command, "org_id", world.Org, NpgsqlDbType.Uuid);
        Bind(command, "project_id", world.Project, NpgsqlDbType.Uuid);
        Bind(command, "cursor_created_at", AuthoringVersionCursor.FirstPageTimestamp, NpgsqlDbType.TimestampTz);
        Bind(command, "cursor_id", AuthoringVersionCursor.FirstPageId, NpgsqlDbType.Uuid);
        Bind(command, "entity_kind", entityKind, NpgsqlDbType.Text);
        Bind(command, "entity_id", entityId, NpgsqlDbType.Uuid);
        Bind(command, "source", null, NpgsqlDbType.Text);
        Bind(command, "actor", null, NpgsqlDbType.Uuid);
        Bind(command, "from_at", from, NpgsqlDbType.TimestampTz);
        Bind(command, "to_at", to, NpgsqlDbType.TimestampTz);
        Bind(command, "take", 51, NpgsqlDbType.Integer);

        var plan = (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;

        return PlanNodes(plan);
    }

    /// <summary>Explains route 23's statement, whose project liveness is part of the same statement.</summary>
    private static async Task<(List<string> NodeTypes, List<string> IndexNames, List<string> SequentialScanRelations)> ExplainDetailAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        World world)
    {
        var version = await ScalarGuidAsync(connection, transaction, world);

        await using var command = new NpgsqlCommand("explain (format json) " + AuthoringVersionReadSql.VersionDetail, connection, transaction);

        Bind(command, "org_id", world.Org, NpgsqlDbType.Uuid);
        Bind(command, "project_id", world.Project, NpgsqlDbType.Uuid);
        Bind(command, "version_id", version, NpgsqlDbType.Uuid);

        var plan = (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;

        return PlanNodes(plan);
    }

    private static async Task<Guid> ScalarGuidAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, World world)
    {
        await using var command = new NpgsqlCommand(
            "select id from public.authoring_version where org_id = @org and project_id = @project order by created_at desc limit 1",
            connection,
            transaction);

        command.Parameters.AddWithValue("org", world.Org);
        command.Parameters.AddWithValue("project", world.Project);

        return (Guid)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>Binds one parameter, with an explicit type for a null so PostgreSQL never has to guess.</summary>
    private static void Bind(NpgsqlCommand command, string name, object? value, NpgsqlDbType type)
    {
        if (value is null)
        {
            command.Parameters.Add(new NpgsqlParameter(name, type) { Value = DBNull.Value });
            return;
        }

        command.Parameters.AddWithValue(name, value);
    }

    /// <summary>
    /// Every node type, index name and sequentially scanned relation in an <c>EXPLAIN (FORMAT JSON)</c> document, however
    /// the plan nests. The relation names let an assertion allow a sequential scan on a small table (the project
    /// liveness probe) while still forbidding one on the version table.
    /// </summary>
    private static (List<string> NodeTypes, List<string> IndexNames, List<string> SequentialScanRelations) PlanNodes(string json)
    {
        var nodeTypes = new List<string>();
        var indexNames = new List<string>();
        var sequentialScanRelations = new List<string>();
        var queue = new Queue<JsonElement>();

        using var document = JsonDocument.Parse(json);

        foreach (var root in document.RootElement.EnumerateArray())
        {
            queue.Enqueue(root.GetProperty("Plan"));
        }

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            var nodeType = node.GetProperty("Node Type").GetString()!;

            nodeTypes.Add(nodeType);

            if (node.TryGetProperty("Index Name", out var index))
            {
                indexNames.Add(index.GetString()!);
            }

            if (nodeType == "Seq Scan" && node.TryGetProperty("Relation Name", out var relation))
            {
                sequentialScanRelations.Add(relation.GetString()!);
            }

            if (node.TryGetProperty("Plans", out var children))
            {
                foreach (var child in children.EnumerateArray())
                {
                    queue.Enqueue(child);
                }
            }
        }

        return (nodeTypes, indexNames, sequentialScanRelations);
    }
}
