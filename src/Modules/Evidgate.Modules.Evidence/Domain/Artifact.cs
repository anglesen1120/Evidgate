namespace Evidgate.Modules.Evidence.Domain;

/// <summary>
/// The closed list of <c>artifact.kind</c> (00 §7, 06.1.1 §4): the CHECK constraint of migration 0028 and this type
/// carry the same vocabulary, so a kind the database would refuse never reaches a statement (00 §5.5 C6).
/// </summary>
internal static class ArtifactKind
{
    /// <summary>A Playwright trace archive.</summary>
    public const string Trace = "trace";

    /// <summary>A recorded video of the attempt.</summary>
    public const string Video = "video";

    /// <summary>A screenshot of the page or app under test.</summary>
    public const string Screenshot = "screenshot";

    /// <summary>A raw runner report.</summary>
    public const string RawReport = "raw_report";

    /// <summary>A serialised DOM snapshot.</summary>
    public const string DomSnapshot = "dom_snapshot";

    /// <summary>An accessibility snapshot.</summary>
    public const string A11ySnapshot = "a11y_snapshot";

    /// <summary>A patch of the code under test.</summary>
    public const string Patch = "patch";

    /// <summary>A UI hierarchy dump.</summary>
    public const string UiHierarchy = "ui_hierarchy";

    /// <summary>The eight kinds of the 00 §7 contract, in contract order.</summary>
    public static readonly IReadOnlyList<string> All =
        [Trace, Video, Screenshot, RawReport, DomSnapshot, A11ySnapshot, Patch, UiHierarchy];

    /// <summary>True when <paramref name="kind"/> is one of the eight contract kinds.</summary>
    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);

    /// <summary>The kind, or an <see cref="ArgumentOutOfRangeException"/> when it is outside the closed list (C6).</summary>
    public static string Ensure(string kind) =>
        IsKnown(kind)
            ? kind
            : throw new ArgumentOutOfRangeException(nameof(kind), kind, "kind is outside the 00 §7 closed list (06.1.1 §4)");
}

/// <summary>The closed list of <c>artifact.sensitivity</c> and <c>artifact_kind_policy.default_sensitivity</c> (00 §7).</summary>
internal static class ArtifactSensitivity
{
    /// <summary>No masking is required before the artifact is served.</summary>
    public const string Normal = "normal";

    /// <summary>Carries personal data; reads are gated by 06.2.1.</summary>
    public const string Sensitive = "sensitive";

    /// <summary>The two sensitivities of the 00 §7 contract.</summary>
    public static readonly IReadOnlyList<string> All = [Normal, Sensitive];

    /// <summary>True when <paramref name="value"/> is one of the two contract sensitivities.</summary>
    public static bool IsKnown(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);

    /// <summary>The sensitivity, or an <see cref="ArgumentOutOfRangeException"/> when it is outside the closed list (C6).</summary>
    public static string Ensure(string value) =>
        IsKnown(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "sensitivity is outside the 00 §7 closed list (06.1.1 §4)");
}

/// <summary>The closed list of <c>artifact.scan_state</c> (00 §7, T19): the masking scan starts at <c>pending</c>.</summary>
internal static class ArtifactScanState
{
    /// <summary>Not scanned yet; the state a minted row starts in (T19).</summary>
    public const string Pending = "pending";

    /// <summary>Scanned and no secret was found.</summary>
    public const string Clean = "clean";

    /// <summary>Scanned and a secret was found; the read path gates it.</summary>
    public const string Flagged = "flagged";

    /// <summary>The three scan states of the 00 §7 contract.</summary>
    public static readonly IReadOnlyList<string> All = [Pending, Clean, Flagged];

    /// <summary>True when <paramref name="value"/> is one of the three contract scan states.</summary>
    public static bool IsKnown(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);

    /// <summary>
    /// The scan state, or an <see cref="ArgumentOutOfRangeException"/> when it is outside the closed list (C6).
    /// Null is allowed: the column carries the <c>pending</c> default and the database fills it (06.1.1 §6).
    /// </summary>
    public static string? EnsureIfSet(string? value) =>
        value is null || IsKnown(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "scan_state is outside the 00 §7 closed list (06.1.1 §4)");
}

/// <summary>The closed list of <c>artifact.state</c> (00 §7, §8.4): <c>pending_upload</c> is the only initial state.</summary>
internal static class ArtifactState
{
    /// <summary>Minted, not yet uploaded; the only state 06.1.1 creates (00 §8.4).</summary>
    public const string PendingUpload = "pending_upload";

    /// <summary>The object is in storage, not yet verified.</summary>
    public const string Uploaded = "uploaded";

    /// <summary>Verified and servable.</summary>
    public const string Ready = "ready";

    /// <summary>Refused by a scan or a size/type check; terminal.</summary>
    public const string Rejected = "rejected";

    /// <summary>Marked for deletion by the retention job (06.4.x).</summary>
    public const string PendingDelete = "pending_delete";

    /// <summary>The object is gone; terminal.</summary>
    public const string Deleted = "deleted";

    /// <summary>The six states of the 00 §8.4 state machine.</summary>
    public static readonly IReadOnlyList<string> All =
        [PendingUpload, Uploaded, Ready, Rejected, PendingDelete, Deleted];

    /// <summary>True when <paramref name="value"/> is one of the six contract states.</summary>
    public static bool IsKnown(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);

    /// <summary>
    /// The state, or an <see cref="ArgumentOutOfRangeException"/> when it is outside the closed list (C6).
    /// Null is allowed: the column carries the <c>pending_upload</c> default and the database fills it (06.1.1 §6).
    /// </summary>
    public static string? EnsureIfSet(string? value) =>
        value is null || IsKnown(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "state is outside the 00 §8.4 closed list (06.1.1 §4)");
}

/// <summary>
/// The write-model row of <c>public.artifact</c> (migration 0028, 06.1.1-T1): one stored evidence artifact whose
/// object key is minted only by the server. No property carries a <c>set</c> or <c>init</c> accessor (00 §5.5 C6),
/// so the write model cannot be mutated after construction; EF Core binds the row through this constructor and the
/// compiler-generated backing fields, and the closed lists of <c>kind</c>/<c>sensitivity</c>/<c>scan_state</c>/<c>state</c>
/// are enforced here as well as by the migration's CHECK constraints (C6).
/// </summary>
internal sealed class Artifact
{
    /// <summary>Materialises one artifact (EF Core constructor binding, 00 §5.5 C4).</summary>
    public Artifact(
        Guid id,
        Guid orgId,
        Guid projectId,
        Guid runId,
        string executionKey,
        int attemptNo,
        string kind,
        string objectKey,
        string mediaType,
        string checksum,
        string sensitivity,
        Guid? attemptId = null,
        long? bytes = null,
        string? scanState = null,
        string? state = null,
        DateTimeOffset? expiresAt = null,
        string? failureCode = null,
        int version = 0,
        DateTimeOffset createdAt = default,
        DateTimeOffset updatedAt = default)
    {
        Id = id;
        OrgId = orgId;
        ProjectId = projectId;
        RunId = runId;
        ExecutionKey = executionKey;
        AttemptNo = attemptNo;
        AttemptId = attemptId;
        Kind = ArtifactKind.Ensure(kind);
        ObjectKey = objectKey;
        MediaType = mediaType;
        Bytes = bytes;
        Checksum = checksum;
        Sensitivity = ArtifactSensitivity.Ensure(sensitivity);
        ScanState = ArtifactScanState.EnsureIfSet(scanState);
        State = ArtifactState.EnsureIfSet(state);
        ExpiresAt = expiresAt;
        FailureCode = failureCode;
        Version = version;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    /// <summary>UUIDv7 id (00 §7).</summary>
    public Guid Id { get; }

    /// <summary>RLS scope (00 §5.4).</summary>
    public Guid OrgId { get; }

    /// <summary>The project the artifact belongs to.</summary>
    public Guid ProjectId { get; }

    /// <summary>The run the artifact was captured in; no foreign key yet (05.2.1-T1 creates <c>run</c>).</summary>
    public Guid RunId { get; }

    /// <summary>The attempt's execution key, sha256 hex (00 §7).</summary>
    public string ExecutionKey { get; }

    /// <summary>The attempt number within the execution key.</summary>
    public int AttemptNo { get; }

    /// <summary>The resolved attempt, written at evidence freeze; null at mint (T17).</summary>
    public Guid? AttemptId { get; }

    /// <summary>One of <see cref="ArtifactKind.All"/> (00 §7).</summary>
    public string Kind { get; }

    /// <summary>The server-minted object key, unique per org (FR-06-1).</summary>
    public string ObjectKey { get; }

    /// <summary>The declared media type.</summary>
    public string MediaType { get; }

    /// <summary>The declared size in bytes, or null when unknown at mint.</summary>
    public long? Bytes { get; }

    /// <summary>The declared content address, <c>sha256:&lt;64 hex&gt;</c>.</summary>
    public string Checksum { get; }

    /// <summary>One of <see cref="ArtifactSensitivity.All"/> (00 §7).</summary>
    public string Sensitivity { get; }

    /// <summary>One of <see cref="ArtifactScanState.All"/>; null lets the <c>pending</c> default apply (T19).</summary>
    public string? ScanState { get; }

    /// <summary>One of <see cref="ArtifactState.All"/>; null lets the <c>pending_upload</c> default apply (00 §8.4).</summary>
    public string? State { get; }

    /// <summary>Retention deadline, read by the 06.4.x sweeper; null until set.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>The failure code of a rejected artifact; null otherwise.</summary>
    public string? FailureCode { get; }

    /// <summary>Optimistic-concurrency token (00 §7); the trigger owns the increment.</summary>
    public int Version { get; }

    /// <summary>Commit timestamp (00 §7).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Last write, UTC; the trigger owns it (00 §7).</summary>
    public DateTimeOffset UpdatedAt { get; }
}
