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
/// 13.1.1 AC14/AC16 for route 25's export half: a project exports in JSON, CSV and XLSX, an import of that file into an
/// <b>empty</b> project carrying the same <c>case_prefix</c> reproduces the tree — asserted on the export-normalised
/// projection of the <em>latest</em> <c>authoring_version</c> snapshot of each entity, with every project-local uuid
/// replaced by that row's export key — and the two exports are the same file. Export twice from an unchanged tree is byte-identical in every
/// format, CSV carries the UTF-8 BOM and its media type, text is written NFC, and a formula-leading cell is neutralised
/// with one leading <c>'</c> on export and stored literally again on import.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the projection and not the ids.</b> A cross-project import assigns new uuids, so <c>content_hash</c> can only be
/// compared inside one project (AC15, <see cref="AuthoringImportTests.Reimport_SameContentHashes"/>); AC14's equality is
/// therefore stated on the §4 field list with the uuids replaced by export keys, which is exactly what a consumer of the
/// file sees.
/// </para>
/// <para>
/// <b>Two projects, one database.</b> The mirror project lives in a second org: <c>project.case_prefix</c> is unique per
/// org, so two projects of one org cannot share the prefix the case codes of A carry, and the codes are part of the
/// round-trip (AC4/AC15).
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class AuthoringImportExportTests
{
    private readonly PostgresTenancyFixture _fixture;

    /// <summary>Wires the fixture.</summary>
    /// <param name="fixture">The shared PostgreSQL fixture.</param>
    public AuthoringImportExportTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>One test world: the host, the database, and the source and mirror projects with their own founders.</summary>
    /// <param name="Api">The host.</param>
    /// <param name="Client">The API client.</param>
    /// <param name="Database">The migrated test database.</param>
    /// <param name="Source">The founder of the source org.</param>
    /// <param name="SourceOrg">The source org.</param>
    /// <param name="SourceProject">The project the tree is authored in (A).</param>
    /// <param name="Mirror">The founder of the mirror org.</param>
    /// <param name="MirrorOrg">The mirror org.</param>
    /// <param name="MirrorProject">The empty project the export is imported into (B).</param>
    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid Source,
        Guid SourceOrg,
        Guid SourceProject,
        Guid Mirror,
        Guid MirrorOrg,
        Guid MirrorProject) : IAsyncDisposable
    {
        /// <summary>The source founder's headers.</summary>
        internal Action<HttpRequestHeaders> AsSource => TenancyApiFactory.Resolved(user: Source, orgHeader: SourceOrg);

        /// <summary>The mirror founder's headers.</summary>
        internal Action<HttpRequestHeaders> AsMirror => TenancyApiFactory.Resolved(user: Mirror, orgHeader: MirrorOrg);

        /// <summary>The authoring base URL of the source project.</summary>
        internal string SourceAuthoring => $"/v1/projects/{SourceProject}/authoring";

        /// <summary>The authoring base URL of the mirror project.</summary>
        internal string MirrorAuthoring => $"/v1/projects/{MirrorProject}/authoring";

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>
    /// Starts the world: two orgs, the source project, the empty mirror project, and the mirror's <c>case_prefix</c>
    /// patched to the source's so an imported case code is not a <c>code_conflict</c>.
    /// </summary>
    private async Task<World> StartAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();

        var source = Guid.CreateVersion7();
        var sourceOrg = await CreateOrgAsync(client, source, "roundtrip-a");
        var sourceProject = await CreateProjectAsync(client, source, sourceOrg, "checkout");

        var mirror = Guid.CreateVersion7();
        var mirrorOrg = await CreateOrgAsync(client, mirror, "roundtrip-b");
        var mirrorProject = await CreateProjectAsync(client, mirror, mirrorOrg, "mirror");

        var prefix = await _fixture.ScalarAsync<string>(
            database, $"select case_prefix from public.project where id = '{sourceProject:D}'");

        var asMirror = TenancyApiFactory.Resolved(user: mirror, orgHeader: mirrorOrg);
        var read = await client.SendAsync(HttpMethod.Get, $"/v1/projects/{mirrorProject}", asMirror);
        read.Status.ShouldBe(HttpStatusCode.OK, read.ErrorMessage);
        var tag = read.ETag;
        tag.ShouldNotBeNull("a project read carries its ETag");
        var patched = await client.SendAsync(
            HttpMethod.Patch, $"/v1/projects/{mirrorProject}", asMirror, new { case_prefix = prefix }, NewKey(), tag);
        patched.Status.ShouldBe(HttpStatusCode.OK, $"the mirror project must share the prefix {prefix}: {patched.ErrorMessage}");

        return new World(api, client, database, source, sourceOrg, sourceProject, mirror, mirrorOrg, mirrorProject);
    }

    /// <summary>
    /// AC14 for the two text formats: the tree of A exports as JSON and as CSV, the file imports into the empty project B,
    /// and both the export-normalised projection of each entity's latest version snapshot and the export itself are equal.
    /// </summary>
    /// <param name="format"><c>json</c> or <c>csv</c>.</param>
    [Theory]
    [InlineData("json")]
    [InlineData("csv")]
    public async Task RoundTrip_ProjectionEqual_JsonAndCsv(string format)
    {
        await using var world = await StartAsync();
        await FillAsync(world);

        var projection = await ProjectionAsync(world.Database, world.SourceProject);
        projection.Count.ShouldBeGreaterThan(15, "the fixture tree must be worth round-tripping");

        var exported = await ExportAsync(world.Client, world.AsSource, world.SourceProject, format);
        exported.Status.ShouldBe(HttpStatusCode.OK, $"{format}: export must serve the file");

        var imported = await ImportAsync(world.Client, world.AsMirror, world.MirrorProject, format, exported.Bytes);
        imported.Status.ShouldBe(HttpStatusCode.OK, $"{format}: {imported.Text}");
        imported.Count("created").ShouldBe(projection.Count, $"{format}: every exported row creates one entity in B");

        var mirrored = await ProjectionAsync(world.Database, world.MirrorProject);
        mirrored.ShouldBe(projection, $"{format}: B reproduces the export-normalised projection of A");

        var reExported = await ExportAsync(world.Client, world.AsMirror, world.MirrorProject, format);
        reExported.Status.ShouldBe(HttpStatusCode.OK, $"{format}: B exports");
        reExported.Bytes.ShouldBe(exported.Bytes, $"{format}: A's export and B's export are the same file");
    }

    /// <summary>
    /// AC14 for XLSX: the workbook A exports imports into the empty project B and reproduces the same projection; the two
    /// workbooks carry the same rows (a zip may not be byte-identical across writers, so the rows are compared through
    /// the module's own reader) and every cell is inside the profile's caps.
    /// </summary>
    [Fact]
    public async Task RoundTrip_ProjectionEqual_Xlsx()
    {
        await using var world = await StartAsync();
        await FillAsync(world);

        var projection = await ProjectionAsync(world.Database, world.SourceProject);

        var exported = await ExportAsync(world.Client, world.AsSource, world.SourceProject, "xlsx");
        exported.Status.ShouldBe(HttpStatusCode.OK);
        exported.ContentType.ShouldNotBeNull();
        exported.ContentType!.ShouldStartWith(XlsxMediaType);

        var imported = await ImportAsync(world.Client, world.AsMirror, world.MirrorProject, "xlsx", exported.Bytes);
        imported.Status.ShouldBe(HttpStatusCode.OK, imported.Text);
        imported.Count("created").ShouldBe(projection.Count, "every exported row creates one entity in B");

        (await ProjectionAsync(world.Database, world.MirrorProject))
            .ShouldBe(projection, "B reproduces the export-normalised projection of A");

        var rowsOfA = XlsxRows(exported.Bytes);
        var rowsOfB = XlsxRows((await ExportAsync(world.Client, world.AsMirror, world.MirrorProject, "xlsx")).Bytes);

        rowsOfA.Count.ShouldBe(projection.Count, "the workbook holds one row per exported entity");
        rowsOfB.ShouldBe(rowsOfA, "B's workbook carries the rows of A's workbook");
    }

    /// <summary>
    /// AC14: two exports of an unchanged tree are byte-identical in every format — no timestamp, no map iteration order and
    /// no random key may reach the file, so a diff of two exports is a diff of the tree.
    /// </summary>
    /// <param name="format"><c>json</c>, <c>csv</c> or <c>xlsx</c>.</param>
    [Theory]
    [InlineData("json")]
    [InlineData("csv")]
    [InlineData("xlsx")]
    public async Task Export_Twice_ByteIdentical(string format)
    {
        await using var world = await StartAsync();
        await FillAsync(world);

        var first = await ExportAsync(world.Client, world.AsSource, world.SourceProject, format);
        var second = await ExportAsync(world.Client, world.AsSource, world.SourceProject, format);

        first.Status.ShouldBe(HttpStatusCode.OK, $"{format}: export must serve the file");
        second.Status.ShouldBe(HttpStatusCode.OK, $"{format}: export must serve the file again");
        second.Bytes.Length.ShouldBe(first.Bytes.Length, $"{format}: two exports have the same length");
        second.Bytes.ShouldBe(first.Bytes, $"{format}: two exports of an unchanged tree are the same file");
    }

    /// <summary>
    /// AC14: the CSV export is UTF-8 with a byte-order mark and declares <c>text/csv; charset=utf-8</c>, so a spreadsheet
    /// reads unicode titles instead of mojibake.
    /// </summary>
    [Fact]
    public async Task Export_CsvHasBomAndContentType()
    {
        await using var world = await StartAsync();
        await FillAsync(world);

        var exported = await ExportAsync(world.Client, world.AsSource, world.SourceProject, "csv");

        exported.Status.ShouldBe(HttpStatusCode.OK);
        exported.HasBom.ShouldBeTrue("a CSV export starts with the UTF-8 byte-order mark");

        var afterBom = exported.Bytes[3..];
        afterBom.Length.ShouldBeGreaterThan(3);
        Encoding.UTF8.GetString(afterBom).ShouldNotStartWith("\uFEFF", Case.Sensitive, "the BOM appears exactly once");

        exported.ContentType.ShouldNotBeNull("the export declares its media type");
        exported.ContentType!.ShouldStartWith("text/csv");
        exported.ContentType.ShouldContain("charset=utf-8", Case.Sensitive);
        exported.Text.ShouldStartWith("\uFEFF" + Header, Case.Sensitive, "the BOM is followed by the header row");
    }

    /// <summary>
    /// AC16 both ways: a title leading with <c>=</c>, <c>+</c>, <c>-</c> or <c>@</c> is neutralised with exactly one
    /// leading <c>'</c> on export, a genuine apostrophe is not doubled, and an import strips one <c>'</c> only when one of
    /// those characters follows — so the stored value is the literal text the author typed, never a formula.
    /// </summary>
    [Fact]
    public async Task Export_FormulaCells_PrefixedAndRoundTrip()
    {
        await using var world = await StartAsync();

        await CreateNodeAsync(world, world.AsSource, world.SourceAuthoring, null, "module", "=1+1");
        await CreateNodeAsync(world, world.AsSource, world.SourceAuthoring, null, "module", "+SUM(A1)");
        await CreateNodeAsync(world, world.AsSource, world.SourceAuthoring, null, "module", "@user");
        await CreateNodeAsync(world, world.AsSource, world.SourceAuthoring, null, "module", "-2");
        await CreateNodeAsync(world, world.AsSource, world.SourceAuthoring, null, "module", "'quoted");

        foreach (var format in new[] { "csv", "xlsx" })
        {
            var exported = await ExportAsync(world.Client, world.AsSource, world.SourceProject, format);
            exported.Status.ShouldBe(HttpStatusCode.OK, $"{format}: export");

            if (format == "csv")
            {
                var text = exported.Text;

                text.ShouldContain("'=1+1", Case.Sensitive, "a formula-leading cell is prefixed on export");
                text.ShouldContain("'+SUM(A1)", Case.Sensitive);
                text.ShouldContain("'@user", Case.Sensitive);
                text.ShouldContain("'-2", Case.Sensitive);
                text.ShouldContain("'quoted", Case.Sensitive);
                text.ShouldNotContain("''quoted", Case.Sensitive, "a genuine apostrophe is not doubled");
            }

            var imported = await ImportAsync(world.Client, world.AsMirror, world.MirrorProject, format, exported.Bytes);
            imported.Status.ShouldBe(HttpStatusCode.OK, $"{format}: {imported.Text}");

            var stored = await _fixture.ColumnAsync<string>(
                world.Database,
                $"select title from public.feature_node where project_id = '{world.MirrorProject:D}' order by position, id");
            stored.ShouldBe(
                ["=1+1", "+SUM(A1)", "@user", "-2", "'quoted"],
                $"{format}: an imported cell is stored as literal text, with exactly one protecting apostrophe stripped");

            var storedOfSource = await _fixture.ColumnAsync<string>(
                world.Database,
                $"select title from public.feature_node where project_id = '{world.SourceProject:D}' order by position, id");
            storedOfSource.ShouldBe(stored, $"{format}: B quotes exactly what A stores");
        }
    }

    /// <summary>
    /// AC14: every text member is written NFC on export. A title authored in decomposed form (NFD) is stored precomposed
    /// (the write path normalises) and leaves the export as NFC — no combining mark survives into the file a consumer
    /// parses.
    /// </summary>
    [Fact]
    public async Task Export_Nfc()
    {
        await using var world = await StartAsync();

        const string Decomposed = "Ca\u0300 phe\u0302";
        const string Composed = "Cà phê";

        await CreateNodeAsync(world, world.AsSource, world.SourceAuthoring, null, "module", Decomposed);

        (await _fixture.ScalarAsync<string>(
            world.Database, $"select title from public.feature_node where project_id = '{world.SourceProject:D}'"))
            .ShouldBe(Composed, "the write path normalises to NFC");

        foreach (var format in new[] { "json", "csv" })
        {
            var exported = await ExportAsync(world.Client, world.AsSource, world.SourceProject, format);
            exported.Status.ShouldBe(HttpStatusCode.OK, $"{format}: export");

            var text = exported.Text;
            text.ShouldContain(Composed, Case.Sensitive, $"{format}: the export is NFC");
            text.ShouldNotContain("\u0300", Case.Sensitive, $"{format}: no combining grave accent leaves the export");
            text.ShouldNotContain("\u0302", Case.Sensitive, $"{format}: no combining circumflex leaves the export");
            Encoding.UTF8.GetString(exported.Bytes).IsNormalized(NormalizationForm.FormC)
                .ShouldBeTrue($"{format}: the whole file is NFC");
        }
    }

    /// <summary>
    /// AC14's byte half, at the route: a file the import could not accept is refused, not served. A hundred cases whose
    /// <c>steps</c> each hold 20 000 three-byte characters make a CSV of about 6 MiB out of row text of only 2 million
    /// characters, so the refusal comes from the bytes the writer produced and not from the row cap or the text budget;
    /// it is the same 413 envelope, pointer and reason the row cap uses.
    /// </summary>
    [Fact]
    public async Task Export_OverTheBodyCap_413()
    {
        await using var world = await StartAsync();
        await FillAsync(world);

        // Rows are under the cap (and the text under the body cap in characters); only the encoded bytes are over it.
        await _fixture.ExecuteAsync(
            world.Database,
            $"""
            insert into public.manual_test_case
              (id, org_id, project_id, checklist_item_id, code, title, steps, priority, state, source)
            select gen_random_uuid(), '{world.SourceOrg:D}', '{world.SourceProject:D}',
                   (select id from public.checklist_item where project_id = '{world.SourceProject:D}' order by position, id limit 1),
                   'SHOP-' || lpad((1000 + generated.number)::text, 4, '0'),
                   'Wide ' || generated.number::text,
                   jsonb_build_object('steps', jsonb_build_array(jsonb_build_object(
                       'no', 1, 'action', repeat('ằ', 20000), 'expected', 'Kết quả'))),
                   'medium', 'draft', 'manual'
              from generate_series(1, 100) as generated(number);
            """);

        var exported = await ExportAsync(world.Client, world.AsSource, world.SourceProject, "csv");

        exported.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge, exported.Text);
        exported.ErrorCode.ShouldBe("payload_too_large");
        exported.ErrorDetails.ShouldBe([("/node_id", "body_too_large")]);
        exported.Text.ShouldNotContain("ằ", Case.Sensitive, "a refusal names a pointer, never a cell");
    }

    // ------------------------------------------------------------------ the fixture tree

    /// <summary>
    /// Authors the tree of AC14 in project A: two modules at the root, the four-level ladder of AC1, items under
    /// categories, cases with multi-step documents — one step carrying <c>data</c> — a decomposed unicode title and
    /// formula-leading titles, so every field the projection compares is exercised.
    /// </summary>
    /// <param name="world">The world whose source project receives the tree.</param>
    private static async Task FillAsync(World world)
    {
        var checkout = await CreateNodeAsync(world, world.AsSource, world.SourceAuthoring, null, "module", "Checkout");
        var payment = await CreateNodeAsync(world, world.AsSource, world.SourceAuthoring, checkout, "feature", "Payment");
        var cards = await CreateNodeAsync(world, world.AsSource, world.SourceAuthoring, payment, "category", "Cards");
        var secure = await CreateNodeAsync(world, world.AsSource, world.SourceAuthoring, cards, "sub_category", "3-D Secure");

        var payByCard = await CreateItemAsync(world, world.AsSource, world.SourceAuthoring, secure, "Pay by card", "A card payment", "high");
        await CreateCaseAsync(
            world, world.AsSource, world.SourceAuthoring, payByCard, "Card succeeds", "high",
            Step(1, "Open the cart", "The cart is shown"),
            Step(2, "Add a card", "The card is listed", "4242"),
            Step(3, "Pay", "The order is placed"));
        await CreateCaseAsync(world, world.AsSource, world.SourceAuthoring, payByCard, "Card declines", "critical", Step(1, "Pay with 4000", "A decline is shown"));

        var refund = await CreateItemAsync(world, world.AsSource, world.SourceAuthoring, cards, "Refund a card", null, "critical");
        await CreateCaseAsync(world, world.AsSource, world.SourceAuthoring, refund, "Refund is issued", "medium", Step(1, "Refund", "The refund is issued"));

        var wallets = await CreateNodeAsync(world, world.AsSource, world.SourceAuthoring, payment, "category", "Wallets");
        var wallet = await CreateItemAsync(world, world.AsSource, world.SourceAuthoring, wallets, "Pay by wallet", "A wallet payment", "medium");
        await CreateCaseAsync(world, world.AsSource, world.SourceAuthoring, wallet, "Wallet succeeds", "medium", Step(1, "Pay by wallet", "The order is placed"));

        var warehouse = await CreateNodeAsync(world, world.AsSource, world.SourceAuthoring, null, "module", "Warehouse");
        var stock = await CreateNodeAsync(world, world.AsSource, world.SourceAuthoring, warehouse, "feature", "Stock");
        var count = await CreateNodeAsync(world, world.AsSource, world.SourceAuthoring, stock, "category", "Count");
        var countItem = await CreateItemAsync(world, world.AsSource, world.SourceAuthoring, count, "Count stock", null, "low");
        await CreateCaseAsync(world, world.AsSource, world.SourceAuthoring, countItem, "Count is exact", "low", Step(1, "Count", "The count matches"));

        // The unicode and formula titles of AC14/AC16, on the one ladder that still accepts children.
        var accents = await CreateNodeAsync(world, world.AsSource, world.SourceAuthoring, warehouse, "feature", "Ca\u0300 phe\u0302");
        var formulas = await CreateNodeAsync(world, world.AsSource, world.SourceAuthoring, accents, "category", "=1+1");
        var formulaItem = await CreateItemAsync(world, world.AsSource, world.SourceAuthoring, formulas, "Item +SUM(A1)", null, "medium");
        await CreateCaseAsync(world, world.AsSource, world.SourceAuthoring, formulaItem, "+SUM(A1) case", "medium", Step(1, "Evaluate", "Nothing is evaluated"));
    }

    /// <summary>One case step; <paramref name="data"/> is omitted when null (the schema refuses a non-string <c>data</c>).</summary>
    /// <param name="no">The 1-based step number.</param>
    /// <param name="action">The action text.</param>
    /// <param name="expected">The expected result.</param>
    /// <param name="data">The optional data text.</param>
    private static Dictionary<string, object> Step(int no, string action, string expected, string? data = null)
    {
        var step = new Dictionary<string, object> { ["no"] = no, ["action"] = action, ["expected"] = expected };

        if (data is not null)
        {
            step["data"] = data;
        }

        return step;
    }

    /// <summary>Creates one node through its route and returns its id.</summary>
    /// <param name="world">The world.</param>
    /// <param name="authorize">The caller.</param>
    /// <param name="authoring">The project's authoring base URL.</param>
    /// <param name="parent">The parent node, or null for a root module.</param>
    /// <param name="kind">The node kind.</param>
    /// <param name="title">The title as the author typed it.</param>
    private static async Task<Guid> CreateNodeAsync(
        World world,
        Action<HttpRequestHeaders> authorize,
        string authoring,
        Guid? parent,
        string kind,
        string title)
    {
        object body = parent is { } parentId
            ? new { parent_id = parentId, kind, title }
            : new { kind, title };

        var created = await world.Client.SendAsync(HttpMethod.Post, $"{authoring}/nodes", authorize, body, NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created, $"{kind} '{title}': {created.ErrorMessage}");

        return created.Id("node_id");
    }

    /// <summary>Creates one checklist item through its route and returns its id.</summary>
    /// <param name="world">The world.</param>
    /// <param name="authorize">The caller.</param>
    /// <param name="authoring">The project's authoring base URL.</param>
    /// <param name="node">The owning node.</param>
    /// <param name="title">The title.</param>
    /// <param name="description">The description, or null.</param>
    /// <param name="priority">The priority.</param>
    private static async Task<Guid> CreateItemAsync(
        World world,
        Action<HttpRequestHeaders> authorize,
        string authoring,
        Guid node,
        string title,
        string? description,
        string priority)
    {
        object body = description is null
            ? new { node_id = node, title, priority }
            : new { node_id = node, title, description, priority };

        var created = await world.Client.SendAsync(HttpMethod.Post, $"{authoring}/checklist-items", authorize, body, NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created, $"item '{title}': {created.ErrorMessage}");

        return created.Id("item_id");
    }

    /// <summary>Creates one manual case through its route and returns its id and allocated code.</summary>
    /// <param name="world">The world.</param>
    /// <param name="authorize">The caller.</param>
    /// <param name="authoring">The project's authoring base URL.</param>
    /// <param name="item">The owning item.</param>
    /// <param name="title">The title.</param>
    /// <param name="priority">The priority.</param>
    /// <param name="steps">The steps, as the schema's step objects.</param>
    private static async Task<(Guid Id, string Code)> CreateCaseAsync(
        World world,
        Action<HttpRequestHeaders> authorize,
        string authoring,
        Guid item,
        string title,
        string priority,
        params Dictionary<string, object>[] steps)
    {
        var created = await world.Client.SendAsync(
            HttpMethod.Post,
            $"{authoring}/manual-cases",
            authorize,
            new { checklist_item_id = item, title, steps, priority },
            NewKey());

        created.Status.ShouldBe(HttpStatusCode.Created, $"case '{title}': {created.ErrorMessage}");

        var code = created.Text("code");
        code.ShouldNotBeNull($"case '{title}' must come back with its allocated code");

        return (created.Id("case_id"), code!);
    }

    // ------------------------------------------------------------------ the export-normalised projection

    /// <summary>One node as the key assignment needs it.</summary>
    /// <param name="Id">The entity id.</param>
    /// <param name="Parent">The parent node, or null for a root.</param>
    private sealed record NodeRow(Guid Id, Guid? Parent);

    /// <summary>One item as the key assignment needs it.</summary>
    /// <param name="Id">The entity id.</param>
    /// <param name="Node">The owning node.</param>
    private sealed record ItemRow(Guid Id, Guid Node);

    /// <summary>One case as the key assignment needs it.</summary>
    /// <param name="Id">The entity id.</param>
    /// <param name="Item">The owning item.</param>
    private sealed record CaseRow(Guid Id, Guid Item);

    /// <summary>
    /// The export-normalised projection of each entity's latest <c>authoring_version</c> snapshot in one project, in the
    /// export's own key order: the §4 field list of each entity with its three project-local uuid members replaced by the row's export key
    /// (<c>n1…</c>, <c>i1…</c>, <c>c1…</c> numbered in depth-first tree order). Two projects are equal when their lists
    /// are, whatever ids the database assigned.
    /// </summary>
    /// <param name="database">The database.</param>
    /// <param name="project">The project.</param>
    private async Task<List<string>> ProjectionAsync(string database, Guid project)
    {
        var nodes = (await _fixture.ColumnAsync<string>(
                database,
                $"""
                select json_build_object('id', id, 'parent', parent_id)::text from public.feature_node
                where project_id = '{project:D}' order by position, id
                """))
            .Select(row =>
            {
                using var document = JsonDocument.Parse(row);
                var root = document.RootElement;
                return new NodeRow(
                    root.GetProperty("id").GetGuid(),
                    root.GetProperty("parent").ValueKind == JsonValueKind.Null ? null : root.GetProperty("parent").GetGuid());
            })
            .ToList();

        var items = (await _fixture.ColumnAsync<string>(
                database,
                $"""
                select json_build_object('id', id, 'node', feature_node_id)::text from public.checklist_item
                where project_id = '{project:D}' order by position, id
                """))
            .Select(row =>
            {
                using var document = JsonDocument.Parse(row);
                var root = document.RootElement;
                return new ItemRow(root.GetProperty("id").GetGuid(), root.GetProperty("node").GetGuid());
            })
            .ToList();

        var cases = (await _fixture.ColumnAsync<string>(
                database,
                $"""
                select json_build_object('id', id, 'item', checklist_item_id)::text from public.manual_test_case
                where project_id = '{project:D}' order by code, id
                """))
            .Select(row =>
            {
                using var document = JsonDocument.Parse(row);
                var root = document.RootElement;
                return new CaseRow(root.GetProperty("id").GetGuid(), root.GetProperty("item").GetGuid());
            })
            .ToList();

        var contents = new Dictionary<Guid, JsonElement>();
        foreach (var row in await _fixture.ColumnAsync<string>(
            database,
            $"""
            select json_build_object('id', entity_id, 'content', content)::text
            from (select distinct on (entity_id) entity_id, content from public.authoring_version
                  where project_id = '{project:D}' order by entity_id, version_no desc) snapshots
            """))
        {
            using var document = JsonDocument.Parse(row);
            contents[document.RootElement.GetProperty("id").GetGuid()] = document.RootElement.GetProperty("content").Clone();
        }

        var children = nodes.Where(node => node.Parent is not null).GroupBy(node => node.Parent!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());
        var itemsByNode = items.GroupBy(item => item.Node).ToDictionary(group => group.Key, group => group.ToList());
        var casesByItem = cases.GroupBy(testCase => testCase.Item).ToDictionary(group => group.Key, group => group.ToList());

        var keys = new Dictionary<Guid, string>();
        var order = new List<(string Kind, Guid Id)>();
        var nodeNumber = 0;
        var itemNumber = 0;
        var caseNumber = 0;

        void Visit(Guid node)
        {
            keys[node] = "n" + (++nodeNumber).ToString(System.Globalization.CultureInfo.InvariantCulture);
            order.Add(("node", node));

            foreach (var item in itemsByNode.GetValueOrDefault(node) ?? [])
            {
                keys[item.Id] = "i" + (++itemNumber).ToString(System.Globalization.CultureInfo.InvariantCulture);
                order.Add(("item", item.Id));

                foreach (var testCase in casesByItem.GetValueOrDefault(item.Id) ?? [])
                {
                    keys[testCase.Id] = "c" + (++caseNumber).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    order.Add(("case", testCase.Id));
                }
            }

            foreach (var child in children.GetValueOrDefault(node) ?? [])
            {
                Visit(child.Id);
            }
        }

        foreach (var root in nodes.Where(node => node.Parent is null))
        {
            Visit(root.Id);
        }

        var projection = new List<string>(order.Count);

        foreach (var (kind, id) in order)
        {
            var content = contents[id];
            var view = new JsonObject { ["kind"] = kind };

            switch (kind)
            {
                case "node":
                    view["node_kind"] = Member(content, "kind");
                    view["title"] = Member(content, "title");
                    view["position"] = Member(content, "position");
                    view["parent"] = Reference(content, "parent_id", keys);
                    break;

                case "item":
                    view["node"] = Reference(content, "feature_node_id", keys);
                    view["title"] = Member(content, "title");
                    view["description"] = Member(content, "description");
                    view["priority"] = Member(content, "priority");
                    view["state"] = Member(content, "state");
                    view["position"] = Member(content, "position");
                    break;

                default:
                    view["item"] = Reference(content, "checklist_item_id", keys);
                    view["code"] = Member(content, "code");
                    view["title"] = Member(content, "title");
                    view["description"] = Member(content, "description");
                    view["precondition"] = Member(content, "precondition");
                    view["steps"] = Member(content, "steps");
                    view["priority"] = Member(content, "priority");
                    view["state"] = Member(content, "state");
                    break;
            }

            projection.Add(view.ToJsonString());
        }

        return projection;
    }

    /// <summary>One member of a snapshot, copied as a JSON node (null included).</summary>
    /// <param name="content">The snapshot.</param>
    /// <param name="name">The member name.</param>
    private static JsonNode? Member(JsonElement content, string name) =>
        content.GetProperty(name).ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(content.GetProperty(name).GetRawText());

    /// <summary>One project-local uuid member of a snapshot, replaced by the export key of the entity it names.</summary>
    /// <param name="content">The snapshot.</param>
    /// <param name="name">The member name.</param>
    /// <param name="keys">The entity-to-key map of the project.</param>
    private static JsonNode? Reference(JsonElement content, string name, Dictionary<Guid, string> keys)
    {
        var member = content.GetProperty(name);

        return member.ValueKind == JsonValueKind.Null ? null : keys[member.GetGuid()];
    }

    /// <summary>The rows one workbook carries, read by the module's own reader.</summary>
    /// <param name="bytes">The workbook.</param>
    private static IReadOnlyList<Evidgate.Modules.Authoring.Application.Import.ImportRow> XlsxRows(byte[] bytes)
    {
        var parse = ReadXlsx(bytes);

        parse.TooLarge.ShouldBeFalse("an exported workbook is inside the caps");
        parse.Issues.ShouldBeEmpty("the module's own reader parses the module's own workbook");
        parse.Rows.Count.ShouldBeGreaterThan(0);

        return parse.Rows;
    }
}
