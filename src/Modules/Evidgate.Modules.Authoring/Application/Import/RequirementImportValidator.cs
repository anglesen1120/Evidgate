using System.Globalization;
using Evidgate.Modules.Authoring.Application.Import.Formats;
using Evidgate.Modules.Authoring.Domain;

namespace Evidgate.Modules.Authoring.Application.Import;

/// <summary>A requirement row once the file alone has vouched for it: every text is NFC and within its cap.</summary>
/// <param name="Source">The row as a reader produced it.</param>
/// <param name="Kind">One of <see cref="RequirementRules.Kinds"/>.</param>
/// <param name="Title">The trimmed NFC title, 1..200 code points.</param>
/// <param name="Body">The NFC body, at most 20 000 code points; empty when the row carried none.</param>
/// <param name="ExternalRef">The external reference label, or null when the row named none.</param>
/// <param name="FeatureNodeId">The feature node the row links to, or null.</param>
/// <param name="ParentKey">The key of the requirement row above this one, or null for a top-level requirement.</param>
internal sealed record RequirementImportRow(
    ImportRow Source,
    string Kind,
    string Title,
    string Body,
    string? ExternalRef,
    Guid? FeatureNodeId,
    string? ParentKey);

/// <summary>
/// Everything a requirement file's own bytes decide, with no database read: the one-family rule, the key graph, the
/// kind, the title, the body cap after NFC, the reference label and the columns a requirement row must leave empty.
/// </summary>
/// <remarks>Every issue is a fixed column pointer and a fixed reason; a cell value never reaches an issue.</remarks>
internal static class RequirementImportValidator
{
    /// <summary>
    /// A file is all tree rows or all requirement rows: the first row decides, and the first row of the other family is
    /// refused with its index so one fix makes the file whole.
    /// </summary>
    /// <param name="rows">The file's rows in order; at least one.</param>
    /// <returns>The refusal of the first offending row, or null when the file is of one family.</returns>
    internal static ImportIssue? MixedFamily(IReadOnlyList<ImportRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var requirements = rows[0].Kind == ImportRowKind.Requirement;
        foreach (var row in rows)
        {
            if ((row.Kind == ImportRowKind.Requirement) != requirements)
            {
                return new ImportIssue(row.Index, "/kind", "value_invalid");
            }
        }

        return null;
    }

    /// <summary>Validates every row of a requirement file; nothing is applied when any issue is returned.</summary>
    /// <param name="rows">The rows, all of kind requirement, in file order.</param>
    /// <returns>The accepted rows and the issues (at most <see cref="ImportSchema.MaxIssues"/>).</returns>
    internal static (IReadOnlyList<RequirementImportRow> Rows, List<ImportIssue> Issues) Validate(IReadOnlyList<ImportRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var issues = new List<ImportIssue>();
        var accepted = new List<RequirementImportRow>(rows.Count);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var codes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var before = issues.Count;
            CheckKeys(row, keys, codes, issues);
            var shaped = Shape(row, issues);
            if (issues.Count == before && shaped is not null)
            {
                accepted.Add(shaped);
            }
        }

        return (accepted, issues);
    }

    /// <summary>
    /// The key must be new to the file, the parent key must name a requirement row above this one, and a reference label
    /// may name one row only (two rows with one label would name one stored requirement twice).
    /// </summary>
    private static void CheckKeys(ImportRow row, HashSet<string> keys, HashSet<string> codes, List<ImportIssue> issues)
    {
        if (row.ParentKey is { } parent && !keys.Contains(parent))
        {
            ImportSchema.Add(issues, row.Index, "/parent_key", "unknown_parent");
        }

        if (!keys.Add(row.Key))
        {
            ImportSchema.Add(issues, row.Index, "/key", "duplicate_key");
        }

        if (row.Code is not null && !codes.Add(row.Code))
        {
            ImportSchema.Add(issues, row.Index, "/code", "duplicate_key");
        }
    }

    private static RequirementImportRow? Shape(ImportRow row, List<ImportIssue> issues)
    {
        var before = issues.Count;
        RejectTreeColumns(row, issues);

        if (!RequirementRules.IsKind(row.NodeKind))
        {
            ImportSchema.Add(issues, row.Index, "/node_kind", "value_invalid");
        }

        var title = Title(row, issues);
        var body = Body(row, issues);

        if (row.Code is not null && !RequirementRules.IsExternalRef(row.Code))
        {
            ImportSchema.Add(issues, row.Index, "/code", "value_invalid");
        }

        Guid? feature = null;
        if (row.ItemKey is not null)
        {
            if (Guid.TryParseExact(row.ItemKey, "D", out var parsed))
            {
                feature = parsed;
            }
            else
            {
                ImportSchema.Add(issues, row.Index, "/item_key", "value_invalid");
            }
        }

        return issues.Count == before
            ? new RequirementImportRow(row, row.NodeKind!, title, body, row.Code, feature, row.ParentKey)
            : null;
    }

    /// <summary>A requirement row has no precondition, steps, priority, state or position; a filled one is refused at its column.</summary>
    private static void RejectTreeColumns(ImportRow row, List<ImportIssue> issues)
    {
        (string? Value, string Path)[] forbidden =
        [
            (row.Precondition, "/precondition"),
            (row.StepsJson, "/steps"),
            (row.Priority, "/priority"),
            (row.State, "/state"),
            (row.Position?.ToString(CultureInfo.InvariantCulture), "/position"),
        ];

        foreach (var (value, path) in forbidden)
        {
            if (value is not null)
            {
                ImportSchema.Add(issues, row.Index, path, "value_invalid");
            }
        }
    }

    private static string Title(ImportRow row, List<ImportIssue> issues)
    {
        var title = ImportText.Normalise(row.Title, out var invalid)?.Trim() ?? string.Empty;
        if (invalid)
        {
            ImportSchema.Add(issues, row.Index, "/title", "invalid_text");
        }
        else if (title.Length == 0 || !AuthoringText.Fits(title, RequirementRules.MaximumTitleCodePoints))
        {
            ImportSchema.Add(issues, row.Index, "/title", "value_invalid");
        }

        return title;
    }

    /// <summary>The body is checked after NFC, so a decomposed text that composes to the cap is accepted and one cap over is not.</summary>
    private static string Body(ImportRow row, List<ImportIssue> issues)
    {
        var composed = ImportText.Normalise(row.Description, out var invalid) ?? string.Empty;
        if (invalid)
        {
            ImportSchema.Add(issues, row.Index, "/description", "invalid_text");

            return string.Empty;
        }

        if (!RequirementRules.TryNormalizeBody(composed, out var normalized))
        {
            ImportSchema.Add(issues, row.Index, "/description", "length");

            return string.Empty;
        }

        return normalized;
    }
}
