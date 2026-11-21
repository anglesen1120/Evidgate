using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.AuthoringImportCalls;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class AuthoringImportTests
{
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

    /// <summary>An active member of the org, optionally holding one built-in role, seeded as the owner.</summary>
    /// <param name="world">The world.</param>
    /// <param name="roleCode">The role, or null for a member with no role at all.</param>
    private Task<Guid> AddMemberAsync(World world, string? roleCode) =>
        RegistryApi.AddMemberAsync(_fixture, world.Database, world.Org, roleCode);
}
