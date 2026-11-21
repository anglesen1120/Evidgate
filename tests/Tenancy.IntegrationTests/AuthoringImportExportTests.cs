using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
public sealed partial class AuthoringImportExportTests
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
        var database = await _fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();

        var source = Guid.CreateVersion7();
        var sourceOrg = await CreateOrgAsync(client, source, Unique.Slug("roundtrip-a"));
        var sourceProject = await CreateProjectAsync(client, source, sourceOrg, Unique.Slug("checkout"));

        var mirror = Guid.CreateVersion7();
        var mirrorOrg = await CreateOrgAsync(client, mirror, Unique.Slug("roundtrip-b"));
        var mirrorProject = await CreateProjectAsync(client, mirror, mirrorOrg, Unique.Slug("mirror"));

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
}
