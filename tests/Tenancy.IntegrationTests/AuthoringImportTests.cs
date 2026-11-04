using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.AuthoringImportCalls;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1 AC13/AC15/AC16/AC18 at the HTTP edge of route 25: one valid file applies in one transaction under one
/// <c>batch_id</c>, any invalid row refuses the whole file and writes nothing, a dry run reports and writes nothing, the
/// apply is a bounded number of statements whatever the row count, the 5 MiB / 5 000-row caps answer 413 before any
/// parse, and a re-import is idempotent by <c>code</c> — identical file all <c>unchanged</c>, changed case
/// <c>update</c>, soft-deleted code <c>restore</c> — with the raw <c>content_hash</c> preserved.
/// </summary>
/// <remarks>
/// The file builders and the raw-body request helpers live in <see cref="AuthoringImportCalls"/>; the export projection
/// is <see cref="AuthoringImportExportTests"/>'s, because round-trip equality is the only place it is needed.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class AuthoringImportTests
{
    private readonly PostgresTenancyFixture _fixture;

    /// <summary>Wires the fixture.</summary>
    /// <param name="fixture">The shared PostgreSQL fixture.</param>
    public AuthoringImportTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>One test world: the host, one org and one project it owns.</summary>
    /// <param name="Api">The host, whose statement log the budget test reads.</param>
    /// <param name="Client">The API client.</param>
    /// <param name="Database">The migrated test database.</param>
    /// <param name="User">The founder.</param>
    /// <param name="Org">The org.</param>
    /// <param name="Project">The project.</param>
    private sealed record World(TenancyApiFactory Api, HttpClient Client, string Database, Guid User, Guid Org, Guid Project)
        : IAsyncDisposable
    {
        /// <summary>The founder's headers.</summary>
        internal Action<HttpRequestHeaders> As => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        /// <summary>The authoring base URL of the project.</summary>
        internal string Authoring => $"/v1/projects/{Project}/authoring";

        /// <summary>One manual case's route.</summary>
        /// <param name="manualCase">The case id.</param>
        internal string Case(Guid manualCase) => $"{Authoring}/manual-cases/{manualCase:D}";

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private async Task<World> StartAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("acme"));
        var project = await CreateProjectAsync(client, user, org, "checkout");

        return new World(api, client, database, user, org, project);
    }

    /// <summary>
    /// The project's stored <c>case_prefix</c>: a case row's code must carry it (AC4/AC13), and the round-trip needs it
    /// to reuse A's codes in B.
    /// </summary>
    private async Task<string> PrefixAsync(World world) =>
        await _fixture.ScalarAsync<string>(world.Database, $"select case_prefix from public.project where id = '{world.Project:D}'");

    /// <summary>
    /// One valid file: the four-level ladder of AC1 (<c>module → feature → category → sub_category</c>), an item under
    /// the category and a case under the item, in file order — the shape every import test below reuses.
    /// </summary>
    /// <param name="code">The case code, or null to let the allocator number it.</param>
    private static List<FileRow> Ladder(string? code = null) =>
    [
        FileRow.Node("n1", null, "module", "Checkout"),
        FileRow.Node("n2", "n1", "feature", "Payment"),
        FileRow.Node("n3", "n2", "category", "Cards"),
        FileRow.Node("n4", "n3", "sub_category", "3-D Secure"),
        FileRow.Item("i1", "n4", "Pay by card", "Card payment", "high"),
        FileRow.Case("c1", "i1", "Card succeeds", Steps3(), "high", code),
    ];

    /// <summary><paramref name="count"/> independent copies of <see cref="Ladder"/>, keys and titles prefixed per copy.</summary>
    /// <param name="count">How many ladders the file holds.</param>
    private static List<FileRow> Ladders(int count)
    {
        var rows = new List<FileRow>();

        for (var k = 1; k <= count; k++)
        {
            rows.Add(FileRow.Node($"n{k}a", null, "module", $"Checkout {k}"));
            rows.Add(FileRow.Node($"n{k}b", $"n{k}a", "feature", "Payment"));
            rows.Add(FileRow.Node($"n{k}c", $"n{k}b", "category", "Cards"));
            rows.Add(FileRow.Node($"n{k}d", $"n{k}c", "sub_category", "3-D Secure"));
            rows.Add(FileRow.Item($"i{k}", $"n{k}d", $"Pay by card {k}", "Card payment", "high"));
            rows.Add(FileRow.Case($"c{k}", $"i{k}", $"Card succeeds {k}", Steps3(), "high"));
        }

        return rows;
    }

    /// <summary>A three-step case document, one step carrying <c>data</c>.</summary>
    private static JsonObject Steps3() => new JsonObject
    {
        ["steps"] = new JsonArray
        {
            new JsonObject { ["no"] = 1, ["action"] = "Open the cart", ["expected"] = "The cart is shown" },
            new JsonObject { ["no"] = 2, ["action"] = "Add a card", ["expected"] = "The card is listed", ["data"] = "4242" },
            new JsonObject { ["no"] = 3, ["action"] = "Pay", ["expected"] = "The order is placed" },
        },
    };

    /// <summary>
    /// AC13: one valid file creates the whole ladder, every row of the run carries the answer's single <c>batch_id</c>,
    /// each changed entity gets exactly one <c>create</c> version with <c>source = import</c>, and the run appends
    /// exactly one <c>audit_log</c> row (AC18).
    /// </summary>
    [Fact]
    public async Task AtomicApply_OneBatchId()
    {
        await using var world = await StartAsync();
        var rows = Ladder(await PrefixAsync(world) + "-101");
        var auditsBefore = await AuditsAsync(world);

        var answer = await ImportAsync(world.Client, world.As, world.Project, "csv", CsvBody(rows));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.Text);
        answer.ErrorCode.ShouldBeNull();

        var batch = answer.BatchId.ShouldNotBeNull();
        answer.Count("created").ShouldBe(rows.Count, "every row creates one entity");
        answer.Count("updated").ShouldBe(0);
        answer.Count("restored").ShouldBe(0);
        answer.Count("unchanged").ShouldBe(0);
        answer.Outcomes.ShouldAllBe(outcome => outcome == "create");
        answer.Indexes.ShouldBe([.. Enumerable.Range(1, rows.Count)], "rows[] echoes the 1-based data-row index");
        answer.Codes[^1].ShouldBe(rows[^1].Code, "the report carries the case code the row ends up with");

        var versionBatches = await _fixture.ColumnAsync<string>(
            world.Database,
            $"select distinct batch_id::text from public.authoring_version where project_id = '{world.Project:D}'");
        versionBatches.ShouldBe([batch.ToString("D")], "one file is one batch — no second batch_id exists");

        (await CountAsync(world, "authoring_version")).ShouldBe(rows.Count, "one version row per changed entity");

        var importVersions = await _fixture.ColumnAsync<string>(
            world.Database,
            $"""
            select change_kind || '|' || source from public.authoring_version
            where project_id = '{world.Project:D}' order by change_kind, source
            """);
        importVersions.ShouldAllBe(row => row == "create|import", "an import version is change_kind create, source import");

        (await AuditsAsync(world)).ShouldBe(
            auditsBefore + 1, "one import is one successful mutating command: exactly one new audit row");

        var storedCode = await _fixture.ScalarAsync<string>(
            world.Database, $"select code from public.manual_test_case where project_id = '{world.Project:D}'");
        storedCode.ShouldBe(rows[^1].Code, "the row's explicit code is the stored code");

        var counter = await _fixture.ScalarAsync<long>(
            world.Database, $"select next_value from public.authoring_code_counter where project_id = '{world.Project:D}'");
        counter.ShouldBe(
            102L,
            "the counter is raised to exactly one past the code the file brought (next_value is the next number to hand out)");
    }

    /// <summary>
    /// AC13: a file whose <em>last</em> row names an unknown parent is refused whole — one <c>details[]</c> entry for that
    /// row alone, naming its index and a fixed column pointer, and no table, version, counter or audit row of the run
    /// changes.
    /// </summary>
    [Fact]
    public async Task InvalidRow_WritesNothing()
    {
        await using var world = await StartAsync();
        var prefix = await PrefixAsync(world);

        var valid = Ladder(prefix + "-101");
        var rows = new List<FileRow>(valid)
        {
            // The offending row is LAST, so a "validate then apply" gap would have written the five rows above it.
            FileRow.Item("i9", "n99", "Orphan item", "No such parent in the file", "medium"),
        };

        var before = await RowCountsAsync(world);
        var answer = await ImportAsync(world.Client, world.As, world.Project, "csv", CsvBody(rows));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.Text);
        answer.ErrorCode.ShouldBe("request_invalid");
        answer.Details.Count.ShouldBe(1, answer.Text);

        var detail = answer.Details[0];
        detail.Index.ShouldBe(rows.Count, "the detail names the 1-based data row that failed");
        detail.Reason.ShouldBe("unknown_parent");
        detail.Path.ShouldBe("/parent_key", "the detail names the column, never a cell value");

        (await RowCountsAsync(world)).ShouldBe(before, "a refused file writes no row of any table, no version and no audit entry");
    }

    /// <summary>
    /// AC13: <c>dry_run=true</c> answers the same report and writes nothing at all — no entity, no version, no code
    /// allocation and no audit row — so a caller can validate a file before applying it.
    /// </summary>
    [Fact]
    public async Task DryRun_ReturnsReportWritesNothing()
    {
        await using var world = await StartAsync();
        var rows = Ladder(await PrefixAsync(world) + "-101");

        var before = await RowCountsAsync(world);
        var answer = await ImportAsync(world.Client, world.As, world.Project, "json", JsonBody(rows), dryRun: true);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.Text);
        answer.BatchId.ShouldBeNull("a dry run writes no version row, so it has no batch");
        answer.Count("created").ShouldBe(rows.Count, "the report is the one the same file would produce");
        answer.Outcomes.ShouldAllBe(outcome => outcome == "create");
        answer.Rows.Count.ShouldBe(rows.Count);

        (await RowCountsAsync(world)).ShouldBe(before, "a dry run writes no entity, version, counter or audit row");
    }

    /// <summary>
    /// AC13/P2: the apply is a batched write, not a per-row loop — a file of three ladders costs the same number of
    /// statements against the module's tables as a file of one, so no statement grows with the row count.
    /// </summary>
    /// <remarks>
    /// The two files go into two <b>empty</b> projects: a second import of the same ladder would find rows by title and
    /// code and could report <c>unchanged</c> rows, and an apply of nothing is a different statement path (no version
    /// insert, no allocation) than the create path the budget is about.
    /// </remarks>
    [Fact]
    public async Task Import_StatementCount()
    {
        await using var world = await StartAsync();
        var second = await CreateProjectAsync(world.Client, world.User, world.Org, "warehouse");

        world.Api.Statements.Clear();
        var small = await ImportAsync(world.Client, world.As, world.Project, "csv", CsvBody(Ladders(1)));
        small.Status.ShouldBe(HttpStatusCode.OK, small.Text);
        var smallCount = AuthoringStatements(world);

        world.Api.Statements.Clear();
        var large = await ImportAsync(world.Client, world.As, second, "csv", CsvBody(Ladders(3)));
        large.Status.ShouldBe(HttpStatusCode.OK, large.Text);
        var largeCount = AuthoringStatements(world);

        small.Count("created").ShouldBe(6, "one ladder of six rows");
        large.Count("created").ShouldBe(18, "three ladders of six rows each");
        largeCount.ShouldBe(
            smallCount,
            $"an import of 18 rows must issue the statements of an import of 6 rows ({smallCount} vs {largeCount}): "
            + "one batched insert per entity kind, one code allocation for the file");
    }

    /// <summary>
    /// AC16: a body of 5 MiB + 1 byte is refused 413 <c>payload_too_large</c> before it is parsed — no statement touches
    /// the module's tables and no row changes — while a body of exactly 5 MiB is at the cap, not over it.
    /// </summary>
    [Fact]
    public async Task Import_BodyOver5MiB_413()
    {
        await using var world = await StartAsync();

        var exact = ExactlyFiveMebibytes();
        exact.Length.ShouldBe(5 * 1024 * 1024, "the builder must hit the cap exactly");

        var over = new byte[exact.Length + 1];
        exact.CopyTo(over, 0);
        over[^1] = (byte)'x';

        var before = await RowCountsAsync(world);
        world.Api.Statements.Clear();
        var refused = await ImportAsync(world.Client, world.As, world.Project, "csv", over);

        refused.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge, refused.Text);
        refused.ErrorCode?.ShouldBe("payload_too_large");
        (await RowCountsAsync(world)).ShouldBe(before, "an oversized body writes nothing");

        var parsed = world.Api.Statements.Snapshot()
            .Count(statement => statement.Contains("public.feature_node", StringComparison.Ordinal)
                || statement.Contains("public.checklist_item", StringComparison.Ordinal)
                || statement.Contains("public.manual_test_case", StringComparison.Ordinal));
        parsed.ShouldBe(0, "the cap is answered before a single statement reads the import target");

        // The control: the same content at exactly the cap is inside it, and a dry run writes nothing either.
        world.Api.Statements.Clear();
        var accepted = await ImportAsync(world.Client, world.As, world.Project, "csv", exact, dryRun: true);

        accepted.Status.ShouldNotBe(HttpStatusCode.RequestEntityTooLarge, "exactly 5 MiB is the cap, not over it");
        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.Text);
        (await RowCountsAsync(world)).ShouldBe(before, "the accepted dry run writes nothing");
    }

    /// <summary>
    /// AC15: a re-import of the identical file reports every row <c>unchanged</c> with a null <c>batch_id</c> and appends
    /// nothing — no version row and no audit row; a row whose live <c>code</c> matches but whose content differs is an
    /// <c>update</c>; and a row whose <code>code</code> matches a <b>soft-deleted</b> case restores it with
    /// <c>change_kind = restore</c>.
    /// </summary>
    [Fact]
    public async Task Reimport_IdempotentByCode()
    {
        await using var world = await StartAsync();
        var code = await PrefixAsync(world) + "-101";
        var rows = Ladder(code);

        var first = await ImportAsync(world.Client, world.As, world.Project, "csv", CsvBody(rows));
        first.Status.ShouldBe(HttpStatusCode.OK, first.Text);
        first.BatchId.ShouldNotBeNull();

        var versionsAfterFirst = await CountAsync(world, "authoring_version");
        var auditsAfterFirst = await AuditsAsync(world);

        var second = await ImportAsync(world.Client, world.As, world.Project, "csv", CsvBody(rows));

        second.Status.ShouldBe(HttpStatusCode.OK, second.Text);
        second.BatchId.ShouldBeNull("an all-unchanged import writes no version row, so it has no batch");
        second.Count("unchanged").ShouldBe(rows.Count, "the same file changes nothing (AC15)");
        second.Count("created").ShouldBe(0);
        second.Count("updated").ShouldBe(0);
        second.Outcomes.ShouldAllBe(outcome => outcome == "unchanged");
        (await CountAsync(world, "authoring_version")).ShouldBe(versionsAfterFirst, "a re-import appends no version row");
        (await AuditsAsync(world)).ShouldBe(auditsAfterFirst, "a re-import appends no audit row");

        // A changed case: the same code, a different title, so the row is an update of the live case.
        var renamed = rows
            .Select(row => row.Kind == "case" ? row with { Title = "Card succeeds (renamed)" } : row)
            .ToList();
        var update = await ImportAsync(world.Client, world.As, world.Project, "csv", CsvBody(renamed));

        update.Status.ShouldBe(HttpStatusCode.OK, update.Text);
        update.BatchId.ShouldNotBeNull();
        update.Count("updated").ShouldBe(1, "only the case row differs");
        update.Outcomes[^1].ShouldBe("update", "the case row reports an update");

        var manualCase = await _fixture.ScalarAsync<Guid>(
            world.Database, $"select id from public.manual_test_case where project_id = '{world.Project:D}' and code = '{code}'");
        (await LatestChangeKindAsync(world, manualCase)).ShouldBe("update");

        var version = await _fixture.ScalarAsync<int>(
            world.Database, $"select version from public.manual_test_case where id = '{manualCase:D}'");

        // Soft-delete the case through its own route, then re-import the original file: the code matches a tombstone.
        var deleted = await world.Client.SendAsync(
            HttpMethod.Delete, world.Case(manualCase), world.As, idempotencyKey: NewKey(), ifMatch: $"\"{version}\"");
        deleted.Status.ShouldBe(HttpStatusCode.NoContent, deleted.ErrorMessage);

        var restored = await ImportAsync(world.Client, world.As, world.Project, "csv", CsvBody(rows));

        restored.Status.ShouldBe(HttpStatusCode.OK, restored.Text);
        restored.Count("restored").ShouldBe(1, "the soft-deleted code restores rather than duplicates");
        restored.Outcomes[^1].ShouldBe("restore");
        (await CountAsync(world, "manual_test_case")).ShouldBe(1, "no second case row was created for one code");

        var tombstone = await _fixture.ScalarAsync<bool>(
            world.Database, $"select deleted_at is null from public.manual_test_case where id = '{manualCase:D}'");
        tombstone.ShouldBeTrue("a restore clears deleted_at");

        var restoreVersions = await _fixture.ScalarAsync<long>(
            world.Database,
            $"""
            select count(*) from public.authoring_version
            where entity_id = '{manualCase:D}' and change_kind = 'restore'
            """);
        restoreVersions.ShouldBeGreaterThanOrEqualTo(1L, "the restore appends a change_kind = restore version");

        (await _fixture.ScalarAsync<string>(
            world.Database, $"select title from public.manual_test_case where id = '{manualCase:D}'"))
            .ShouldBe("Card succeeds", "the restored case carries the file's content again");
    }

    /// <summary>
    /// AC14/AC15: only a same-project re-import can preserve the raw <c>content_hash</c> (a cross-project import assigns
    /// new ids), so the re-import must not rewrite a single snapshot: every <c>(entity, version_no, content_hash)</c>
    /// triple is identical before and after.
    /// </summary>
    [Fact]
    public async Task Reimport_SameContentHashes()
    {
        await using var world = await StartAsync();
        var rows = Ladder(await PrefixAsync(world) + "-101");

        (await ImportAsync(world.Client, world.As, world.Project, "csv", CsvBody(rows)))
            .Status.ShouldBe(HttpStatusCode.OK);

        var before = await VersionHashesAsync(world);
        before.Count.ShouldBe(rows.Count);

        var again = await ImportAsync(world.Client, world.As, world.Project, "csv", CsvBody(rows));

        again.Status.ShouldBe(HttpStatusCode.OK, again.Text);
        again.BatchId.ShouldBeNull();
        (await VersionHashesAsync(world)).ShouldBe(before, "a re-import preserves every raw content_hash");
    }

    /// <summary>
    /// AC18: the same <c>Idempotency-Key</c> and body replay the stored answer and write nothing a second time, while a
    /// different body under that key is refused 409 <c>idempotency_conflict</c> and writes nothing.
    /// </summary>
    [Fact]
    public async Task Import_Idempotency_Replay()
    {
        await using var world = await StartAsync();
        var prefix = await PrefixAsync(world);
        var body = CsvBody(Ladder(prefix + "-101"));
        var key = NewKey();

        var first = await ImportAsync(world.Client, world.As, world.Project, "csv", body, idempotencyKey: key);
        first.Status.ShouldBe(HttpStatusCode.OK, first.Text);
        var auditsAfterFirst = await AuditsAsync(world);
        var versionsAfterFirst = await CountAsync(world, "authoring_version");

        var replay = await ImportAsync(world.Client, world.As, world.Project, "csv", body, idempotencyKey: key);

        replay.Status.ShouldBe(HttpStatusCode.OK, replay.Text);
        JsonElement.DeepEquals(first.Body!.Value, replay.Body!.Value)
            .ShouldBeTrue($"the replay answers the first body, not {replay.Text}");
        (await AuditsAsync(world)).ShouldBe(auditsAfterFirst, "a replay appends no audit row");
        (await CountAsync(world, "authoring_version")).ShouldBe(versionsAfterFirst, "a replay appends no version row");

        // The same key with a different file is not a replay.
        var before = await RowCountsAsync(world);
        var conflict = await ImportAsync(
            world.Client, world.As, world.Project, "csv", CsvBody(Ladder(prefix + "-202")), idempotencyKey: key);

        conflict.Status.ShouldBe(HttpStatusCode.Conflict);
        conflict.ErrorCode.ShouldBe("idempotency_conflict");
        (await RowCountsAsync(world)).ShouldBe(before, "a conflicting key writes nothing");
    }

    /// <summary>
    /// AC18: a mutating route without an <c>Idempotency-Key</c> is 400 <c>request_invalid</c> naming
    /// <c>/Idempotency-Key</c> with reason <c>required</c>, and the file is not applied.
    /// </summary>
    [Fact]
    public async Task Import_MissingIdempotencyKey_400()
    {
        await using var world = await StartAsync();
        var before = await RowCountsAsync(world);

        var answer = await ImportAsync(
            world.Client, world.As, world.Project, "csv", CsvBody(Ladder(await PrefixAsync(world) + "-101")),
            withIdempotencyKey: false);

        answer.Status.ShouldBe(HttpStatusCode.BadRequest, answer.Text);
        answer.ErrorCode.ShouldBe("request_invalid");
        answer.Details.ShouldContain(detail => detail.Path == "/Idempotency-Key" && detail.Reason == "required");
        (await RowCountsAsync(world)).ShouldBe(before, "a refused request writes nothing");
    }

    /// <summary>
    /// AC13's <c>unsupported_format</c>: an unknown <c>format</c> is 422 <c>request_invalid</c> naming <c>/format</c>, and
    /// nothing is parsed or written.
    /// </summary>
    [Fact]
    public async Task Import_UnknownFormat_422()
    {
        await using var world = await StartAsync();
        var before = await RowCountsAsync(world);

        var answer = await ImportAsync(
            world.Client, world.As, world.Project, "yaml", CsvBody(Ladder(await PrefixAsync(world) + "-101")));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.Text);
        answer.ErrorCode.ShouldBe("request_invalid");
        answer.Details.ShouldContain(detail => detail.Path == "/format" && detail.Reason == "unsupported_format");
        (await RowCountsAsync(world)).ShouldBe(before, "an unsupported format writes nothing");
    }

    /// <summary>
    /// AC13's <c>code_conflict</c>: a case code that does not carry the project's <c>case_prefix</c> (AC4) refuses the
    /// file, naming <c>/code</c> and the row, and nothing is written.
    /// </summary>
    [Fact]
    public async Task Import_CodePrefixMismatch_CodeConflict()
    {
        await using var world = await StartAsync();
        var prefix = await PrefixAsync(world);
        prefix.ShouldNotBe("ZZZZ", "the fixture project must not already hold the foreign prefix");

        var rows = Ladder("ZZZZ-101");
        var before = await RowCountsAsync(world);

        var answer = await ImportAsync(world.Client, world.As, world.Project, "csv", CsvBody(rows));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.Text);
        answer.ErrorCode.ShouldBe("request_invalid");
        answer.Details.ShouldContain(detail => detail.Reason == "code_conflict" && detail.Path == "/code");
        answer.Details.ShouldContain(detail => detail.Index == rows.Count, "the refused row is the case row");
        (await RowCountsAsync(world)).ShouldBe(before, "a code conflict refuses the whole file");
    }

    /// <summary>
    /// AC17: import needs <c>authoring:write</c>, which the <c>viewer</c> role does not carry — 403 <c>forbidden</c>
    /// before the body is read, and nothing is written.
    /// </summary>
    [Fact]
    public async Task Import_ViewerForbidden_403()
    {
        await using var world = await StartAsync();
        var viewer = await AddMemberAsync(world, "viewer");
        var asViewer = TenancyApiFactory.Resolved(user: viewer, orgHeader: world.Org);
        var before = await RowCountsAsync(world);

        var refused = await ImportAsync(
            world.Client, asViewer, world.Project, "csv", CsvBody(Ladder(await PrefixAsync(world) + "-101")));

        refused.Status.ShouldBe(HttpStatusCode.Forbidden, refused.Text);
        refused.ErrorCode.ShouldBe("forbidden");

        // AC17's control on the same principal: `authoring:read` is exactly what the export route needs.
        var exported = await ExportAsync(world.Client, asViewer, world.Project, "csv");
        exported.Status.ShouldBe(HttpStatusCode.OK, "a viewer holds authoring:read and may export");

        (await RowCountsAsync(world)).ShouldBe(before, "the refused import writes nothing");
    }

    /// <summary>
    /// AC17: export needs <c>authoring:read</c> — a member holding no role is refused 403 <c>forbidden</c> and the file is
    /// not served.
    /// </summary>
    [Fact]
    public async Task Export_NoPermission_403()
    {
        await using var world = await StartAsync();
        var outsider = await AddMemberAsync(world, roleCode: null);
        var asOutsider = TenancyApiFactory.Resolved(user: outsider, orgHeader: world.Org);

        foreach (var format in new[] { "csv", "json", "xlsx" })
        {
            var refused = await ExportAsync(world.Client, asOutsider, world.Project, format);

            refused.Status.ShouldBe(HttpStatusCode.Forbidden, $"{format}: a member without authoring:read must not export");
            refused.ErrorCode.ShouldBe("forbidden", $"{format}: the answer is the 00 §9.2 envelope, not a file");
            refused.HasBom.ShouldBeFalse($"{format}: a refused export serves no file");
        }

        // The control: the founder holds authoring:read, so the same requests are served.
        (await ExportAsync(world.Client, world.As, world.Project, "csv")).Status.ShouldBe(HttpStatusCode.OK);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Rows of one authoring table of the world's project, read as the superuser (RLS-free).</summary>
    private async Task<long> CountAsync(World world, string table) =>
        await _fixture.ScalarAsync<long>(
            world.Database, $"select count(*) from public.{table} where project_id = '{world.Project:D}'");

    /// <summary>
    /// Every <c>audit_log</c> row of the world's org, whatever its action. The helper counts the whole org rather than one
    /// action name: the tests assert a <b>delta</b> (+1 for one successful apply, +0 for a dry run, a re-import or a
    /// replay), so they hold whatever the import route names its audit action.
    /// </summary>
    private async Task<long> AuditsAsync(World world) =>
        await _fixture.ScalarAsync<long>(
            world.Database, $"select count(*) from public.audit_log where org_id = '{world.Org:D}'");

    /// <summary>
    /// The world's row counts plus the code counter's next value, in a fixed order, for a "nothing was written" check. The
    /// counter is in the list because an import that allocated a code before refusing the file would leak a number.
    /// </summary>
    private async Task<IReadOnlyList<long>> RowCountsAsync(World world) =>
    [
        await CountAsync(world, "feature_node"),
        await CountAsync(world, "checklist_item"),
        await CountAsync(world, "manual_test_case"),
        await CountAsync(world, "authoring_version"),
        await _fixture.ScalarAsync<long>(
            world.Database, $"select next_value from public.authoring_code_counter where project_id = '{world.Project:D}'"),
        await _fixture.ScalarAsync<long>(
            world.Database, $"select count(*) from public.audit_log where org_id = '{world.Org:D}'"),
    ];

    /// <summary>Every <c>(entity, version_no, content_hash)</c> of the project, in a stable order.</summary>
    private async Task<IReadOnlyList<string>> VersionHashesAsync(World world) =>
        await _fixture.ColumnAsync<string>(
            world.Database,
            $"""
            select entity_id::text || '|' || version_no || '|' || content_hash from public.authoring_version
            where project_id = '{world.Project:D}' order by entity_id, version_no
            """);

    /// <summary>The <c>change_kind</c> of one entity's newest version row.</summary>
    private async Task<string> LatestChangeKindAsync(World world, Guid entityId) =>
        await _fixture.ScalarAsync<string>(
            world.Database,
            $"""
            select change_kind from public.authoring_version
            where entity_id = '{entityId:D}' order by version_no desc limit 1
            """);

    /// <summary>The statements of the last request that touch the module's tables (the P2 budget).</summary>
    private static int AuthoringStatements(World world) =>
        world.Api.Statements.Snapshot().Count(statement =>
            statement.Contains("public.feature_node", StringComparison.Ordinal)
            || statement.Contains("public.checklist_item", StringComparison.Ordinal)
            || statement.Contains("public.manual_test_case", StringComparison.Ordinal)
            || statement.Contains("public.authoring_version", StringComparison.Ordinal)
            || statement.Contains("public.authoring_code_counter", StringComparison.Ordinal)
            || statement.Contains("public.audit_log", StringComparison.Ordinal));

    /// <summary>
    /// A CSV file of exactly 5 MiB (5 242 880 bytes): four module → feature → category ladders with 348 items under each
    /// category — 1 404 data rows, under the 5 000 row cap — whose descriptions pad the body to the byte, so the cap
    /// boundary can be probed from both sides.
    /// </summary>
    private static byte[] ExactlyFiveMebibytes()
    {
        const int Target = 5 * 1024 * 1024;
        const int Modules = 4;
        const int ItemsPerModule = 348;
        const int Items = Modules * ItemsPerModule;

        var rows = new List<FileRow>();
        for (var module = 1; module <= Modules; module++)
        {
            rows.Add(FileRow.Node($"n{module}", null, "module", $"Bulk {module}"));
            rows.Add(FileRow.Node($"f{module}", $"n{module}", "feature", $"Bulk {module} feature"));
            rows.Add(FileRow.Node($"k{module}", $"f{module}", "category", $"Bulk {module} category"));
        }

        for (var item = 1; item <= Items; item++)
        {
            var module = ((item - 1) / ItemsPerModule) + 1;
            rows.Add(FileRow.Item($"i{item}", $"k{module}", $"Item {item}", string.Empty, "medium"));
        }

        var compact = Encoding.UTF8.GetByteCount(CsvBody(rows));
        var padding = Target - compact;
        var each = padding / Items;
        var remainder = padding % Items;

        (each + 1).ShouldBeLessThan(4000, "a padded description must stay inside the item's 4 000-character column cap");

        var padded = new List<FileRow>(rows.Count);
        var seen = 0;
        foreach (var row in rows)
        {
            if (row.Kind != "item")
            {
                padded.Add(row);
                continue;
            }

            var length = each + (seen < remainder ? 1 : 0);
            padded.Add(row with { Description = new string('x', length) });
            seen++;
        }

        var body = Encoding.UTF8.GetBytes(CsvBody(padded));
        body.Length.ShouldBe(Target, "the padded file must be exactly the cap");

        return body;
    }

    /// <summary>An active member of the org, optionally holding one built-in role, granted by the founder through the API.</summary>
    /// <param name="world">The world.</param>
    /// <param name="roleCode">The role, or null for a member with no role at all.</param>
    private Task<Guid> AddMemberAsync(World world, string? roleCode) =>
        RegistryApi.AddMemberAsync(_fixture, world.Client, world.Database, world.Org, world.As, roleCode);
}
