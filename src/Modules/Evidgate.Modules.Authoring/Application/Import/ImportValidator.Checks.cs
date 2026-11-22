using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.Modules.Authoring.Application.Import.Formats;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;

namespace Evidgate.Modules.Authoring.Application.Import;

internal sealed partial class ImportValidator
{
    /// <summary>Resolves every file-local key and refuses a repeated identity, a missing parent or a broken ladder.</summary>
    /// <param name="rows">The parsed rows, in file order.</param>
    /// <param name="issues">The issue list of the file.</param>
    private static void Keys(List<ImportValidatedRow> rows, List<ImportIssue> issues)
    {
        var byKey = new Dictionary<string, ImportValidatedRow>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (!byKey.TryAdd(row.Key, row))
            {
                Issue(issues, row.Source.Index, "/key", "duplicate_key");
            }
        }

        if (issues.Count > 0)
        {
            return;
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            switch (row.Kind)
            {
                case ImportRowKind.Node:
                    {
                        ImportValidatedRow? parent = null;

                        if (row.ParentKey is { } parentKey)
                        {
                            if (!byKey.TryGetValue(parentKey, out parent))
                            {
                                Issue(issues, row.Source.Index, "/parent_key", "unknown_parent");
                                continue;
                            }

                            if (parent.Kind != ImportRowKind.Node)
                            {
                                Issue(issues, row.Source.Index, "/parent_key", "orphan_row");
                                continue;
                            }
                        }

                        // AC1's ladder, the same validator route 2 calls: a module stands at the root, every other kind
                        // needs its one parent kind, and nothing hangs under a sub_category.
                        var placement = FeatureNodeRules.CheckPlacement(
                            row.NodeKind!,
                            parent is null
                                ? null
                                : new FeatureNodeParent(Guid.Empty, parent.NodeKind!, Deleted: false, SameProject: true));

                        if (!placement.IsAccepted)
                        {
                            Issue(issues, row.Source.Index, "/parent_key", "unknown_parent");
                            continue;
                        }

                        Identity(
                            identities,
                            row,
                            (row.ParentKey ?? string.Empty) + "\u001f" + row.NodeKind + "\u001f" + row.Title,
                            issues);
                        break;
                    }

                case ImportRowKind.Item:
                    {
                        if (row.ParentKey is not { } ownerKey)
                        {
                            Issue(issues, row.Source.Index, "/parent_key", "orphan_row");
                            continue;
                        }

                        if (!byKey.TryGetValue(ownerKey, out var owner))
                        {
                            Issue(issues, row.Source.Index, "/parent_key", "unknown_parent");
                            continue;
                        }

                        if (owner.Kind != ImportRowKind.Node)
                        {
                            Issue(issues, row.Source.Index, "/parent_key", "orphan_row");
                            continue;
                        }

                        // AC3: a checklist item attaches to a category or a sub-category, never to a module or a feature.
                        if (!FeatureNodeKinds.ItemParentKinds.Contains(owner.NodeKind!, StringComparer.Ordinal))
                        {
                            Issue(issues, row.Source.Index, "/parent_key", "unknown_parent");
                            continue;
                        }

                        Identity(identities, row, ownerKey + "\u001f" + row.Title, issues);
                        break;
                    }

                default:
                    {
                        if (row.ItemKey is not { } itemKey)
                        {
                            Issue(issues, row.Source.Index, "/item_key", "orphan_row");
                            continue;
                        }

                        if (!byKey.TryGetValue(itemKey, out var item))
                        {
                            Issue(issues, row.Source.Index, "/item_key", "unknown_parent");
                            continue;
                        }

                        if (item.Kind != ImportRowKind.Item)
                        {
                            Issue(issues, row.Source.Index, "/item_key", "orphan_row");
                        }

                        break;
                    }
            }
        }
    }

    /// <summary>Refuses a <c>code</c> the grammar, the project's prefix or the file's own uniqueness rejects (AC4).</summary>
    /// <param name="rows">The parsed rows, in file order.</param>
    /// <param name="casePrefix">The project's stored <c>case_prefix</c>.</param>
    /// <param name="issues">The issue list of the file.</param>
    private static void Codes(List<ImportValidatedRow> rows, string casePrefix, List<ImportIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (row.Code is not { } code)
            {
                continue;
            }

            if (!AuthoringCode.TryParse(code, out var parsed)
                || !string.Equals(parsed.Prefix, casePrefix, StringComparison.Ordinal)
                || parsed.Number > MaximumExplicitCodeNumber
                || !seen.Add(parsed.Value))
            {
                // One reason for every code the project will not take: another prefix, the grammar, a number above the
                // import ceiling of the project's code space, or a code the file already used.
                Issue(issues, row.Source.Index, "/code", "code_conflict");
            }
        }
    }

    /// <summary>Refuses two rows of the file that describe one entity (the later row carries the issue).</summary>
    /// <param name="identities">The identities seen so far.</param>
    /// <param name="row">The row.</param>
    /// <param name="identity">The row's identity within the file.</param>
    /// <param name="issues">The issue list of the file.</param>
    private static void Identity(
        HashSet<string> identities,
        ImportValidatedRow row,
        string identity,
        List<ImportIssue> issues)
    {
        if (!identities.Add(row.Kind + "\u001f" + identity))
        {
            // Two rows with different keys but one identity would both resolve to the same entity in phase 2; the AC13
            // vocabulary has one reason for a repeated row identity, and this is it.
            Issue(issues, row.Source.Index, "/key", "duplicate_key");
        }
    }

    /// <summary>Reads, guards and NFC-composes every string column of one row.</summary>
    /// <param name="row">The row.</param>
    /// <param name="issues">The issue list of the file.</param>
    private static ImportTextColumns Columns(ImportRow row, List<ImportIssue> issues) => new(
        Column(row.Index, row.Key, "/key", issues),
        Column(row.Index, row.ParentKey, "/parent_key", issues),
        Column(row.Index, row.ItemKey, "/item_key", issues),
        Column(row.Index, row.NodeKind, "/node_kind", issues),
        Column(row.Index, row.Title, "/title", issues),
        Column(row.Index, row.Description, "/description", issues),
        Column(row.Index, row.Precondition, "/precondition", issues),
        Column(row.Index, row.StepsJson, "/steps", issues),
        Column(row.Index, row.Priority, "/priority", issues),
        Column(row.Index, row.State, "/state", issues),
        Column(row.Index, row.Code, "/code", issues));

    /// <summary>
    /// Guards and composes one column: <c>null</c> when empty, and the <c>invalid_text</c> issue when the text carries
    /// U+0000 or a lone surrogate. The guard itself is <see cref="ImportText.Normalise"/> — the one rule the three readers
    /// and the import share, so a CSV cell, an XLSX cell and a JSON string can never be composed or refused differently.
    /// </summary>
    /// <param name="index">The row's file position.</param>
    /// <param name="value">The cell.</param>
    /// <param name="path">The fixed column pointer.</param>
    /// <param name="issues">The issue list of the file.</param>
    private static string? Column(int index, string? value, string path, List<ImportIssue> issues)
    {
        var normalised = ImportText.Normalise(value, out var invalid);

        if (invalid)
        {
            Issue(issues, index, path, "invalid_text");
            return null;
        }

        return string.IsNullOrEmpty(normalised) ? null : normalised;
    }

    /// <summary>Reports a column that carries a value on a row kind it does not belong to.</summary>
    /// <param name="issues">The issue list of the file.</param>
    /// <param name="index">The row's file position.</param>
    /// <param name="value">The cell.</param>
    /// <param name="path">The fixed column pointer.</param>
    private static void NotApplicable(List<ImportIssue> issues, int index, string? value, string path)
    {
        if (value is not null)
        {
            Issue(issues, index, path, "value_invalid");
        }
    }

    /// <summary>Maps one <see cref="AuthoringContent"/> problem onto the AC13 reason and column vocabulary.</summary>
    /// <param name="issues">The issue list of the file.</param>
    /// <param name="index">The row's file position.</param>
    /// <param name="path">The problem path the content parser reported.</param>
    /// <param name="reason">The problem reason the content parser reported.</param>
    private static void Map(List<ImportIssue> issues, int index, string path, string reason)
    {
        var member = path.StartsWith("/content/", StringComparison.Ordinal)
            ? path["/content/".Length..]
            : path.TrimStart('/');

        switch (member)
        {
            case "steps":
                Issue(issues, index, "/steps", "steps_invalid");
                return;
            case "code":
                // AC4/AC13: a code outside the `<PREFIX>-<n>` grammar is the code_conflict reason, on the code column.
                Issue(issues, index, "/code", "code_conflict");
                return;
            case "kind":
                // The stored member is `kind`; the file's column is `node_kind`.
                Issue(issues, index, "/node_kind", "value_invalid");
                return;
            default:
                Issue(
                    issues,
                    index,
                    "/" + member,
                    string.Equals(reason, "invalid_text", StringComparison.Ordinal) ? "invalid_text" : "value_invalid");
                return;
        }
    }

    /// <summary>Counts one issue, up to <see cref="MaximumIssues"/>.</summary>
    /// <param name="issues">The issue list of the file.</param>
    /// <param name="index">The row's file position.</param>
    /// <param name="path">The fixed column pointer.</param>
    /// <param name="reason">One of the AC13 reasons.</param>
    private static void Issue(List<ImportIssue> issues, int index, string path, string reason)
    {
        if (issues.Count < MaximumIssues)
        {
            issues.Add(new ImportIssue(index, path, reason));
        }
    }

    /// <summary>The parsed steps document of a case row, or null when the cell is not a JSON document.</summary>
    /// <param name="steps">The cell's JSON text.</param>
    private static JsonNode? StepsDocument(string? steps)
    {
        if (steps is null)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(steps);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // The reader materialises member names and leaves; text that is not valid UTF-16 throws there. The column
            // guard already refused that row, so this branch only keeps the refusal a refusal.
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The code a case row without one is validated against: a well-formed code of the project's own prefix, never
    /// stored — phase 2 allocates the real number and the importer writes that. It exists so one parser validates every
    /// code rule for both halves of the file; the number is 1 because <see cref="AuthoringCode"/> refuses 0.
    /// </summary>
    /// <param name="casePrefix">The project's stored <c>case_prefix</c>.</param>
    private static string PlaceholderCode(string casePrefix) =>
        casePrefix + "-" + 1L.ToString("D" + AuthoringCode.MinimumDigits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>The entity kind of a row kind.</summary>
    /// <param name="kind">The row kind.</param>
    private static AuthoringEntityKind EntityKindOf(ImportRowKind kind) => kind switch
    {
        ImportRowKind.Node => AuthoringEntityKind.FeatureNode,
        ImportRowKind.Item => AuthoringEntityKind.ChecklistItem,
        ImportRowKind.Case => AuthoringEntityKind.ManualTestCase,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown import row kind"),
    };

    /// <summary>The string columns of one row, already guarded and composed.</summary>
    /// <param name="Key">The row's file-local key.</param>
    /// <param name="ParentKey">A node's parent key, or the key of an item's owning node.</param>
    /// <param name="ItemKey">A case's owning item key.</param>
    /// <param name="NodeKind">A node's kind.</param>
    /// <param name="Title">The title.</param>
    /// <param name="Description">The description.</param>
    /// <param name="Precondition">The case precondition.</param>
    /// <param name="StepsJson">The case steps document as JSON text.</param>
    /// <param name="Priority">The priority.</param>
    /// <param name="State">The state.</param>
    /// <param name="Code">The case code.</param>
    private sealed record ImportTextColumns(
        string? Key,
        string? ParentKey,
        string? ItemKey,
        string? NodeKind,
        string? Title,
        string? Description,
        string? Precondition,
        string? StepsJson,
        string? Priority,
        string? State,
        string? Code);
}
