using System.Globalization;

using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.Modules.Authoring.Application.Import.Formats;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;

namespace Evidgate.Modules.Authoring.Application.Import;

/// <summary>
/// Phase 1 of an import (13.1.1 AC13, route 25): the pure half of the row validation — everything that can be decided
/// from the file alone, with no database read. It refuses the whole file, never repairs a row, and reports one
/// <see cref="ImportIssue"/> per offending row whose <see cref="ImportIssue.Path"/> is always a fixed column pointer
/// (<c>/title</c>, <c>/parent_key</c>, …), never a cell value (S3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one parser for the content.</b> A row's title, description, precondition, steps, priority, state and code are
/// exactly the members of the entity's <c>content</c> snapshot (§4), so every value rule — NFC, U+0000, a lone surrogate,
/// the length bounds in code points, the closed priority/state sets, a well-formed code, the steps document — is the
/// writer's own and is applied through <see cref="AuthoringContent.Parse"/> instead of being restated here. What this
/// type adds is the import-only half: which column belongs to which kind, the file-local <c>key</c> graph, and the AC13
/// reason vocabulary (<c>duplicate_key</c>, <c>orphan_row</c>, <c>unknown_parent</c>, <c>code_conflict</c>,
/// <c>value_invalid</c>, <c>steps_invalid</c>, <c>invalid_text</c>).
/// </para>
/// <para>
/// <b>Order does not matter in the file.</b> A parent may follow its child: parents are resolved by <c>key</c>, and the
/// kind ladder (<see cref="FeatureNodeRules.CheckPlacement"/>) makes a cycle unreachable, so no ancestor walk runs.
/// </para>
/// <para>
/// <b>Never a 500.</b> Every string of a row is guarded before it reaches a normaliser or a JSON reader: text that is not
/// valid UTF-16 or carries U+0000 is <c>invalid_text</c> on that column (the OI-13-116 shape, one column further out),
/// and the steps document is parsed defensively, so a hostile file answers issues and nothing else.
/// </para>
/// </remarks>
internal sealed class ImportValidator(ManualCaseStepsValidator steps)
{
    /// <summary>The most issues one refusal reports; a longer list is truncated rather than echoed in full.</summary>
    internal const int MaximumIssues = 100;

    /// <summary>The longest accepted file-local <c>key</c>, in code points (the node title bound of §4).</summary>
    internal const int MaximumKeyLength = 200;

    /// <summary>
    /// The highest number a file may bring in an explicit <c>code</c>. The allocator's own bound is
    /// <see cref="AuthoringCode.MaximumNumber"/> (10⁹) and stays where it is, but an import row would raise the project's
    /// counter to it through <c>ReserveAtLeastAsync</c>, which commits in its own transaction: one row carrying
    /// <c>PFX-1000000000</c> would spend the project's whole numbering budget and answer <c>409 quota_exceeded</c> to
    /// every later create, with no way back short of SQL. A project's code space is therefore bounded here, at the
    /// six-digit range a person would ever type, so a write-only caller cannot exhaust it through the import route.
    /// </summary>
    internal const long MaximumExplicitCodeNumber = 999_999;

    /// <summary>
    /// Validates the whole row set against the file alone.
    /// </summary>
    /// <param name="rows">The rows a reader produced, in file order.</param>
    /// <param name="casePrefix">The project's stored <c>case_prefix</c>: a row's <c>code</c> must carry it (AC4).</param>
    /// <returns>The validated rows, or the issues that refuse the file.</returns>
    internal ImportValidation Validate(IReadOnlyList<ImportRow> rows, string casePrefix)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (!AuthoringCode.IsValidPrefix(casePrefix))
        {
            // The column CHECK would have refused it, so a value outside the grammar is a schema bug, not a file problem.
            throw new InvalidOperationException(
                "the stored case_prefix '" + casePrefix + "' is outside the grammar of 13.1.1 §4");
        }

        var issues = new List<ImportIssue>();
        var candidates = new List<ImportValidatedRow>(rows.Count);

        foreach (var row in rows)
        {
            if (Row(row, casePrefix, issues) is { } validated)
            {
                candidates.Add(validated);
            }
        }

        if (issues.Count == 0)
        {
            Keys(candidates, issues);
        }

        if (issues.Count == 0)
        {
            Codes(candidates, casePrefix, issues);
        }

        return issues.Count == 0
            ? ImportValidation.Accepted(candidates)
            : ImportValidation.Refused([.. issues]);
    }

    /// <summary>Validates one row's shape and content, answering the normalised row or null when it is refused.</summary>
    /// <param name="row">The row.</param>
    /// <param name="casePrefix">The project's <c>case_prefix</c>.</param>
    /// <param name="issues">The issue list of the file.</param>
    private ImportValidatedRow? Row(ImportRow row, string casePrefix, List<ImportIssue> issues)
    {
        var before = issues.Count;
        var kind = row.Kind;

        // The member-name guard of OI-13-116, applied to every string of the row: text that is not valid UTF-16 or holds
        // U+0000 cannot be composed, compared or stored, so it is refused here — on its own column — and never reaches a
        // normaliser that would throw.
        var text = Columns(row, issues);

        var key = text.Key;

        if (key is null)
        {
            Issue(issues, row.Index, "/key", "value_invalid");
        }
        else if (AuthoringText.CodePointLength(key) > MaximumKeyLength)
        {
            Issue(issues, row.Index, "/key", "value_invalid");
        }

        switch (kind)
        {
            case ImportRowKind.Node:
                NotApplicable(issues, row.Index, text.ItemKey, "/item_key");
                NotApplicable(issues, row.Index, text.Description, "/description");
                NotApplicable(issues, row.Index, text.Precondition, "/precondition");
                NotApplicable(issues, row.Index, text.StepsJson, "/steps");
                NotApplicable(issues, row.Index, text.Priority, "/priority");
                NotApplicable(issues, row.Index, text.State, "/state");
                NotApplicable(issues, row.Index, text.Code, "/code");
                break;

            case ImportRowKind.Item:
                NotApplicable(issues, row.Index, text.ItemKey, "/item_key");
                NotApplicable(issues, row.Index, text.NodeKind, "/node_kind");
                NotApplicable(issues, row.Index, text.Precondition, "/precondition");
                NotApplicable(issues, row.Index, text.StepsJson, "/steps");
                NotApplicable(issues, row.Index, text.Code, "/code");
                break;

            default:
                NotApplicable(issues, row.Index, text.ParentKey, "/parent_key");
                NotApplicable(issues, row.Index, text.NodeKind, "/node_kind");
                if (row.Position is not null)
                {
                    Issue(issues, row.Index, "/position", "value_invalid");
                }

                break;
        }

        if (row.Position is { } named && (named < 0 || named > AuthoringVersionWriter.MaximumSiblingPosition))
        {
            Issue(issues, row.Index, "/position", "value_invalid");
        }

        if (issues.Count != before)
        {
            return null;
        }

        // The content object of §4, built exactly as the routes build it; the ids are placeholders because a parent is
        // resolved by key against the database in phase 2, and the position is a placeholder because an omitted position
        // means "append" (phase 2 decides the number).
        var position = row.Position ?? 0;
        JsonObject? content = kind switch
        {
            ImportRowKind.Node => text.NodeKind is { } nodeKind
                ? AuthoringContent.FeatureNode(
                    nodeKind,
                    text.Title ?? string.Empty,
                    position,
                    text.ParentKey is null ? null : Guid.Empty)
                : null,
            ImportRowKind.Item => AuthoringContent.ChecklistItem(
                Guid.Empty,
                text.Title ?? string.Empty,
                text.Description,
                text.Priority ?? string.Empty,
                text.State ?? ChecklistItemStateMachine.NewItemState(AuthoringSource.Import),
                position),
            _ => StepsDocument(text.StepsJson) is { } stepsNode
                ? AuthoringContent.ManualTestCase(
                    Guid.Empty,
                    text.Code ?? PlaceholderCode(casePrefix),
                    text.Title ?? string.Empty,
                    text.Description,
                    text.Precondition,
                    stepsNode,
                    text.Priority ?? string.Empty,
                    text.State ?? ManualTestCaseStateMachine.NewCaseState)
                : null,
        };

        if (content is null)
        {
            Issue(
                issues,
                row.Index,
                kind switch
                {
                    ImportRowKind.Node => "/node_kind",
                    ImportRowKind.Case => "/steps",
                    _ => "/title",
                },
                kind == ImportRowKind.Case ? "steps_invalid" : "value_invalid");
            return null;
        }

        using var document = JsonDocument.Parse(content.ToJsonString());

        if (!AuthoringContent.Parse(EntityKindOf(kind), document.RootElement, out var snapshot, out var details))
        {
            foreach (var (path, reason) in details)
            {
                Map(issues, row.Index, path, reason);
            }

            return null;
        }

        if (snapshot is ManualTestCaseSnapshot manualCase)
        {
            using var stepsDocument = JsonDocument.Parse(manualCase.Steps);
            if (steps.ValidateDocument(stepsDocument.RootElement).Errors.Count > 0)
            {
                // One issue per row: the schema's own pointers below /steps would name a position inside the document,
                // and the S3 rule keeps a refusal down to the fixed column pointer plus the reason.
                Issue(issues, row.Index, "/steps", "steps_invalid");
                return null;
            }
        }

        return snapshot switch
        {
            FeatureNodeSnapshot node => new ImportValidatedRow(
                row,
                kind,
                key!,
                text.ParentKey,
                text.ItemKey,
                node.Kind,
                node.Title,
                null,
                null,
                null,
                string.Empty,
                string.Empty,
                null,
                row.Position,
                FeatureNodeKinds.Depth(node.Kind)),
            ChecklistItemSnapshot item => new ImportValidatedRow(
                row,
                kind,
                key!,
                text.ParentKey,
                text.ItemKey,
                null,
                item.Title,
                item.Description,
                null,
                null,
                item.Priority,
                item.State,
                null,
                row.Position,
                FeatureNodeKinds.MaximumDepth + 1,
                StateGiven: text.State is not null),
            ManualTestCaseSnapshot manual => new ImportValidatedRow(
                row,
                kind,
                key!,
                text.ParentKey,
                text.ItemKey,
                null,
                manual.Title,
                manual.Description,
                manual.Precondition,
                manual.Steps,
                manual.Priority,
                manual.State,
                text.Code is null ? null : manual.Code,
                row.Position,
                FeatureNodeKinds.MaximumDepth + 2,
                StateGiven: text.State is not null),
            _ => throw new InvalidOperationException("the parsed snapshot is not an authoring entity"),
        };
    }

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

/// <summary>One row that passed phase 1, with every text member composed and every rule of §4 applied.</summary>
/// <param name="Source">The row as the reader produced it.</param>
/// <param name="Kind">node, item or case.</param>
/// <param name="Key">The file-local key.</param>
/// <param name="ParentKey">A node's parent key, or an item's owning node key.</param>
/// <param name="ItemKey">A case's owning item key.</param>
/// <param name="NodeKind">A node row's kind.</param>
/// <param name="Title">The title, in NFC.</param>
/// <param name="Description">The description, in NFC.</param>
/// <param name="Precondition">A case's precondition, in NFC.</param>
/// <param name="StepsJson">A case's steps document, already normalised and schema-checked.</param>
/// <param name="Priority">The priority (empty for a node row).</param>
/// <param name="State">The state (the creation default when the row named none; empty for a node row).</param>
/// <param name="Code">The case code, or null when the row asked for one to be allocated.</param>
/// <param name="Position">The sibling position the row named, or null to append.</param>
/// <param name="Depth">The row's depth in the file, the order new rows are written in.</param>
/// <param name="StateGiven">
/// True when the row's <c>state</c> cell carried a value. An empty cell means "keep the stored state" on a matched row
/// and "the creation default" on a new one (13.1.1 §6, AC13), so the difference survives phase 1 and is decided by the
/// planner, which is the only phase that knows what is stored.
/// </param>
internal sealed record ImportValidatedRow(
    ImportRow Source,
    ImportRowKind Kind,
    string Key,
    string? ParentKey,
    string? ItemKey,
    string? NodeKind,
    string Title,
    string? Description,
    string? Precondition,
    string? StepsJson,
    string Priority,
    string State,
    string? Code,
    int? Position,
    int Depth,
    bool StateGiven = false);

/// <summary>The answer of phase 1: the validated rows, or the issues that refuse the whole file.</summary>
internal sealed class ImportValidation
{
    private ImportValidation(IReadOnlyList<ImportValidatedRow> rows, IReadOnlyList<ImportIssue> issues)
    {
        Rows = rows;
        Issues = issues;
    }

    /// <summary>The validated rows, in file order; empty when the file is refused.</summary>
    public IReadOnlyList<ImportValidatedRow> Rows { get; }

    /// <summary>The refusals of the file; empty when it is accepted.</summary>
    public IReadOnlyList<ImportIssue> Issues { get; }

    /// <summary>True when every row passed phase 1.</summary>
    public bool IsAccepted => Issues.Count == 0;

    /// <summary>An accepted file.</summary>
    /// <param name="rows">The validated rows, in file order.</param>
    internal static ImportValidation Accepted(IReadOnlyList<ImportValidatedRow> rows) => new(rows, []);

    /// <summary>A refused file; nothing is applied.</summary>
    /// <param name="issues">The issues that refuse it.</param>
    internal static ImportValidation Refused(IReadOnlyList<ImportIssue> issues) => new([], issues);
}
