using System.Text.Json;
using Evidgate.BuildingBlocks.Contracts.Validation;

namespace Evidgate.Modules.Authoring.Application.Import.Formats;

/// <summary>
/// The one validator of the import/export table (13.1.1 §3 <c>authoring-import.v1.json</c>): the CSV and XLSX readers
/// build the document shape from their cells, the JSON reader <em>is</em> it, and every file is judged by the same
/// schema so a rule can never hold in one format and not in another (AC13/AC14).
/// </summary>
/// <remarks>
/// <para>
/// <b>The schema is the contract, this type adds no rule of its own</b> — the shape the <c>contracts/schemas</c> file
/// declares is what a reader enforces, exactly as <see cref="ManualCaseStepsValidator"/> does for the case
/// <c>steps</c> document.
/// </para>
/// <para>
/// <b>Why the schema set is compiled here.</b> The format readers are static (<c>CsvFormat.Read</c>,
/// <c>JsonFormat.Read</c>) because the route hands them a body and nothing else, so they have no constructor to receive
/// the module's <see cref="ContractSchemaSet"/> through. The set is therefore compiled once per process from the same
/// <c>contracts/</c> directory the hosts load (found above <see cref="AppContext.BaseDirectory"/>, the layout
/// <c>infra/docker/Dockerfile.api</c> ships and <c>ContractValidationServiceExtensions</c> locates), and a process that
/// cannot find it fails loudly on the first import instead of accepting a file no schema ever saw.
/// </para>
/// <para>
/// <b>Error details carry no caller text (S3).</b> One <see cref="ImportIssue"/> per offending row names the row index,
/// a fixed pointer (<c>/row</c>, a column such as <c>/title</c>, or <c>/steps</c>) and the AC13 reason — never a cell
/// value, a step text, a key or a member name it did not receive from the schema's own vocabulary: a row pointer is a
/// declared column (<see cref="ColumnNames"/>) or <c>/row</c>, so no hostile 4 096-character name can inflate a
/// <c>details[]</c> row.
/// </para>
/// </remarks>
internal static class ImportSchema
{
    /// <summary>
    /// How many <c>details[]</c> rows one refused file reports: the shared detail cap of the error envelope
    /// (<see cref="ContractValidationResult.MaxDetails"/>), so a 5 000-row file with the same mistake on every row
    /// cannot produce a 5 000-entry answer.
    /// </summary>
    internal const int MaxIssues = ContractValidationResult.MaxDetails;

    /// <summary>
    /// The deepest nesting a caller's import document may use. The row table itself is three levels deep
    /// (<c>rows</c> → row → <c>steps</c> → step), so 32 leaves room for any well-formed file and keeps untrusted input
    /// off the stack (00 §5.7 S5); a deeper document is refused as <c>unsupported_format</c>.
    /// </summary>
    internal const int MaxDocumentDepth = 32;

    /// <summary>The schema key of <c>contracts/schemas/authoring-import.v1.json</c>.</summary>
    internal const string SchemaKey = ContractSchemaKeys.AuthoringImport;

    /// <summary>
    /// The 13 columns of the CSV/XLSX header, in their contract order (13.1.1-T5, Q3). A row member of the JSON document
    /// carries the same name, which is why one list serves the header check, the CSV writer and the error pointer.
    /// </summary>
    internal static readonly string[] ColumnNames =
    [
        "kind",
        "key",
        "parent_key",
        "item_key",
        "node_kind",
        "title",
        "description",
        "precondition",
        "steps",
        "priority",
        "state",
        "code",
        "position",
    ];

    /// <summary>The same 13 names as a set, for the row-pointer decision of an error path.</summary>
    private static readonly HashSet<string> ColumnSet = new(ColumnNames, StringComparer.Ordinal);

    /// <summary>The compiled contract schemas, built on first use (a host that never imports pays nothing).</summary>
    private static readonly Lazy<ContractSchemaSet> LazySchemas =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The options every import document is parsed with: no comments, no trailing commas, a 32-level cap.</summary>
    /// <remarks>
    /// <c>AllowDuplicateProperties</c> is left at its default here on purpose: <see cref="Scan"/> already refuses a
    /// repeated member name with a reason of its own, so this parse never has to describe it.
    /// </remarks>
    internal static JsonDocumentOptions DocumentOptions { get; } = new()
    {
        MaxDepth = MaxDocumentDepth,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    /// <summary>The compiled schema set.</summary>
    internal static ContractSchemaSet Schemas => LazySchemas.Value;

    /// <summary>
    /// The structural and text scan of a JSON import body, run <em>before</em> the document is parsed or validated:
    /// JSON well-formedness and nesting, a repeated member name, and a member name or escaped string value that carries
    /// U+0000 or a lone surrogate.
    /// </summary>
    /// <remarks>
    /// These checks cannot be deferred to the schema. The schema engine sees a parsed document, so a member name that is
    /// not valid UTF-16 (<c>{"\ud800":1}</c>) would make either the engine or a <c>JsonElement</c> access throw an
    /// <see cref="InvalidOperationException"/> — the 500 that OI-13-116 closed for <c>AuthoringContent</c> — and a NUL is
    /// valid to both. The walk is one pass over the body with a <see cref="Utf8JsonReader"/>, and it decodes a string
    /// value only when it carries an escape (<see cref="Utf8JsonReader.ValueIsEscaped"/>), because an unescaped value of
    /// valid UTF-8 cannot hold a lone surrogate and a literal NUL byte is not well-formed JSON.
    /// </remarks>
    /// <param name="body">The body without its UTF-8 preamble.</param>
    /// <returns>Every refusal found, in document order and capped at <see cref="MaxIssues"/>.</returns>
    internal static IReadOnlyList<ImportIssue> Scan(ReadOnlySpan<byte> body)
    {
        var issues = new List<ImportIssue>();
        var reader = new Utf8JsonReader(
            body,
            new JsonReaderOptions
            {
                MaxDepth = MaxDocumentDepth,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });

        // The member-name chain of the current position. Array indexes are deliberately not part of it: the only index a
        // report names is the row, which is counted separately (see RowIndex) and never echoed with a member name.
        var segments = new List<string>(8);

        // One name set per property depth, reset when a new object starts at that depth: a duplicate is a repeat within
        // one object, not across two siblings.
        var members = new HashSet<string>?[MaxDocumentDepth + 2];

        var rowIndex = 0;
        var started = false;

        try
        {
            while (reader.Read())
            {
                started = true;

                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        if (segments.Count == 1 && string.Equals(segments[0], "rows", StringComparison.Ordinal))
                        {
                            rowIndex++;
                        }

                        if (reader.CurrentDepth + 1 < members.Length)
                        {
                            members[reader.CurrentDepth + 1] = null;
                        }

                        break;

                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        Truncate(segments, reader.CurrentDepth);
                        break;

                    case JsonTokenType.PropertyName:
                        {
                            var depth = reader.CurrentDepth;
                            Truncate(segments, depth - 1);

                            if (!TryReadString(ref reader, out var name) || !ImportText.IsStorable(name))
                            {
                                // The name cannot even be decoded (an unpaired surrogate escape) or is unstorable: report it
                                // without echoing it, because the name is the caller's own text.
                                Add(issues, rowIndex, NestedPointer(rowIndex, segments), "invalid_text");
                                break;
                            }

                            var seen = depth < members.Length ? members[depth] ??= new HashSet<string>(StringComparer.Ordinal) : null;
                            if (seen is not null && !seen.Add(name))
                            {
                                Add(issues, rowIndex, NestedPointer(rowIndex, segments), "value_invalid");
                            }

                            segments.Add(name);
                            break;
                        }

                    case JsonTokenType.String:
                        if (reader.ValueIsEscaped
                            && (!TryReadString(ref reader, out var value) || !ImportText.IsStorable(value)))
                        {
                            Add(issues, rowIndex, NestedPointer(rowIndex, segments), "invalid_text");
                        }

                        break;
                }

                if (issues.Count >= MaxIssues)
                {
                    break;
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON at all, deeper than MaxDocumentDepth, a comment, a trailing comma or two root values: the body is
            // not an import document of this format, and the reason names the format rather than a value.
            return [new ImportIssue(null, "/file", "unsupported_format")];
        }

        if (!started)
        {
            return [new ImportIssue(null, "/file", "unsupported_format")];
        }

        return issues;
    }

    /// <summary>
    /// Validates a candidate document (the JSON body itself, or the document a cell reader built) against
    /// <c>authoring-import.v1.json</c> and maps every violation to a row issue.
    /// </summary>
    /// <param name="document">The UTF-8 document.</param>
    /// <returns>Every refusal, capped at <see cref="MaxIssues"/>; empty means the document is acceptable.</returns>
    internal static IReadOnlyList<ImportIssue> Validate(ReadOnlyMemory<byte> document)
    {
        // A cell reader builds the document, so it is well-formed by construction; a caller that hands raw bytes still
        // gets a refusal rather than an exception (S5: untrusted input never throws).
        try
        {
            using var parsed = JsonDocument.Parse(document, DocumentOptions);
            return Validate(parsed.RootElement);
        }
        catch (JsonException)
        {
            return [new ImportIssue(null, "/file", "unsupported_format")];
        }
    }

    /// <summary>
    /// Validates a parsed document against <c>authoring-import.v1.json</c> and maps every violation to a row issue.
    /// </summary>
    /// <remarks>
    /// No second pass over the <c>steps</c> member is needed: the row branches are <c>allOf</c>/<c>if</c>/<c>then</c>,
    /// and the shared mapper drops only the <c>if</c> row and rows inside <em>reporting</em> composites
    /// (<c>anyOf</c>/<c>oneOf</c>/<c>not</c>/<c>contains</c>) — <c>allOf</c> and <c>then</c> merely wrap a child — so a
    /// steps violation arrives as a leaf whose instance path is <c>/rows/N/steps/…</c> and <see cref="FromError"/>
    /// reports it as <c>steps_invalid</c> at <c>/steps</c>. That is also why the branches are conditional rather than a
    /// three-way <c>oneOf</c>: a <c>oneOf</c> branch diagnostic never survives the mapper, and every row would collapse
    /// to <c>/row</c>.
    /// </remarks>
    /// <param name="document">The parsed document.</param>
    /// <returns>Every refusal, capped at <see cref="MaxIssues"/>.</returns>
    internal static IReadOnlyList<ImportIssue> Validate(JsonElement document) =>
        Map(Schemas.Validate(SchemaKey, document).Errors);

    /// <summary>
    /// Appends one issue unless the report is full, and turns a row index of <c>0</c> (the header) into a file-level
    /// entry so a header refusal carries no row number.
    /// </summary>
    /// <param name="issues">The report under construction.</param>
    /// <param name="rowIndex">The 1-based data row, or <c>0</c> for the header.</param>
    /// <param name="path">The fixed column pointer.</param>
    /// <param name="reason">The AC13 reason.</param>
    internal static void Add(List<ImportIssue> issues, int rowIndex, string path, string reason)
    {
        if (issues.Count < MaxIssues)
        {
            issues.Add(new ImportIssue(rowIndex <= 0 ? null : rowIndex, path, reason));
        }
    }

    /// <summary>
    /// The fixed column pointer for a member the raw scan met at <paramref name="segments"/>: only a declared column is
    /// named, a <c>steps</c> member is <c>/steps</c>, and anything else (a caller's own member name, a member inside an
    /// object that is not a row) is reported as <c>/row</c> or <c>/file</c> so no name is echoed (S3).
    /// </summary>
    /// <param name="rowIndex">The 1-based data row the scan is inside.</param>
    /// <param name="segments">The member path the scan has walked, starting at <c>rows</c>.</param>
    /// <returns>The pointer.</returns>
    internal static string NestedPointer(int rowIndex, List<string> segments)
    {
        if (rowIndex <= 0 || segments.Count == 0 || !string.Equals(segments[0], "rows", StringComparison.Ordinal))
        {
            return "/file";
        }

        if (segments.Count == 1)
        {
            return "/row";
        }

        if (string.Equals(segments[1], "steps", StringComparison.Ordinal))
        {
            return "/steps";
        }

        return segments.Count == 2 && ColumnSet.Contains(segments[1]) ? "/" + segments[1] : "/row";
    }

    /// <summary>Cuts the walked member path back to <paramref name="count"/> segments when the reader leaves a depth.</summary>
    /// <param name="segments">The walked path.</param>
    /// <param name="count">The depth to keep.</param>
    private static void Truncate(List<string> segments, int count)
    {
        var keep = Math.Max(count, 0);
        if (segments.Count > keep)
        {
            segments.RemoveRange(keep, segments.Count - keep);
        }
    }

    /// <summary>
    /// Reads the current string token, answering <see langword="false"/> instead of throwing when the bytes are not a
    /// readable string (an escaped lone surrogate): input is never trusted to be well-formed even after the scan.
    /// </summary>
    /// <param name="reader">The reader positioned on a string token.</param>
    /// <param name="value">The decoded text, or empty when it cannot be decoded.</param>
    /// <returns><see langword="true"/> when the token decoded.</returns>
    private static bool TryReadString(ref Utf8JsonReader reader, out string value)
    {
        try
        {
            value = reader.GetString() ?? string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            value = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Maps the schema's <c>(index, path, reason)</c> rows onto the AC13 vocabulary: a row path becomes that row's index
    /// plus a fixed pointer, <c>/steps</c> violations become <c>steps_invalid</c>, everything else at row level is
    /// <c>value_invalid</c>, and a document-level violation names the file (a wrong <c>format</c>/<c>version</c> is
    /// <c>unsupported_format</c>, any other document-level problem <c>value_invalid</c>).
    /// </summary>
    /// <param name="errors">The schema's errors.</param>
    /// <returns>The issues, capped at <see cref="MaxIssues"/>.</returns>
    private static List<ImportIssue> Map(IReadOnlyList<ContractValidationError> errors)
    {
        // Several leaves can name the same member of the same row (every step of a steps array that breaks one bound),
        // and a report carries one entry per offending member, not one per leaf.
        var seen = new HashSet<ImportIssue>();
        var issues = new List<ImportIssue>();
        foreach (var error in errors)
        {
            if (issues.Count >= MaxIssues)
            {
                break;
            }

            var issue = FromError(error);
            if (seen.Add(issue))
            {
                issues.Add(issue);
            }
        }

        return issues;
    }

    /// <summary>Maps one schema error to one issue.</summary>
    /// <param name="error">The schema error.</param>
    /// <returns>The issue.</returns>
    private static ImportIssue FromError(ContractValidationError error)
    {
        var segments = error.Path.Split('/');
        if (segments.Length >= 3
            && string.Equals(segments[0], string.Empty, StringComparison.Ordinal)
            && string.Equals(segments[1], "rows", StringComparison.Ordinal)
            && int.TryParse(segments[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index))
        {
            // Only a column the contract declares is echoed; an unknown member (a caller's own name) is reported at the
            // row, for the same reason a name the scan cannot read never reaches a report (S3).
            var relative = segments.Length <= 3
                ? "/row"
                : string.Equals(segments[3], "steps", StringComparison.Ordinal)
                    ? "/steps"
                    : ColumnSet.Contains(segments[3])
                        ? "/" + segments[3]
                        : "/row";

            return new ImportIssue(
                index + 1,
                relative,
                string.Equals(relative, "/steps", StringComparison.Ordinal) ? "steps_invalid" : "value_invalid");
        }

        // A document-level path is echoed only when the contract declares it (the discriminator, the version, the row
        // array); an unknown root member is a caller's own name and is reported at the file (S3).
        return error.Path switch
        {
            "/format" or "/version" => new ImportIssue(null, error.Path, "unsupported_format"),
            "/rows" => new ImportIssue(null, "/rows", "value_invalid"),
            _ => new ImportIssue(null, "/file", "value_invalid"),
        };
    }

    /// <summary>Compiles the contract schemas from the repository's <c>contracts/</c> directory.</summary>
    /// <returns>The compiled set.</returns>
    /// <exception cref="InvalidOperationException">No <c>contracts/schemas</c> directory was found above the binary.</exception>
    private static ContractSchemaSet Load() => ContractSchemaSet.Load(ContractsDirectory());

    /// <summary>
    /// The repository's <c>contracts/</c> directory: the nearest ancestor of the application base directory (then of the
    /// working directory) that holds <c>contracts/schemas</c> — the same rule
    /// <c>ContractValidationServiceExtensions.LocateContractsDirectory</c> applies for the hosts, so the deployed image
    /// (<c>COPY contracts/ ./contracts/</c>) and a test host resolve the same files.
    /// </summary>
    /// <returns>The directory holding <c>schemas/</c>.</returns>
    private static string ContractsDirectory()
    {
        var searched = new List<string>();
        foreach (var root in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var directory = new DirectoryInfo(Path.GetFullPath(root));
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "contracts");
                searched.Add(candidate);
                if (Directory.Exists(Path.Combine(candidate, "schemas")))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException(
            $"no 'contracts/schemas' directory found above the import reader: {string.Join(", ", searched)}");
    }
}
