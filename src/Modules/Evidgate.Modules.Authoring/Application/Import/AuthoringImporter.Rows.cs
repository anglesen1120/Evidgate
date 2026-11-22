using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;

namespace Evidgate.Modules.Authoring.Application.Import;

internal sealed partial class AuthoringImporter
{
    /// <summary>
    /// The comparison itself, kept pure so its three refusal directions — a live count that moved, a highest position that
    /// moved, a group that vanished entirely — are provable without standing up a concurrent writer (N2's stale-group
    /// branch).
    /// </summary>
    /// <param name="groups">What the plan read, per sibling group it adds to.</param>
    /// <param name="observed">What the live rows hold now, keyed by the table name and the group's parent id.</param>
    internal static bool ExpectationsHold(
        IReadOnlyList<ImportGroupExpectation> groups,
        IReadOnlyDictionary<(string Kind, Guid ParentId), (int LiveCount, int Maximum)> observed) =>
        groups.All(group => observed.TryGetValue(
                (Kind: group.Kind == ImportRowKind.Node ? "feature_node" : "checklist_item", group.ParentId),
                out var seen)
            && seen.LiveCount == group.LiveCount
            && seen.Maximum == group.Maximum);

    /// <summary>One set-based insert; returns the refusal when the row count does not match the plan.</summary>
    /// <param name="orgId">The tenant.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="statement">The insert statement of the kind.</param>
    /// <param name="source">The entity rows' <c>source</c>, or null for a kind without one (a feature node).</param>
    /// <param name="rows">The rows to write.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async ValueTask<Error?> InsertAsync<T>(
        Guid orgId,
        Guid projectId,
        string statement,
        string? source,
        IReadOnlyList<T> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        // The payload object carries `source` only for the two kinds whose table has one (a feature node has none), so no
        // statement is ever sent a parameter it does not reference.
        var payload = (object)JsonSerializer.Serialize(rows, PayloadJsonOptions);
        var written = source is null
            ? await db.ExecuteAsync(
                statement,
                new { org_id = orgId, project_id = projectId, rows = payload },
                cancellationToken).ConfigureAwait(false)
            : await db.ExecuteAsync(
                statement,
                new { org_id = orgId, project_id = projectId, source, rows = payload },
                cancellationToken).ConfigureAwait(false);

        return written == rows.Count ? null : OutOfStep();
    }

    /// <summary>One set-based update or restore; returns the refusal when the row count does not match the plan.</summary>
    /// <param name="orgId">The tenant.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="statement">The update statement.</param>
    /// <param name="rows">The rows to write.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async ValueTask<Error?> UpdateAsync<T>(
        Guid orgId,
        Guid projectId,
        string statement,
        IReadOnlyList<T> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        var written = await db.ExecuteAsync(
            statement,
            new
            {
                org_id = orgId,
                project_id = projectId,
                rows = JsonSerializer.Serialize(rows, PayloadJsonOptions),
            },
            cancellationToken).ConfigureAwait(false);

        return written == rows.Count ? null : OutOfStep();
    }

    /// <summary>
    /// Appends every version row of the import in <b>one</b> statement — the writer's own
    /// <see cref="AuthoringSql.AppendVersions"/> — with <c>source = import</c> and the shared <c>batch_id</c> (AC13).
    /// </summary>
    /// <param name="orgId">The tenant.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="batchId">The batch id of the import.</param>
    /// <param name="actorMembershipId">The authoring member, or null.</param>
    /// <param name="rows">The decided rows.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async ValueTask<Result<bool>> AppendVersionsAsync(
        Guid orgId,
        Guid projectId,
        Guid batchId,
        Guid? actorMembershipId,
        IReadOnlyList<ImportPlannedRow> rows,
        CancellationToken cancellationToken)
    {
        var source = AuthoringWireValues.ImportSource;
        var versions = new List<VersionRow>(rows.Count);

        foreach (var row in rows)
        {
            if (row.Outcome == ImportOutcome.Unchanged)
            {
                continue;
            }

            var kind = AuthoringWireValues.EntityKindName(KindOf(row.Row.Kind));

            if (row.Outcome == ImportOutcome.Restore)
            {
                // One row per entity per import: `version` is trigger-owned and bumps once, so a restore and an update of
                // the same row could not both be numbered against it. The restore row therefore records the file's
                // content, which is also the content the restore statement wrote.
                versions.Add(Version(row.EntityId, kind, row.StoredVersion + 1, AuthoringWireValues.RestoreChangeKind, row.Content!, source));
                continue;
            }

            versions.Add(row.Outcome == ImportOutcome.Create
                ? Version(row.EntityId, kind, 1, AuthoringWireValues.CreateChangeKind, row.Content!, source)
                : Version(row.EntityId, kind, row.StoredVersion + 1, AuthoringWireValues.UpdateChangeKind, row.Content!, source));
        }

        if (versions.Count == 0)
        {
            return Result.Success(true);
        }

        var written = await db.ExecuteAsync(
            AuthoringSql.AppendVersions,
            new
            {
                org_id = orgId,
                project_id = projectId,
                batch_id = batchId,
                author_membership_id = actorMembershipId,
                rows = JsonSerializer.Serialize(versions, PayloadJsonOptions),
            },
            cancellationToken).ConfigureAwait(false);

        return written == versions.Count
            ? Result.Success(true)
            : AuthoringErrors.Fail<bool>(OutOfStep());
    }

    /// <summary>One version row of the import's batch.</summary>
    /// <param name="entityId">The entity.</param>
    /// <param name="entityKind">The stored entity kind.</param>
    /// <param name="versionNo">The entity's <c>version</c> after this row.</param>
    /// <param name="changeKind">create, update or restore.</param>
    /// <param name="snapshot">The snapshot the row records.</param>
    /// <param name="source">Always <c>import</c>.</param>
    private static VersionRow Version(
        Guid entityId,
        string entityKind,
        int versionNo,
        string changeKind,
        AuthoringSnapshot snapshot,
        string source) => new(
            Guid.CreateVersion7(),
            entityKind,
            entityId,
            versionNo,
            changeKind,
            AuthoringContent.Serialize(snapshot),
            AuthoringHashes.ContentHash(snapshot),
            source,
            null,
            null);

    /// <summary>Builds the report of one import (AC13): one row per input row, in input order, and the totals.</summary>
    /// <param name="batchId">The batch id, or null on a dry run and for a file that changes nothing.</param>
    /// <param name="rows">The decided rows, in file order.</param>
    private static ImportReport Report(Guid? batchId, IReadOnlyList<ImportPlannedRow> rows) => new(
        batchId,
        new ImportCounts(
            rows.Count(row => row.Outcome == ImportOutcome.Create),
            rows.Count(row => row.Outcome == ImportOutcome.Update),
            rows.Count(row => row.Outcome == ImportOutcome.Restore),
            rows.Count(row => row.Outcome == ImportOutcome.Unchanged)),
        [.. rows.Select(row => new ImportRowReport(
            row.Row.Source.Index,
            row.Row.Key,
            row.Row.Kind,
            OutcomeName(row.Outcome),
            row.Code))]);

    /// <summary>The wire name of an outcome (13.1.1 §5 route 25 <c>rows[].outcome</c>).</summary>
    /// <param name="outcome">The outcome.</param>
    private static string OutcomeName(ImportOutcome outcome) => outcome switch
    {
        ImportOutcome.Create => "create",
        ImportOutcome.Update => "update",
        ImportOutcome.Restore => "restore",
        ImportOutcome.Unchanged => "unchanged",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "unknown import outcome"),
    };

    /// <summary>The refusal of a whole file: one <c>details[]</c> row per issue, never a cell value (AC13, S3).</summary>
    /// <param name="issues">The issues that refuse it.</param>
    internal static Result<ImportReport> Refusal(IReadOnlyList<ImportIssue> issues) =>
        AuthoringErrors.Fail<ImportReport>(Error.Invalid(
            "the import file is not valid; nothing was imported",
            [.. issues.Select(issue => new ErrorDetail(issue.Path, issue.Reason, issue.Index))]));

    /// <summary>The 413 of a file that carries more rows than one import may (AC13).</summary>
    private static Error TooLarge() => new(
        ApiErrorCode.PayloadTooLarge,
        "the import carries more than "
            + ImportLimits.MaxDataRows.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " data rows",
        413);

    /// <summary>The 409 of a batch whose stored state no longer matches the plan it was resolved from.</summary>
    private static Error OutOfStep() => AuthoringErrors.InvalidTransition(
        "the project changed while the import was resolved; nothing was imported");

    /// <summary>The entity kind of a row kind.</summary>
    /// <param name="kind">The row kind.</param>
    private static AuthoringEntityKind KindOf(ImportRowKind kind) => kind switch
    {
        ImportRowKind.Node => AuthoringEntityKind.FeatureNode,
        ImportRowKind.Item => AuthoringEntityKind.ChecklistItem,
        ImportRowKind.Case => AuthoringEntityKind.ManualTestCase,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown import row kind"),
    };

    /// <summary>The case-row parameters of a set-based case write (its update and its restore share them).</summary>
    /// <param name="row">An update or restore row.</param>
    private static CaseUpdateRow CaseUpdateRowOf(ImportPlannedRow row)
    {
        var snapshot = (ManualTestCaseSnapshot)row.Content!;

        return new CaseUpdateRow(
            row.EntityId,
            snapshot.ChecklistItemId,
            snapshot.Title,
            snapshot.Description,
            snapshot.Precondition,
            snapshot.Steps,
            snapshot.Priority,
            snapshot.State);
    }

    /// <summary>One row of the set-based feature node insert.</summary>
    /// <param name="Id">The node's id.</param>
    /// <param name="ParentId">The parent node, or null for a root module.</param>
    /// <param name="Kind">The node's kind.</param>
    /// <param name="Title">The title, in NFC.</param>
    /// <param name="Position">The resolved sibling position.</param>
    private sealed record NodeInsertRow(Guid Id, Guid? ParentId, string Kind, string Title, int Position);

    /// <summary>One row of the set-based checklist item insert.</summary>
    /// <param name="Id">The item's id.</param>
    /// <param name="FeatureNodeId">The owning node.</param>
    /// <param name="Title">The title, in NFC.</param>
    /// <param name="Description">The description, in NFC.</param>
    /// <param name="Priority">The priority.</param>
    /// <param name="State">The state.</param>
    /// <param name="Position">The resolved sibling position.</param>
    private sealed record ItemInsertRow(
        Guid Id,
        Guid FeatureNodeId,
        string Title,
        string? Description,
        string Priority,
        string State,
        int Position);

    /// <summary>One row of the set-based checklist item update.</summary>
    /// <param name="Id">The item's id.</param>
    /// <param name="Title">The title, in NFC.</param>
    /// <param name="Description">The description, in NFC.</param>
    /// <param name="Priority">The priority.</param>
    /// <param name="State">The state.</param>
    /// <param name="Position">The stored sibling position (an import never reorders).</param>
    private sealed record ItemUpdateRow(
        Guid Id,
        string Title,
        string? Description,
        string Priority,
        string State,
        int Position);

    /// <summary>One row of the set-based manual case insert.</summary>
    /// <param name="Id">The case's id.</param>
    /// <param name="ChecklistItemId">The owning item.</param>
    /// <param name="Code">The case code.</param>
    /// <param name="Title">The title, in NFC.</param>
    /// <param name="Description">The description, in NFC.</param>
    /// <param name="Precondition">The precondition, in NFC.</param>
    /// <param name="Steps">The steps document as JSON text.</param>
    /// <param name="Priority">The priority.</param>
    /// <param name="State">The state.</param>
    private sealed record CaseInsertRow(
        Guid Id,
        Guid ChecklistItemId,
        string Code,
        string Title,
        string? Description,
        string? Precondition,
        string Steps,
        string Priority,
        string State);

    /// <summary>One row of the set-based manual case update.</summary>
    /// <param name="Id">The case's id.</param>
    /// <param name="ChecklistItemId">The owning item (an import never moves a case).</param>
    /// <param name="Title">The title, in NFC.</param>
    /// <param name="Description">The description, in NFC.</param>
    /// <param name="Precondition">The precondition, in NFC.</param>
    /// <param name="Steps">The steps document as JSON text.</param>
    /// <param name="Priority">The priority.</param>
    /// <param name="State">The state.</param>
    private sealed record CaseUpdateRow(
        Guid Id,
        Guid ChecklistItemId,
        string Title,
        string? Description,
        string? Precondition,
        string Steps,
        string Priority,
        string State);

    /// <summary>One row of the batched <c>authoring_version</c> insert.</summary>
    /// <param name="Id">The version row's id.</param>
    /// <param name="EntityKind">The stored entity kind.</param>
    /// <param name="EntityId">The entity.</param>
    /// <param name="VersionNo">The entity's <c>version</c> after the change.</param>
    /// <param name="ChangeKind">create, update or restore.</param>
    /// <param name="Content">The snapshot as JSON text.</param>
    /// <param name="ContentHash">Its RFC 8785 digest.</param>
    /// <param name="Source">Always <c>import</c>.</param>
    /// <param name="Reason">Always null (the import route carries no reason).</param>
    /// <param name="AiRunId">Always null (an import is not an AI run).</param>
    private sealed record VersionRow(
        Guid Id,
        string EntityKind,
        Guid EntityId,
        int VersionNo,
        string ChangeKind,
        string Content,
        string ContentHash,
        string Source,
        string? Reason,
        Guid? AiRunId);
}
