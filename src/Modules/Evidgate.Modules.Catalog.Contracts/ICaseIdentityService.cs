namespace Evidgate.Modules.Catalog.Contracts;

/// <summary>
/// Case identity registration (03.1.1), exported to the catalog sync of 03.4.1 and to the identity
/// mapping of plan 04 (00 §5.5: modules talk through <c>.Contracts</c> only).
/// </summary>
public interface ICaseIdentityService
{
    /// <summary>
    /// Registers what a sync found at <paramref name="commitSha"/>. <paramref name="files"/> is the
    /// <em>complete</em> set of test files and <c>evidgate.cases.yaml</c> sidecars of the project's tree at that
    /// commit: a case whose id is absent from it becomes <c>missing</c>. Must run inside the caller's tenant
    /// transaction (the 03.4.1 <c>catalog_sync</c> task). Per-file problems are reported, never thrown:
    /// a conflicting file writes nothing and the other files are still processed (AC2, AC4, AC5).
    /// </summary>
    /// <param name="projectId">The project whose tree this is; another tenant's id is a not-found failure.</param>
    /// <param name="commitSha">Lower-case hex commit id (7–64 characters).</param>
    /// <param name="files">The tree snapshot.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<CaseSyncOutcome> RegisterDiscoveredAsync(
        Guid projectId,
        string commitSha,
        IReadOnlyList<SourceFile> files,
        CancellationToken cancellationToken);
}

/// <summary>One file of a synced tree.</summary>
/// <param name="Path">Repository-relative path, forward slashes. A file named <c>evidgate.cases.yaml</c> is a sidecar.</param>
/// <param name="Content">The text of the file.</param>
/// <param name="Framework">Framework of a test file (ignored for sidecars).</param>
/// <param name="Language">Language of a test file, when known.</param>
public sealed record SourceFile(string Path, string Content, string Framework = "unknown", string? Language = null);

/// <summary>Per-file results of a registration (03.1.1 §5).</summary>
public static class CaseSyncResults
{
    /// <summary>A new case was created.</summary>
    public const string Registered = "registered";

    /// <summary>The case moved: its path changed and a rename row was written.</summary>
    public const string Renamed = "renamed";

    /// <summary>The case is already registered at this path.</summary>
    public const string Unchanged = "unchanged";

    /// <summary>Two or more files declare the same id: none of them writes anything.</summary>
    public const string DuplicateCaseId = "duplicate_case_id";

    /// <summary>The annotation and the sidecar (or two annotations) disagree: no silent pick.</summary>
    public const string IdSourceConflict = "id_source_conflict";

    /// <summary>A malformed annotation or sidecar entry: no case is created for it.</summary>
    public const string Warning = "warning";
}

/// <summary>Warning and conflict codes (03.1.1 AC2, AC4, AC5).</summary>
public static class CaseSyncCodes
{
    /// <summary>The <c>evidgate.id</c> text is not a UUID, or the file is unreadable.</summary>
    public const string InvalidAnnotation = "invalid_annotation";

    /// <summary>The sidecar, or one entry of it, is malformed or names a file that does not exist.</summary>
    public const string InvalidSidecarEntry = "invalid_sidecar_entry";

    /// <summary>Two files declare the same id in one commit.</summary>
    public const string DuplicateCaseId = "duplicate_case_id";

    /// <summary>Annotation and sidecar differ, or a file carries two annotations.</summary>
    public const string IdSourceConflict = "id_source_conflict";
}

/// <summary>The result for one path.</summary>
/// <param name="Path">The file (the sidecar itself for a malformed manifest).</param>
/// <param name="Result">One of <see cref="CaseSyncResults"/>.</param>
/// <param name="CaseId">The case, when one exists.</param>
/// <param name="Code">A <see cref="CaseSyncCodes"/> value for conflicts and warnings.</param>
/// <param name="Message">A human-readable reason for conflicts and warnings.</param>
public sealed record CaseSyncEntry(string Path, string Result, Guid? CaseId = null, string? Code = null, string? Message = null);

/// <summary>The sync report returned to 03.4.1 and stored on <c>catalog_sync.counts</c> (03.1.1-T2.2).</summary>
/// <param name="Entries">One row per file with something to say, in path order.</param>
/// <param name="Registered">New cases.</param>
/// <param name="Renamed">Moved cases.</param>
/// <param name="Unchanged">Cases already registered at their path.</param>
/// <param name="Conflicts">Files in <c>duplicate_case_id</c> or <c>id_source_conflict</c>.</param>
/// <param name="Warnings">Malformed annotations or sidecar entries.</param>
/// <param name="Missing">Cases newly marked <c>missing</c>.</param>
/// <param name="Reactivated">Cases that came back from <c>missing</c>.</param>
public sealed record CaseSyncReport(
    IReadOnlyList<CaseSyncEntry> Entries,
    int Registered,
    int Renamed,
    int Unchanged,
    int Conflicts,
    int Warnings,
    int Missing,
    int Reactivated);

/// <summary>Outcome of <see cref="ICaseIdentityService.RegisterDiscoveredAsync"/>: a report, or why it could not run.</summary>
/// <param name="Report">The report, null on failure.</param>
/// <param name="Failure">A short code (<c>project_not_found</c>), null on success.</param>
public sealed record CaseSyncOutcome(CaseSyncReport? Report, string? Failure)
{
    /// <summary>True when the registration ran.</summary>
    public bool Succeeded => Report is not null;
}
