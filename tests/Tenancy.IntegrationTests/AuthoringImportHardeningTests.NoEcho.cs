using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.AuthoringImportCalls;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1's hostile-input rules at the HTTP edge of route 25 (AC16, 00 §5.7 S3): a refused file never echoes a cell
/// value, a step text or a key back — only a fixed column pointer, a reason and a row index — and a request keyed to one
/// project never reads or writes another project's rows, whether that project is a sibling of the same org or belongs to
/// another org.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shared partial class.</b> This file declares the class, its fixture, its world and the workbook helper once; the
/// XLSX slice's archive-shaped cases (zip caps, entry flood, DTD, macro part) live in a second file of this class and add
/// methods only — a partial type may not declare the same member or attribute twice.
/// </para>
/// <para>
/// <b>What "the log" means here.</b> The host's captured log surface is <see cref="TenancyApiFactory.Statements"/>: every
/// command Npgsql executed, whitespace-normalised. A canary there would mean the refused text reached a statement, which
/// 00 §5.7 S3 forbids just as it forbids it in a response.
/// </para>
/// </remarks>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed partial class AuthoringImportHardeningTests
{
    /// <summary>
    /// The marker planted in every refused cell. Long and printable, so a search over a response cannot match it by
    /// accident, and free of characters a CSV or XML writer would have to escape differently.
    /// </summary>
    private const string Canary = "CANARY-do-not-echo-7f3a9c";

    private readonly PostgresTenancyFixture _fixture;

    /// <summary>Wires the fixture.</summary>
    /// <param name="fixture">The shared PostgreSQL fixture.</param>
    public AuthoringImportHardeningTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>
    /// One test world: the host, one org with the project under test and a sibling project of the same org, and a second
    /// org with a project of its own.
    /// </summary>
    /// <param name="Api">The host, whose statement log the no-echo case reads.</param>
    /// <param name="Client">The API client.</param>
    /// <param name="Database">The migrated test database.</param>
    /// <param name="User">The founder of the first org.</param>
    /// <param name="Org">The first org.</param>
    /// <param name="Project">The project under test, in the first org.</param>
    /// <param name="Sibling">A second project of the same org (a different <c>case_prefix</c>).</param>
    /// <param name="Stranger">The founder of the second org.</param>
    /// <param name="OtherOrg">The second org.</param>
    /// <param name="Foreign">A project of the second org.</param>
    private sealed record World(
        TenancyApiFactory Api,
        HttpClient Client,
        string Database,
        Guid User,
        Guid Org,
        Guid Project,
        Guid Sibling,
        Guid Stranger,
        Guid OtherOrg,
        Guid Foreign) : IAsyncDisposable
    {
        /// <summary>The first founder's headers.</summary>
        internal Action<HttpRequestHeaders> As => TenancyApiFactory.Resolved(user: User, orgHeader: Org);

        /// <summary>The second founder's headers.</summary>
        internal Action<HttpRequestHeaders> AsStranger => TenancyApiFactory.Resolved(user: Stranger, orgHeader: OtherOrg);

        /// <summary>The authoring base URL of the project under test.</summary>
        internal string Authoring => AuthoringOf(Project);

        /// <summary>The authoring base URL of one project.</summary>
        /// <param name="project">The project.</param>
        internal static string AuthoringOf(Guid project) => $"/v1/projects/{project}/authoring";

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    /// <summary>Starts the world of the remark above: two orgs, three projects, one database.</summary>
    private async Task<World> StartAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        var client = api.CreateClient();

        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, "hardening-a");
        var project = await CreateProjectAsync(client, user, org, "checkout");
        var sibling = await CreateProjectAsync(client, user, org, "warehouse");

        var stranger = Guid.CreateVersion7();
        var otherOrg = await CreateOrgAsync(client, stranger, "hardening-b");
        var foreign = await CreateProjectAsync(client, stranger, otherOrg, "external");

        return new World(api, client, database, user, org, project, sibling, stranger, otherOrg, foreign);
    }

    /// <summary>
    /// S3/AC16: a file whose rows are refused, with the marker planted in a key, a parent key, a code and a step text,
    /// answers 422 without the marker anywhere in the body, the headers or the captured log — 00 §9.2's details carry a
    /// fixed column pointer, a reason and a row index and nothing else — in every format, and nothing is written.
    /// </summary>
    [Fact]
    public async Task HostileRows_NoEcho()
    {
        await using var world = await StartAsync();
        var prefix = await PrefixAsync(world.Database, world.Project);

        var rows = HostileRows(prefix);

        foreach (var format in new[] { "csv", "json", "xlsx" })
        {
            var before = await CountsAsync(world);
            var body = await BodyAsync(format, rows);

            world.Api.Statements.Clear();
            var answer = await ImportAsync(world.Client, world.As, world.Project, format, body);

            answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, $"{format}: {answer.Text}");
            answer.ErrorCode.ShouldBe("request_invalid");
            answer.Details.ShouldNotBeEmpty($"{format}: the file is refused row by row, with a reason per row");

            answer.Text.ShouldNotContain(Canary, Case.Sensitive, $"{format}: the body echoes no cell value");
            EchoSurface(answer).ShouldNotContain(Canary, Case.Sensitive, $"{format}: no header echoes a cell value");

            var logged = string.Join('\n', world.Api.Statements.Snapshot());
            logged.ShouldNotContain(Canary, Case.Sensitive, $"{format}: no captured statement carries a refused cell value");

            (await CountsAsync(world)).ShouldBe(before, $"{format}: a refused file writes nothing");
        }
    }

    /// <summary>
    /// AC10: the routes are scoped to the project in the address. Exporting a sibling project's <c>node_id</c> is
    /// indistinguishable from exporting an absent node (<c>404</c>, no row of the sibling in the answer), importing a code
    /// another project allocated is a <c>code_conflict</c> rather than a write into either project, importing text that
    /// matches the sibling's titles creates rows only in the addressed project, and another org's project id is not
    /// served at all.
    /// </summary>
    [Fact]
    public async Task OtherOrgAndSiblingProject_Isolation()
    {
        await using var world = await StartAsync();

        var (nodeId, code) = await SeedAsync(world);
        var prefixOfSibling = await PrefixAsync(world.Database, world.Sibling);
        prefixOfSibling.ShouldNotBe(await PrefixAsync(world.Database, world.Project), "one org cannot share a case prefix (AC4)");

        var untouchable = await CountsAsync(world);
        var hashesOfSource = await HashesAsync(world.Database, world.Project);

        // 1. A sibling project cannot export a subtree of the project under test: the id is absent from its scope.
        var leaked = await ExportAsync(world.Client, world.As, world.Sibling, "csv", nodeId);
        leaked.Status.ShouldBe(HttpStatusCode.NotFound, "a node of another project is absent, not forbidden");
        leaked.Text.ShouldNotContain(code, Case.Sensitive, "a refused export serves no row of the other project");

        // 2. A case code another project allocated is a conflict, never a write into either project.
        var foreignCode = Ladder(code);
        var conflicted = await ImportAsync(world.Client, world.As, world.Sibling, "csv", CsvBody(foreignCode));
        conflicted.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, conflicted.Text);
        conflicted.Details.ShouldContain(detail => detail.Reason == "code_conflict" && detail.Path == "/code");
        (await CountsAsync(world)).ShouldBe(untouchable, "a code conflict of the sibling writes nothing anywhere");

        // 3. The same titles under the sibling's own prefix create rows of the sibling only.
        var mirroredRows = Ladder(prefixOfSibling + "-101");
        var mirrored = await ImportAsync(world.Client, world.As, world.Sibling, "csv", CsvBody(mirroredRows));
        mirrored.Status.ShouldBe(HttpStatusCode.OK, mirrored.Text);
        mirrored.Count("created").ShouldBe(mirroredRows.Count);

        (await HashesAsync(world.Database, world.Project))
            .ShouldBe(hashesOfSource, "an import into the sibling appends no version row of the project under test");
        (await CountsAsync(world)).Take(4).ShouldBe(
            untouchable.Take(4),
            "the project under test keeps its node, item, case and version counts");

        // 4. Another org's project id is not served: the caller is not a member of that project's org.
        var denied = await ExportAsync(world.Client, world.AsStranger, world.Project, "csv");
        denied.Status.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Forbidden);
        denied.Text.ShouldNotContain(code, Case.Sensitive, "a project outside the caller's org serves no row");

        // 5. And an import keyed to another org's project writes nothing there, whatever the file says.
        var foreignImport = await ImportAsync(
            world.Client, world.As, world.Foreign, "csv", CsvBody(Ladder(prefixOfSibling + "-301")));
        foreignImport.Status.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Forbidden);
        (await CountAsync(world.Database, world.Foreign, "feature_node"))
            .ShouldBe(0, "an import cannot reach a project of another org");

        // The control: the project under test still exports, and its case is still there.
        var served = await ExportAsync(world.Client, world.As, world.Project, "csv");
        served.Status.ShouldBe(HttpStatusCode.OK);
        served.Text.ShouldContain(code, Case.Sensitive);
    }

    /// <summary>
    /// A workbook of <paramref name="rows"/> built by the module's own writer, for the archive-shaped cases of the second
    /// partial file (the caps are probed by mutating these bytes, not by hand-writing XML).
    /// </summary>
    /// <param name="rows">The rows, in file order; their index is their 1-based position.</param>
    internal static async Task<byte[]> XlsxWorkbookAsync(IReadOnlyList<Evidgate.Modules.Authoring.Application.Import.ImportRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        using var buffer = new MemoryStream();
        await Evidgate.Modules.Authoring.Application.Import.Formats.XlsxFormat
            .WriteAsync(rows, buffer, TestContext.Current.CancellationToken).ConfigureAwait(false);

        return buffer.ToArray();
    }

    /// <summary>
    /// One valid import file: the four-level ladder of AC1 (<c>module → feature → category → sub_category</c>), an item
    /// under the category and a case under the item, so a cross-project case can compare what one project's file does to
    /// the other's rows.
    /// </summary>
    /// <param name="code">The case code, or null to let the allocator number it.</param>
    private static List<FileRow> Ladder(string? code = null) =>
    [
        FileRow.Node("n1", null, "module", "Checkout"),
        FileRow.Node("n2", "n1", "feature", "Payment"),
        FileRow.Node("n3", "n2", "category", "Cards"),
        FileRow.Node("n4", "n3", "sub_category", "3-D Secure"),
        FileRow.Item("i1", "n4", "Pay by card", "Card payment", "high"),
        FileRow.Case("c1", "i1", "Card succeeds", Steps1("Pay", "The order is placed"), "high", code),
    ];

    /// <summary>
    /// The hostile file: nine rows, each refused for a reason of its own, with the marker planted in a key, a parent key,
    /// a title, a code and a step text, so a leak from any of those five cell kinds would show.
    /// </summary>
    /// <param name="prefix">The project's <c>case_prefix</c>, so one case code is otherwise well formed.</param>
    private static List<FileRow> HostileRows(string prefix) =>
    [
        // duplicate_key: the marker is the key the second row repeats.
        FileRow.Node($"{Canary}n1", null, "module", "Checkout"),
        FileRow.Node($"{Canary}n1", null, "module", "Duplicate key"),

        // A live ladder, so the item and case rows below have parents of the right kind.
        FileRow.Node("n1", null, "module", "Warehouse"),
        FileRow.Node("n2", "n1", "feature", "Stock"),
        FileRow.Node("n5", "n2", "category", "Count"),

        // value_invalid at /title: the marker is in a title of 224 code points, over the node bound of 200.
        FileRow.Node("n3", null, "module", Canary + new string('x', 200)),

        // unknown_parent: the marker is the parent key, which names no row above it.
        FileRow.Node("n4", $"{Canary}n9", "feature", "Unknown parent"),

        FileRow.Item("i1", "n5", "Count stock", "A stock count", "high"),

        // The code cell carries the marker and cannot be this project's code, so the row is refused at /code.
        FileRow.Case("c1", "i1", "Count is exact", Steps1("Count", "The count matches"), "high", $"{Canary}-101"),

        // steps_invalid: a step document with an unknown member, with the marker in the action text.
        FileRow.Case(
            "c2",
            "i1",
            "Count is off",
            new JsonObject
            {
                ["steps"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["no"] = 1,
                        ["action"] = Canary,
                        ["expected"] = "A difference is shown",
                        ["injected"] = prefix,
                    },
                },
            },
            "high",
            prefix + "-102"),
    ];

    /// <summary>The project's stored <c>case_prefix</c>.</summary>
    /// <param name="database">The database.</param>
    /// <param name="project">The project.</param>
    private async Task<string> PrefixAsync(string database, Guid project) =>
        await _fixture.ScalarAsync<string>(database, $"select case_prefix from public.project where id = '{project:D}'");

    /// <summary>
    /// The project under test plus its sibling: node, item and case counts and version rows of both, plus the audit rows
    /// of the org, in a fixed order, for a "nothing was written" check.
    /// </summary>
    /// <param name="world">The world.</param>
    private async Task<IReadOnlyList<long>> CountsAsync(World world) =>
    [
        await CountAsync(world.Database, world.Project, "feature_node"),
        await CountAsync(world.Database, world.Project, "checklist_item"),
        await CountAsync(world.Database, world.Project, "manual_test_case"),
        await CountAsync(world.Database, world.Project, "authoring_version"),
        await CountAsync(world.Database, world.Sibling, "feature_node"),
        await CountAsync(world.Database, world.Sibling, "manual_test_case"),
        await _fixture.ScalarAsync<long>(world.Database, $"select count(*) from public.audit_log where org_id = '{world.Org:D}'"),
    ];

    /// <summary>Rows of one authoring table of one project, read as the superuser (RLS-free).</summary>
    /// <param name="database">The database.</param>
    /// <param name="project">The project.</param>
    /// <param name="table">The authoring table.</param>
    private async Task<long> CountAsync(string database, Guid project, string table) =>
        await _fixture.ScalarAsync<long>(database, $"select count(*) from public.{table} where project_id = '{project:D}'");

    /// <summary>Every <c>(entity, version_no, content_hash)</c> of one project, in a stable order.</summary>
    /// <param name="database">The database.</param>
    /// <param name="project">The project.</param>
    private async Task<IReadOnlyList<string>> HashesAsync(string database, Guid project) =>
        await _fixture.ColumnAsync<string>(
            database,
            $"""
            select entity_id::text || '|' || version_no || '|' || content_hash from public.authoring_version
            where project_id = '{project:D}' order by entity_id, version_no
            """);

    /// <summary>Seeds the project under test with one ladder through its routes; returns the module's id and the case code.</summary>
    /// <param name="world">The world.</param>
    private static async Task<(Guid NodeId, string Code)> SeedAsync(World world)
    {
        var moduleId = await CreateAsync(world, "/nodes", new { kind = "module", title = "Checkout" }, "node_id");
        var featureId = await CreateAsync(world, "/nodes", new { parent_id = moduleId, kind = "feature", title = "Payment" }, "node_id");
        var categoryId = await CreateAsync(world, "/nodes", new { parent_id = featureId, kind = "category", title = "Cards" }, "node_id");
        var itemId = await CreateAsync(
            world, "/checklist-items", new { node_id = categoryId, title = "Pay by card", priority = "high" }, "item_id");

        var created = await world.Client.SendAsync(
            HttpMethod.Post,
            $"{world.Authoring}/manual-cases",
            world.As,
            new
            {
                checklist_item_id = itemId,
                title = "Card succeeds",
                steps = new[] { new { no = 1, action = "Pay", expected = "The order is placed" } },
                priority = "high",
            },
            NewKey());

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);

        var code = created.Text("code");
        code.ShouldNotBeNull("a created case carries its code");

        return (moduleId, code!);
    }

    /// <summary>One create through a route of the project under test.</summary>
    /// <param name="world">The world.</param>
    /// <param name="path">The route under <c>…/authoring</c>.</param>
    /// <param name="body">The request body.</param>
    /// <param name="member">The id member of the answer.</param>
    private static async Task<Guid> CreateAsync(World world, string path, object body, string member)
    {
        var created = await world.Client.SendAsync(HttpMethod.Post, world.Authoring + path, world.As, body, NewKey());
        created.Status.ShouldBe(HttpStatusCode.Created, $"{path}: {created.ErrorMessage}");

        return created.Id(member);
    }
}
