using System.Text.Json;
using Evidgate.BuildingBlocks.Contracts.Validation;

namespace Evidgate.Modules.Authoring.Application.Import.Formats;

internal static partial class ImportSchema
{
    /// <summary>
    /// Refuses a <c>key</c>, <c>parent_key</c>, <c>item_key</c> or <c>code</c> that ends in a line feed, which the
    /// schema's <c>$</c> tolerates under the registry's engine.
    /// </summary>
    /// <param name="document">The document the schema accepted.</param>
    /// <returns>One <c>value_invalid</c> per anchored member that is not matched exactly.</returns>
    private static List<ImportIssue> ExactEnd(JsonElement document)
    {
        var issues = new List<ImportIssue>();

        if (!document.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return issues;
        }

        var index = 1;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind == JsonValueKind.Object)
            {
                foreach (var (member, pointer, pattern) in Anchored)
                {
                    if (row.TryGetProperty(member, out var value)
                        && value.ValueKind == JsonValueKind.String
                        && !pattern.IsMatch(value.GetString() ?? string.Empty))
                    {
                        Add(issues, index, pointer, "value_invalid");
                    }
                }
            }

            index++;
        }

        return issues;
    }

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
    /// Records the container the walk just entered: a container reached through a member name adds that name to the
    /// chain, an array element or the document root adds nothing.
    /// </summary>
    /// <param name="segments">The member-name chain of the containers currently open.</param>
    /// <param name="entered">One flag per reader depth: whether that container added a segment.</param>
    /// <param name="depth">The <see cref="Utf8JsonReader.CurrentDepth"/> of the start token.</param>
    /// <param name="member">The member name that introduced the container, or <c>null</c>.</param>
    /// <returns>Always <c>null</c>: the pending member name is consumed by its container.</returns>
    private static string? Enter(List<string> segments, bool[] entered, int depth, string? member)
    {
        var added = member is not null;

        if (depth < entered.Length)
        {
            entered[depth] = added;
        }

        if (added)
        {
            segments.Add(member!);
        }

        return null;
    }

    /// <summary>Leaves the container that closes at <paramref name="depth"/>, dropping the name it added.</summary>
    /// <param name="segments">The member-name chain of the containers currently open.</param>
    /// <param name="entered">One flag per reader depth: whether that container added a segment.</param>
    /// <param name="depth">The <see cref="Utf8JsonReader.CurrentDepth"/> of the end token.</param>
    private static void Leave(List<string> segments, bool[] entered, int depth)
    {
        if (depth >= entered.Length || !entered[depth])
        {
            return;
        }

        entered[depth] = false;

        if (segments.Count > 0)
        {
            segments.RemoveAt(segments.Count - 1);
        }
    }

    /// <summary>
    /// The fixed pointer for a member <em>name</em> the scan refuses (an unreadable, unstorable or repeated name): the
    /// object the name sits in — <c>/row</c> for a row object, <c>/steps</c> for a steps document, <c>/file</c> for the
    /// document or any other object. A caller's own name is never echoed (S3).
    /// </summary>
    /// <param name="segments">The member-name chain of the containers currently open.</param>
    /// <returns>The pointer.</returns>
    internal static string NamePointer(List<string> segments)
    {
        if (segments.Count == 0 || !string.Equals(segments[0], "rows", StringComparison.Ordinal))
        {
            return "/file";
        }

        if (segments.Count == 1)
        {
            return "/row";
        }

        return string.Equals(segments[1], "steps", StringComparison.Ordinal) ? "/steps" : "/row";
    }

    /// <summary>
    /// The fixed pointer for a member <em>value</em> the scan refuses: the declared column the value belongs to, or
    /// <c>/steps</c> inside a steps document, and <c>/row</c> or <c>/file</c> where no column name is known.
    /// </summary>
    /// <param name="segments">The member-name chain of the containers currently open.</param>
    /// <param name="member">The member whose value is being read, or <c>null</c>.</param>
    /// <returns>The pointer.</returns>
    internal static string ValuePointer(List<string> segments, string? member)
    {
        if (segments.Count == 0 || !string.Equals(segments[0], "rows", StringComparison.Ordinal))
        {
            return "/file";
        }

        if (segments.Count > 1)
        {
            return string.Equals(segments[1], "steps", StringComparison.Ordinal) ? "/steps" : "/row";
        }

        if (member is null)
        {
            return "/row";
        }

        if (string.Equals(member, "steps", StringComparison.Ordinal))
        {
            return "/steps";
        }

        return ColumnSet.Contains(member) ? "/" + member : "/row";
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
