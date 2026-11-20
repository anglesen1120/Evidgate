using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;

namespace Evidgate.Modules.Authoring.Application.Import;

internal sealed partial class ImportPlanner
{
    /// <summary>Resolves every case row by its <c>code</c> (AC15), including the soft-deleted ones a restore brings back.</summary>
    /// <param name="orgId">The tenant.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="rows">The validated rows.</param>
    /// <param name="planned">The plan slots.</param>
    /// <param name="resolvedItems">The item id and state each file key resolved to.</param>
    /// <param name="issues">The refusals.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One past the highest number the file brought, or null when it brought none.</returns>
    private async ValueTask<long?> CasesAsync(
        Guid orgId,
        Guid projectId,
        IReadOnlyList<ImportValidatedRow> rows,
        ImportPlannedRow?[] planned,
        Dictionary<string, ResolvedItem> resolvedItems,
        List<ImportIssue> issues,
        CancellationToken cancellationToken)
    {
        // Returned rather than accumulated through a `ref`: an async method cannot take one (CS1988), and the value is
        // a plain result of the read this method performs.
        var explicitFloor = (long?)null;

        var order = rows
            .Select((row, slot) => (Row: row, Slot: slot))
            .Where(entry => entry.Row.Kind == ImportRowKind.Case)
            .ToArray();

        if (order.Length == 0)
        {
            return explicitFloor;
        }

        var codes = order
            .Where(entry => entry.Row.Code is not null)
            .Select(entry => CanonicalCode(entry.Row.Code!))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var storedByCode = new Dictionary<string, ImportSql.CaseRow>(StringComparer.Ordinal);

        if (codes.Length != 0)
        {
            var stored = await db.QueryAsync<ImportSql.CaseRow>(
                ImportSql.CasesByCode,
                new { org_id = orgId, project_id = projectId, codes = new ImportSql.TextArrayParameter(codes) },
                cancellationToken).ConfigureAwait(false);

            foreach (var row in stored)
            {
                storedByCode[row.Code] = row;
            }
        }

        foreach (var (row, slot) in order)
        {
            var itemId = resolvedItems[row.ItemKey!].Id;

            if (row.Code is null)
            {
                // A fresh number comes from the file's one allocation block, filled in by the importer before any write;
                // the empty code is a sentinel that the case CHECK would refuse, so a fill that ever failed loudly fails
                // here instead of storing a number nobody allocated.
                // Created with the file's state, for the round-trip reason recorded above the item insert (decision N1).
                var content = new ManualTestCaseSnapshot(
                    itemId,
                    string.Empty,
                    row.Title,
                    row.Description,
                    row.Precondition,
                    row.StepsJson!,
                    row.Priority,
                    row.State);
                planned[slot] = new ImportPlannedRow(row, ImportOutcome.Create, Guid.CreateVersion7(), content, null, null, 0);
                continue;
            }

            var code = CanonicalCode(row.Code);

            if (!storedByCode.TryGetValue(code, out var stored))
            {
                // An explicit, unknown code: created with that code, and the counter is raised past it so the next
                // allocation cannot collide with it (AC4).
                var parsed = AuthoringCode.TryParse(code, out var value) ? value : throw new InvalidOperationException(
                    "the validator accepted a code outside the grammar");
                explicitFloor = explicitFloor is { } floor ? Math.Max(floor, parsed.Number + 1) : parsed.Number + 1;

                var content = new ManualTestCaseSnapshot(
                    itemId,
                    code,
                    row.Title,
                    row.Description,
                    row.Precondition,
                    row.StepsJson!,
                    row.Priority,
                    row.State);
                planned[slot] = new ImportPlannedRow(row, ImportOutcome.Create, Guid.CreateVersion7(), content, null, code, 0);
                continue;
            }

            if (stored.ChecklistItemId != itemId)
            {
                // Route 19 is the one path that moves a case; an import that re-parented it would take the case out of the
                // item the file names without the owner ever asking.
                Issue(issues, row.Source.Index, "/item_key", "value_invalid");
                continue;
            }

            // A restore always comes back in §6's restore state (draft), whatever the file's state cell says: the writer's
            // restore forces it (AC21) and an import must not be the way around the review that a state carries.
            if (stored.DeletedAt is not null)
            {
                var restored = new ManualTestCaseSnapshot(
                    itemId,
                    stored.Code,
                    row.Title,
                    row.Description,
                    row.Precondition,
                    row.StepsJson!,
                    row.Priority,
                    ManualTestCaseStateMachine.RestoreState);

                planned[slot] = new ImportPlannedRow(
                    row,
                    ImportOutcome.Restore,
                    stored.Id,
                    restored,
                    Restored(stored),
                    stored.Code,
                    stored.Version,
                    stored.State);
                continue;
            }

            if (MatchedState(
                row,
                stored.State,
                (from, to) => ManualTestCaseStateMachine.Decide(from, to, null),
                issues) is not { } state)
            {
                continue;
            }

            var desired = new ManualTestCaseSnapshot(
                itemId,
                stored.Code,
                row.Title,
                row.Description,
                row.Precondition,
                row.StepsJson!,
                row.Priority,
                state);

            planned[slot] = new ImportPlannedRow(
                row,
                ImportOutcome.Unchanged,
                stored.Id,
                desired,
                null,
                stored.Code,
                stored.Version,
                stored.State);
        }

        return explicitFloor;
    }

    /// <summary>Compares every planned snapshot with the stored <c>content_hash</c> and fixes each row's outcome (AC15).</summary>
    /// <param name="orgId">The tenant.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="rows">One planned row per validated row, in file order.</param>
    /// <param name="resolvedItems">The item id and state each file key resolved to.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async ValueTask<PlanOutcome> DecideAsync(
        Guid orgId,
        Guid projectId,
        IReadOnlyList<ImportPlannedRow> rows,
        Dictionary<string, ResolvedItem> resolvedItems,
        CancellationToken cancellationToken)
    {
        var matched = rows.Where(row => row.StoredVersion != 0).ToArray();

        // One kind and one id per matched row, in the same order: the read pairs the two arrays positionally, so a
        // distinct-kind set zipped against a distinct-id set would ask for the wrong entities (Perf F1) and a re-import
        // of more than three matched rows would report every one of them as an update.
        var kinds = matched.Select(row => AuthoringWireValues.EntityKindName(KindOf(row.Row.Kind))).ToArray();
        var ids = matched.Select(row => row.EntityId).ToArray();

        var hashes = matched.Length == 0
            ? []
            : await db.QueryAsync<ImportSql.VersionHashRow>(
                ImportSql.LastContentHashes,
                new
                {
                    org_id = orgId,
                    project_id = projectId,
                    kinds = new ImportSql.TextArrayParameter(kinds),
                    ids,
                },
                cancellationToken).ConfigureAwait(false);

        var storedHashes = new Dictionary<(string Kind, Guid Id), string>();

        foreach (var hash in hashes)
        {
            storedHashes[(hash.EntityKind, hash.EntityId)] = hash.ContentHash;
        }

        var decided = new List<ImportPlannedRow>(rows.Count);
        var issues = new List<ImportIssue>();

        foreach (var row in rows)
        {
            if (row.StoredVersion == 0)
            {
                if (WritesUnderRetiredItem(row, resolvedItems, ImportOutcome.Create))
                {
                    RefuseRetiredItem(issues, row);
                    continue;
                }

                decided.Add(row);
                continue;
            }

            if (row.Content is null)
            {
                decided.Add(row);
                continue;
            }

            var kind = AuthoringWireValues.EntityKindName(KindOf(row.Row.Kind));
            var same = storedHashes.TryGetValue((kind, row.EntityId), out var storedHash)
                && string.Equals(storedHash, AuthoringHashes.ContentHash(row.Content), StringComparison.Ordinal);

            if (row.RestoreSnapshot is not null)
            {
                // A soft-deleted case comes back through one restore version row and one statement, which writes the
                // file's content and clears deleted_at. Never "unchanged": the row is still deleted. Folding the content
                // into the restore keeps one version row per entity per import — the entity's version is trigger-owned
                // and bumps once — and that is what makes the next identical import converge on `unchanged` (AC15).
                if (WritesUnderRetiredItem(row, resolvedItems, ImportOutcome.Restore))
                {
                    RefuseRetiredItem(issues, row);
                    continue;
                }

                decided.Add(row);
                continue;
            }

            if (!same && IsRetired(row))
            {
                // Every route that edits an item or a case goes through AuthoringStateRules.CheckEditable, which refuses
                // a terminal row; a set-based update has no such check of its own, so the planner applies it here.
                // Identical content stays `unchanged`, so importing a retired row back changes nothing.
                Issue(issues, row.Row.Source.Index, "/state", "value_invalid");
                continue;
            }

            var outcome = same ? ImportOutcome.Unchanged : ImportOutcome.Update;

            if (WritesUnderRetiredItem(row, resolvedItems, outcome))
            {
                RefuseRetiredItem(issues, row);
                continue;
            }

            decided.Add(same
                ? row with { Outcome = ImportOutcome.Unchanged, Content = null }
                : row with { Outcome = ImportOutcome.Update });
        }

        return new PlanOutcome(decided, issues);
    }
}
