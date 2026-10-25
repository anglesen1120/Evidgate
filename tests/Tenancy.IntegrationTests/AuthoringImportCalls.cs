using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.Modules.Authoring.Application.Import;
using Evidgate.Modules.Authoring.Application.Import.Formats;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The HTTP edge of 13.1.1 §5 route 25 — <c>POST …/authoring/import</c> and <c>GET …/authoring/export</c> — shared by
/// the import, round-trip and hardening suites of this task: one raw-body request per format (JSON, CSV, XLSX), the
/// export download, and the file builders that make a flat import table out of <see cref="FileRow"/>s.
/// </summary>
/// <remarks>
/// <para>
/// <b>Raw bodies.</b> Import takes the file itself, not a JSON envelope, so these helpers build a
/// <see cref="ByteArrayContent"/> with the format's own media type and always add <c>Idempotency-Key</c> (AC18) unless a
/// test is proving its absence.
/// </para>
/// <para>
/// <b>No second caller contract.</b> <see cref="AuthoringCalls"/>, which the tree/item/case routes share, encodes a JSON
/// body and a parsed answer; this file exists so no suite has to grow that type and so the two new routes' headers are
/// spelled out in one place.
/// </para>
/// </remarks>
internal static class AuthoringImportCalls
{
    /// <summary>The CSV media type the import route accepts.</summary>
    internal const string CsvMediaType = "text/csv";

    /// <summary>The JSON media type of the import document and of the JSON export.</summary>
    internal const string JsonMediaType = "application/json";

    /// <summary>The XLSX media type of the export and of an uploaded workbook.</summary>
    internal const string XlsxMediaType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>The header of the flat table, in the order the contract fixes.</summary>
    internal static readonly string[] Columns =
    [
        "kind", "key", "parent_key", "item_key", "node_kind", "title", "description", "precondition", "steps", "priority",
        "state", "code", "position",
    ];

    /// <summary>The header row of a CSV import file (no trailing newline).</summary>
    internal static string Header => string.Join(',', Columns);

    /// <summary>One <c>details[]</c> entry of a 422 import refusal.</summary>
    /// <param name="Path">The fixed member pointer (<c>/title</c>, <c>/file</c>, …), or null when absent.</param>
    /// <param name="Reason">The AC13/AC16 reason.</param>
    /// <param name="Index">The 1-based data row, or null for a file-level reason.</param>
    internal sealed record ImportDetail(string? Path, string? Reason, int? Index);

    /// <summary>One import answer: status, parsed body (null when the body is not JSON), raw text and headers.</summary>
    /// <param name="Status">The status line.</param>
    /// <param name="Body">The parsed body, or null.</param>
    /// <param name="Text">The raw body text, which is what a no-echo assertion reads.</param>
    /// <param name="Headers">The response headers, which a no-echo assertion also reads.</param>
    internal sealed record ImportAnswer(HttpStatusCode Status, JsonElement? Body, string Text, HttpResponseHeaders Headers)
    {
        /// <summary>The 00 §9.2 <c>error.code</c>, or null on success.</summary>
        internal string? ErrorCode =>
            Body is { } body && body.TryGetProperty("error", out var error) ? error.GetProperty("code").GetString() : null;

        /// <summary>The 00 §9.2 <c>error.message</c>, or null on success.</summary>
        internal string? ErrorMessage =>
            Body is { } body && body.TryGetProperty("error", out var error) ? error.GetProperty("message").GetString() : null;

        /// <summary>The <c>details[]</c> entries of an error envelope, empty on success.</summary>
        internal IReadOnlyList<ImportDetail> Details => Body is { } body && body.TryGetProperty("error", out var error)
            ? [.. error.GetProperty("details").EnumerateArray().Select(detail => new ImportDetail(
                detail.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String ? path.GetString() : null,
                detail.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null,
                detail.TryGetProperty("index", out var index) && index.ValueKind == JsonValueKind.Number ? index.GetInt32() : null))]
            : [];

        /// <summary>The <c>details[].reason</c> values of an error envelope.</summary>
        internal IReadOnlyList<string> Reasons => [.. Details.Select(detail => detail.Reason ?? string.Empty)];

        /// <summary>The single <c>batch_id</c> of the import, or null when the report carries none (dry run, all unchanged).</summary>
        internal Guid? BatchId => Member("batch_id") is { ValueKind: JsonValueKind.String } batch ? batch.GetGuid() : null;

        /// <summary>The <c>counts</c> object of a 200 report.</summary>
        internal JsonElement Counts => Member("counts");

        /// <summary>One member of <see cref="Counts"/>.</summary>
        /// <param name="name"><c>created</c>, <c>updated</c>, <c>restored</c> or <c>unchanged</c>.</param>
        internal int Count(string name) => Counts.GetProperty(name).GetInt32();

        /// <summary>The <c>rows[]</c> entries of a 200 report, in input order.</summary>
        internal IReadOnlyList<JsonElement> Rows => Member("rows") is { ValueKind: JsonValueKind.Array } rows
            ? [.. rows.EnumerateArray()]
            : [];

        /// <summary>The <c>rows[].outcome</c> values of a 200 report, in input order.</summary>
        internal IReadOnlyList<string> Outcomes => [.. Rows.Select(row => row.GetProperty("outcome").GetString()!)];

        /// <summary>The <c>rows[].code</c> values of a 200 report, in input order.</summary>
        internal IReadOnlyList<string?> Codes => [.. Rows.Select(row => row.GetProperty("code").GetString())];

        /// <summary>The <c>rows[].index</c> values of a 200 report, in input order.</summary>
        internal IReadOnlyList<int> Indexes => [.. Rows.Select(row => row.GetProperty("index").GetInt32())];

        private JsonElement Member(string name) => Body!.Value.GetProperty(name);
    }

    /// <summary>One export answer: status, the raw bytes and the declared content type.</summary>
    /// <param name="Status">The status line.</param>
    /// <param name="Bytes">The file, byte for byte (empty on an error).</param>
    /// <param name="ContentType">The <c>Content-Type</c> the route declared, or null.</param>
    internal sealed record ExportAnswer(HttpStatusCode Status, byte[] Bytes, string? ContentType)
    {
        /// <summary>The file decoded as UTF-8 text (CSV and JSON exports).</summary>
        internal string Text => Encoding.UTF8.GetString(Bytes);

        /// <summary>True when the file starts with the UTF-8 byte-order mark.</summary>
        internal bool HasBom => Bytes.Length >= 3 && Bytes[0] == 0xEF && Bytes[1] == 0xBB && Bytes[2] == 0xBF;

        /// <summary>The 00 §9.2 <c>error.code</c> when the answer is an error envelope, or null.</summary>
        internal string? ErrorCode
        {
            get
            {
                if (Bytes.Length == 0 || !Text.TrimStart().StartsWith('{'))
                {
                    return null;
                }

                using var document = JsonDocument.Parse(Text);
                return document.RootElement.TryGetProperty("error", out var error) ? error.GetProperty("code").GetString() : null;
            }
        }
    }

    /// <summary>
    /// One row of the flat table as a test writes it: the file key, the ladders of §4 and the case document. The row
    /// renders itself as a CSV line, a JSON row and the module's own <see cref="ImportRow"/>.
    /// </summary>
    /// <param name="Kind"><c>node</c>, <c>item</c> or <c>case</c>.</param>
    /// <param name="Key">The file-local key.</param>
    /// <param name="ParentKey">Node: the parent node key or null. Item: the owning node key.</param>
    /// <param name="ItemKey">Case: the owning item key.</param>
    /// <param name="NodeKind">Node: <c>module</c>, <c>feature</c>, <c>category</c> or <c>sub_category</c>.</param>
    /// <param name="Title">Title of the node, item or case.</param>
    /// <param name="Description">Item or case description.</param>
    /// <param name="Precondition">Case precondition.</param>
    /// <param name="Steps">Case steps as an object (<c>{"steps":[…]}</c>).</param>
    /// <param name="Priority">Item or case priority.</param>
    /// <param name="State">Item or case state, or null for the creation default.</param>
    /// <param name="Code">Case code, or null/empty to let the allocator number it.</param>
    /// <param name="Position">Sibling position, or null to append.</param>
    internal sealed record FileRow(
        string Kind,
        string Key,
        string? ParentKey = null,
        string? ItemKey = null,
        string? NodeKind = null,
        string? Title = null,
        string? Description = null,
        string? Precondition = null,
        JsonNode? Steps = null,
        string? Priority = null,
        string? State = null,
        string? Code = null,
        int? Position = null)
    {
        /// <summary>A node row.</summary>
        /// <param name="key">The file key.</param>
        /// <param name="parentKey">The parent node key, or null for a module.</param>
        /// <param name="nodeKind">The node kind.</param>
        /// <param name="title">The title.</param>
        /// <param name="position">The explicit position, or null.</param>
        internal static FileRow Node(string key, string? parentKey, string nodeKind, string title, int? position = null) =>
            new("node", key, ParentKey: parentKey, NodeKind: nodeKind, Title: title, Position: position);

        /// <summary>An item row.</summary>
        /// <param name="key">The file key.</param>
        /// <param name="nodeKey">The owning node's key.</param>
        /// <param name="title">The title.</param>
        /// <param name="description">The description, or null.</param>
        /// <param name="priority">The priority.</param>
        /// <param name="state">The state, or null for the creation default.</param>
        /// <param name="position">The explicit position, or null.</param>
        internal static FileRow Item(
            string key,
            string nodeKey,
            string title,
            string? description = null,
            string priority = "medium",
            string? state = null,
            int? position = null) =>
            new("item", key, ParentKey: nodeKey, Title: title, Description: description, Priority: priority, State: state, Position: position);

        /// <summary>A case row whose steps default to one valid step.</summary>
        /// <param name="key">The file key.</param>
        /// <param name="itemKey">The owning item's key.</param>
        /// <param name="title">The title.</param>
        /// <param name="steps">The steps document, or null for one default step.</param>
        /// <param name="priority">The priority.</param>
        /// <param name="code">The code, or null/empty to let the allocator number it.</param>
        /// <param name="state">The state, or null for the creation default.</param>
        internal static FileRow Case(
            string key,
            string itemKey,
            string title,
            JsonNode? steps = null,
            string priority = "medium",
            string? code = null,
            string? state = null) =>
            new(
                "case",
                key,
                ItemKey: itemKey,
                Title: title,
                Steps: steps ?? Steps1("Open the screen", "The screen is shown"),
                Priority: priority,
                Code: code,
                State: state);

        /// <summary>The cells of the row in the contract's column order; an absent member is an empty cell.</summary>
        internal IReadOnlyList<string> Cells =>
        [
            Kind,
            Key,
            ParentKey ?? string.Empty,
            ItemKey ?? string.Empty,
            NodeKind ?? string.Empty,
            Title ?? string.Empty,
            Description ?? string.Empty,
            Precondition ?? string.Empty,
            Steps?.ToJsonString() ?? string.Empty,
            Priority ?? string.Empty,
            State ?? string.Empty,
            Code ?? string.Empty,
            Position?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        ];

        /// <summary>This row as one RFC 4180 record (quoted only when a cell needs it).</summary>
        internal string CsvLine => string.Join(',', Cells.Select(Quote));

        /// <summary>This row as the JSON document's <c>rows[]</c> entry, with <c>steps</c> as an object.</summary>
        internal JsonObject ToJsonRow()
        {
            // An absent optional member is omitted, never written as null: the document schema is closed
            // (additionalProperties: false) and every optional member is a string or an integer.
            var row = new JsonObject { ["kind"] = Kind, ["key"] = Key };

            Add("parent_key", ParentKey);
            Add("item_key", ItemKey);
            Add("node_kind", NodeKind);
            Add("title", Title);
            Add("description", Description);
            Add("precondition", Precondition);

            if (Steps is not null)
            {
                row["steps"] = Steps.DeepClone();
            }

            Add("priority", Priority);
            Add("state", State);
            Add("code", Code);

            if (Position is { } position)
            {
                row["position"] = position;
            }

            return row;

            void Add(string name, string? value)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    row[name] = value;
                }
            }
        }

        /// <summary>This row as the module's own <see cref="ImportRow"/>, which the XLSX writer takes.</summary>
        /// <param name="index">The 1-based data-row position.</param>
        internal ImportRow ToImportRow(int index) => new(
            index,
            KindOf(Kind),
            Key,
            ParentKey,
            ItemKey,
            NodeKind,
            Title,
            Description,
            Precondition,
            Steps?.ToJsonString(),
            Priority,
            State,
            Code,
            Position);

        /// <summary>The row kind as the module's enum; a kind the builders never produce is a test bug.</summary>
        /// <param name="kind"><c>node</c>, <c>item</c> or <c>case</c>.</param>
        private static ImportRowKind KindOf(string kind) => kind switch
        {
            "node" => ImportRowKind.Node,
            "item" => ImportRowKind.Item,
            "case" => ImportRowKind.Case,
            var other => throw new ArgumentOutOfRangeException(nameof(kind), other, "unknown row kind"),
        };

        private static string Quote(string cell) =>
            cell.Contains(',')
            || cell.Contains('"')
            || cell.Contains('\n')
            || cell.Contains('\r')
                ? "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
                : cell;
    }

    /// <summary>A one-step <c>authoring-steps.v1</c> document.</summary>
    /// <param name="action">The step action.</param>
    /// <param name="expected">The expected result.</param>
    internal static JsonNode Steps1(string action, string expected) => new JsonObject
    {
        ["steps"] = new JsonArray
        {
            new JsonObject { ["no"] = 1, ["action"] = action, ["expected"] = expected },
        },
    };

    /// <summary>A CSV file of <paramref name="rows"/> (header, LF line ends, no BOM — the import tolerates both).</summary>
    /// <param name="rows">The data rows in file order.</param>
    internal static string CsvBody(IEnumerable<FileRow> rows) =>
        Header + "\n" + string.Join("\n", rows.Select(row => row.CsvLine)) + "\n";

    /// <summary>A JSON import document (<c>{"format":"authoring-import","version":1,"rows":[…]}</c>).</summary>
    /// <param name="rows">The data rows in file order.</param>
    internal static byte[] JsonBody(IEnumerable<FileRow> rows)
    {
        var document = new JsonObject
        {
            ["format"] = "authoring-import",
            ["version"] = 1,
            ["rows"] = new JsonArray([.. rows.Select(row => (JsonNode)row.ToJsonRow())]),
        };

        return Encoding.UTF8.GetBytes(document.ToJsonString());
    }

    /// <summary>The body of one format.</summary>
    /// <param name="format"><c>csv</c>, <c>json</c> or <c>xlsx</c>.</param>
    /// <param name="rows">The data rows in file order.</param>
    internal static async Task<byte[]> BodyAsync(string format, IReadOnlyList<FileRow> rows) => format switch
    {
        "csv" => Encoding.UTF8.GetBytes(CsvBody(rows)),
        "json" => JsonBody(rows),
        "xlsx" => await XlsxBytesAsync(rows).ConfigureAwait(false),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "unknown import format"),
    };

    /// <summary>The body of one format, decoded as text when it is JSON or CSV (XLSX is binary).</summary>
    /// <param name="format"><c>csv</c> or <c>json</c>.</param>
    /// <param name="rows">The data rows in file order.</param>
    internal static string TextBody(string format, IEnumerable<FileRow> rows) => format switch
    {
        "csv" => CsvBody(rows),
        "json" => Encoding.UTF8.GetString(JsonBody(rows)),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "only a text format has a text body"),
    };

    /// <summary>
    /// One import request: the raw file as the body, <c>format</c> in the query string, <c>dry_run</c> only when asked,
    /// and an <c>Idempotency-Key</c> unless a test is proving its absence.
    /// </summary>
    /// <param name="client">The API client.</param>
    /// <param name="authorize">The principal and tenant headers.</param>
    /// <param name="project">The project.</param>
    /// <param name="format"><c>json</c>, <c>csv</c>, <c>xlsx</c> or a value the route must refuse.</param>
    /// <param name="body">The file bytes.</param>
    /// <param name="dryRun">True to send <c>dry_run=true</c>.</param>
    /// <param name="idempotencyKey">The key to reuse, or null for a fresh one.</param>
    /// <param name="withIdempotencyKey">False to send no key at all (the AC18 negative case).</param>
    internal static async Task<ImportAnswer> ImportAsync(
        HttpClient client,
        Action<HttpRequestHeaders> authorize,
        Guid project,
        string format,
        byte[] body,
        bool dryRun = false,
        string? idempotencyKey = null,
        bool withIdempotencyKey = true)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(authorize);
        ArgumentNullException.ThrowIfNull(body);

        using var request = new HttpRequestMessage(HttpMethod.Post, ImportUrl(project, format, dryRun));
        authorize(request.Headers);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeOf(format));

        if (withIdempotencyKey)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey ?? NewKey());
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        JsonElement? parsed = null;

        if (!string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith('{'))
        {
            using var document = JsonDocument.Parse(text);
            parsed = document.RootElement.Clone();
        }

        return new ImportAnswer(response.StatusCode, parsed, text, response.Headers);
    }

    /// <summary>One import request with a text body (CSV or JSON).</summary>
    /// <param name="client">The API client.</param>
    /// <param name="authorize">The principal and tenant headers.</param>
    /// <param name="project">The project.</param>
    /// <param name="format"><c>csv</c> or <c>json</c>.</param>
    /// <param name="body">The file text, encoded as UTF-8.</param>
    /// <param name="dryRun">True to send <c>dry_run=true</c>.</param>
    /// <param name="idempotencyKey">The key to reuse, or null for a fresh one.</param>
    /// <param name="withIdempotencyKey">False to send no key at all.</param>
    internal static Task<ImportAnswer> ImportAsync(
        HttpClient client,
        Action<HttpRequestHeaders> authorize,
        Guid project,
        string format,
        string body,
        bool dryRun = false,
        string? idempotencyKey = null,
        bool withIdempotencyKey = true) =>
        ImportAsync(client, authorize, project, format, Encoding.UTF8.GetBytes(body), dryRun, idempotencyKey, withIdempotencyKey);

    /// <summary>One export download (<c>GET …/authoring/export</c>).</summary>
    /// <param name="client">The API client.</param>
    /// <param name="authorize">The principal and tenant headers.</param>
    /// <param name="project">The project.</param>
    /// <param name="format"><c>csv</c>, <c>json</c> or <c>xlsx</c>.</param>
    /// <param name="nodeId">The subtree root, or null for the whole project.</param>
    /// <param name="include">A comma list of <c>nodes</c>, <c>items</c>, <c>cases</c>, or null for all three.</param>
    internal static async Task<ExportAnswer> ExportAsync(
        HttpClient client,
        Action<HttpRequestHeaders> authorize,
        Guid project,
        string format,
        Guid? nodeId = null,
        string? include = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(authorize);

        var query = new List<string> { "format=" + format };
        if (nodeId is { } node)
        {
            query.Add("node_id=" + node.ToString("D"));
        }

        if (include is not null)
        {
            query.Add("include=" + include);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/v1/projects/{project}/authoring/export?{string.Join('&', query)}");
        authorize(request.Headers);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

        return new ExportAnswer(response.StatusCode, bytes, response.Content.Headers.ContentType?.ToString());
    }

    /// <summary>The module's own XLSX writer, which builds a workbook a test can upload (hostile rows included).</summary>
    /// <param name="rows">The rows in file order; their index comes from <see cref="FileRow"/>'s position in the list.</param>
    internal static async Task<byte[]> XlsxBytesAsync(IReadOnlyList<FileRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        using var buffer = new MemoryStream();
        var importRows = rows.Select((row, position) => row.ToImportRow(position + 1)).ToList();
        await XlsxFormat.WriteAsync(importRows, buffer, TestContext.Current.CancellationToken).ConfigureAwait(false);

        return buffer.ToArray();
    }

    /// <summary>The module's own XLSX reader, which reads an exported workbook back into rows.</summary>
    /// <param name="bytes">The workbook bytes.</param>
    internal static ImportParse ReadXlsx(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        return XlsxFormat.Read(bytes, new ParsingService(), TestContext.Current.CancellationToken);
    }

    /// <summary>The raw text of a response body plus every response header, for a no-echo assertion.</summary>
    /// <param name="answer">The import answer.</param>
    internal static string EchoSurface(ImportAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        var headers = answer.Headers.SelectMany(header => header.Value.Select(value => header.Key + ": " + value));

        return answer.Text + "\n" + string.Join("\n", headers);
    }

    private static string ImportUrl(Guid project, string format, bool dryRun) =>
        $"/v1/projects/{project}/authoring/import?format={Uri.EscapeDataString(format)}" + (dryRun ? "&dry_run=true" : string.Empty);

    private static string MediaTypeOf(string format) => format switch
    {
        "csv" => CsvMediaType,
        "xlsx" => XlsxMediaType,
        _ => JsonMediaType,
    };

    private static string NewKey() => Guid.NewGuid().ToString("N");
}
