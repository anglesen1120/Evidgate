using Evidgate.Modules.Evidence.Contracts;

namespace Evidgate.Modules.Evidence.Domain;

/// <summary>
/// The closed list of <c>artifact.kind</c>: the CHECK constraint of migration 0028 and this type carry the same
/// vocabulary, so a kind the database would refuse never reaches a statement. The list itself is the contract's
/// <see cref="ArtifactKinds"/>, because callers outside this module resolve requirements against it too.
/// </summary>
internal static class ArtifactKind
{
    /// <summary>A Playwright trace archive.</summary>
    public const string Trace = ArtifactKinds.Trace;

    /// <summary>A recorded video of the attempt.</summary>
    public const string Video = ArtifactKinds.Video;

    /// <summary>A screenshot of the page or app under test.</summary>
    public const string Screenshot = ArtifactKinds.Screenshot;

    /// <summary>A raw runner report.</summary>
    public const string RawReport = ArtifactKinds.RawReport;

    /// <summary>A serialised DOM snapshot.</summary>
    public const string DomSnapshot = ArtifactKinds.DomSnapshot;

    /// <summary>An accessibility snapshot.</summary>
    public const string A11ySnapshot = ArtifactKinds.A11ySnapshot;

    /// <summary>A patch of the code under test.</summary>
    public const string Patch = ArtifactKinds.Patch;

    /// <summary>A UI hierarchy dump.</summary>
    public const string UiHierarchy = ArtifactKinds.UiHierarchy;

    /// <summary>The eight kinds of the contract, in contract order.</summary>
    public static readonly IReadOnlyList<string> All = ArtifactKinds.All;

    /// <summary>True when <paramref name="kind"/> is one of the eight contract kinds.</summary>
    public static bool IsKnown(string? kind) => ArtifactKinds.IsKnown(kind);

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

    /// <summary>
    /// The static per-kind fallback a minted artifact inherits when the project has no <c>artifact_kind_policy</c>
    /// row for its kind (06.1.1 §10 T2 Notes item 3, FR-06-5). The map is the one TL ruling T57(a) fixes
    /// (<c>plans/features/README.md</c> §3; 06.2.1 §5/§8): <c>trace</c>, <c>dom_snapshot</c>, <c>a11y_snapshot</c> and
    /// <c>patch</c> carry request bodies, cookies or page text and default to <see cref="Sensitive"/>;
    /// <c>video</c>, <c>screenshot</c>, <c>raw_report</c> and <c>ui_hierarchy</c> default to <see cref="Normal"/>.
    /// A project may lower a default-<c>sensitive</c> kind to <c>normal</c> only through the audited
    /// <c>PUT /v1/projects/{p}/artifact-kind-policy/{kind}</c> override (06.2.1-T1), never by omission.
    /// </summary>
    /// <param name="kind">One of <see cref="ArtifactKind.All"/>.</param>
    /// <returns>The kind's default sensitivity.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The kind is outside the 00 §7 closed list.</exception>
    public static string ForKind(string kind) =>
        ArtifactKind.Ensure(kind) switch
        {
            ArtifactKind.Trace or ArtifactKind.DomSnapshot or ArtifactKind.A11ySnapshot or ArtifactKind.Patch => Sensitive,
            _ => Normal,
        };
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

    /// <summary>The scan state, or an <see cref="ArgumentOutOfRangeException"/> when it is outside the closed list.</summary>
    /// <param name="value">The candidate scan state.</param>
    public static string Ensure(string value) =>
        IsKnown(value)
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
/// The reason codes 06.1.2's <c>complete</c> writes into <c>artifact.failure_code</c> when it refuses an upload
/// (06.1.2 §2 AC3/AC5, §5): the object never became evidence. The column's CHECK only bounds the length
/// (migration 0028: <c>char_length between 1 and 128</c>), so this type carries the vocabulary and is the SLI's
/// <c>failure_code</c> dimension (06.1.2 §8). Every code is also a committed 200, never an error response. The
/// <c>parse_*</c> codes are written later by the <c>artifact_parse</c> task (06.3.6 AC3), on an artifact that had
/// become <c>ready</c>: the same column, the same 1-128 bound, no new status. That task also writes
/// <see cref="TooLarge"/> there, when the stored object no longer fits the kind's cap, and — for a rejection the child
/// reported itself — one of the profile's reason words (<c>ParsingFailureReason</c>: <c>dtd_prohibited</c>,
/// <c>unsafe_entry_name</c>, <c>size_exceeded</c>, …), which is why the codes this type names are not the column's
/// whole vocabulary.
/// </summary>
internal static class ArtifactFailureCode
{
    /// <summary>The store reported a length other than the declared one (T18's signed <c>Content-Length</c> is the first line of defence).</summary>
    public const string SizeMismatch = "size_mismatch";

    /// <summary>The store's SHA-256 does not match the declared checksum, or it records none (not verified, fail closed).</summary>
    public const string ChecksumMismatch = "checksum_mismatch";

    /// <summary>The declared media type is not one the kind policy accepts.</summary>
    public const string MediaTypeMismatch = "media_type_mismatch";

    /// <summary>
    /// The object is larger than the kind policy's <c>max_bytes</c>. Written by <c>complete</c> when the stored object
    /// exceeds the declared cap (06.1.2-T1) and by the <c>artifact_parse</c> task when the capped read refuses to
    /// truncate it (06.3.6-T1.4): retrying cannot shrink the object, so that read is a verdict and no child runs.
    /// </summary>
    public const string TooLarge = "too_large";

    /// <summary>Neither the staging object nor a matching final object exists in the store (T86).</summary>
    public const string ObjectMissing = "object_missing";

    /// <summary>
    /// The masking scan found a secret and the project's kind policy rejects such an artifact (T57(b)): the row moves
    /// <c>ready → pending_delete</c>, so it stops being evidence and the deletion job removes its object.
    /// </summary>
    public const string SecretRejected = "secret_rejected";

    /// <summary>
    /// The parse child died of its heap limit (06.3.6 AC3). The value is the one
    /// <c>ParseRunRejection.Oom</c> maps a run to, so the vocabulary word exists once for the domain and once for the
    /// mapping and a test pins the two together; the <c>artifact_parse</c> task writes it on a <c>ready</c> artifact.
    /// </summary>
    public const string ParseOom = "parse_oom";

    /// <summary>The parse child exceeded its wall clock or its CPU limit (06.3.6 AC3, <c>ParseRunRejection.Timeout</c>).</summary>
    public const string ParseTimeout = "parse_timeout";

    /// <summary>
    /// Every other failed parse — a non-zero or signal exit, output over a cap, a stdout stream that is not the child's
    /// protocol (06.3.6 AC3, <c>ParseRunRejection.Crashed</c>) — and the code a child-reported rejection record earns
    /// when its reason is not a code word (a value outside <c>^[a-z0-9_]{1,64}$</c>, D-S125-Q4): no text the parsed
    /// document influenced may reach this column (00 §5.7 S3).
    /// </summary>
    public const string ParseCrashed = "parse_crashed";
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
        int version = 1,
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

    /// <summary>
    /// The <c>pending_upload|uploaded → ready</c> transition of 06.1.2 §6 (AC4), returned as a new row (C6: no
    /// property carries a setter): the length the store reported for the verified object, the masking scan
    /// awaiting its turn (T19) and no failure code. <c>object_key</c> is unchanged — the row already holds the
    /// final key (06.1.2-T1 decision 7), and it is the one column migration 0028 does not grant the app role.
    /// </summary>
    /// <param name="verifiedBytes">The stored length <c>HEAD</c> reported for the verified object.</param>
    public Artifact MarkReady(long? verifiedBytes) => Copy(
        Sensitivity,
        verifiedBytes,
        ArtifactScanState.Pending,
        ArtifactState.Ready,
        ExpiresAt,
        failureCode: null);

    /// <summary>
    /// The <c>pending_upload|uploaded → rejected</c> transition of 06.1.2 §6 (AC3): the object never became
    /// evidence, so the row keeps the size it declared and carries the reason. Terminal (00 §8.4); the 0028
    /// <c>artifact_failure_code_check</c> bounds the code's length, which is checked here so a value the database
    /// would refuse never reaches a statement (C6).
    /// <para>
    /// The <c>artifact_parse</c> task's verdict (06.3.6-T1.4) is the same transition from <c>ready</c>: an artifact
    /// whose bytes no longer parse cannot stay evidence, so it keeps its size, checksum, key and scan state and gains
    /// one reason — a <c>parse_*</c> code, <c>too_large</c>, or the child's own reason word. Reusing this member is
    /// deliberate — a second rejection shape would be a second place for the state's invariants to drift.
    /// </para>
    /// </summary>
    /// <param name="failureCode">One of the <see cref="ArtifactFailureCode"/> reasons.</param>
    public Artifact MarkRejected(string failureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(failureCode.Length, 128);

        return Copy(Sensitivity, Bytes, ScanState, ArtifactState.Rejected, ExpiresAt, failureCode);
    }

    /// <summary>
    /// The masking scan's <c>pending → clean|flagged</c> transition: a clean artifact keeps the sensitivity it was
    /// minted with, a flagged one is raised to <c>sensitive</c> so every read path gates it. Refuses to run twice, and
    /// refuses <c>pending</c> as a target, because the scan is the only writer of this column and a second write would
    /// mean a second scan of bytes that may already be gone.
    /// </summary>
    /// <param name="scanState">One of <see cref="ArtifactScanState.Clean"/> or <see cref="ArtifactScanState.Flagged"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside the closed list.</exception>
    /// <exception cref="InvalidOperationException">The row is not awaiting its scan.</exception>
    public Artifact MarkScanned(string scanState)
    {
        var next = ArtifactScanState.Ensure(scanState);

        if (string.Equals(next, ArtifactScanState.Pending, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("the masking scan cannot leave an artifact pending");
        }

        if (!string.Equals(ScanState, ArtifactScanState.Pending, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("the artifact is not awaiting its masking scan, so it is not scanned twice");
        }

        return Copy(
            string.Equals(next, ArtifactScanState.Flagged, StringComparison.Ordinal)
                ? ArtifactSensitivity.Sensitive
                : Sensitivity,
            Bytes,
            next,
            State,
            ExpiresAt,
            FailureCode);
    }

    /// <summary>
    /// The <c>ready → pending_delete</c> transition the retention sweep and the secret-scan rejection both take
    /// (00 §8.4, T57(b)): the artifact stops being evidence and the deletion job removes its object like any other
    /// pending deletion. Refuses every other source state, so a rejection can never be written as an illegal move.
    /// </summary>
    /// <param name="failureCode">The reason the artifact leaves evidence, e.g. <see cref="ArtifactFailureCode.SecretRejected"/>.</param>
    /// <exception cref="ArgumentException">The code is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The code is longer than the column's bound.</exception>
    /// <exception cref="InvalidOperationException">The row is not <c>ready</c>.</exception>
    public Artifact MarkPendingDelete(string failureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(failureCode.Length, 128);

        if (!string.Equals(State, ArtifactState.Ready, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("only a ready artifact moves to pending_delete");
        }

        return Copy(Sensitivity, Bytes, ScanState, ArtifactState.PendingDelete, ExpiresAt, failureCode);
    }

    /// <summary>
    /// The next row this aggregate's transition returns: every immutable member carried over, the six lifecycle values
    /// taken from the caller. One place builds a transitioned row, so a new lifecycle column cannot be forgotten in
    /// one transition and carried in another.
    /// </summary>
    /// <param name="sensitivity">The row's <c>sensitivity</c>.</param>
    /// <param name="bytes">The row's stored length, or <see langword="null"/> while unknown.</param>
    /// <param name="scanState">The row's <c>scan_state</c>; null lets the column default apply.</param>
    /// <param name="state">The row's <c>state</c>; null lets the column default apply.</param>
    /// <param name="expiresAt">The retention deadline, or <see langword="null"/> while unset.</param>
    /// <param name="failureCode">The reason the row carries, or <see langword="null"/> when it carries none.</param>
    private Artifact Copy(
        string sensitivity,
        long? bytes,
        string? scanState,
        string? state,
        DateTimeOffset? expiresAt,
        string? failureCode) => new(
        Id,
        OrgId,
        ProjectId,
        RunId,
        ExecutionKey,
        AttemptNo,
        Kind,
        ObjectKey,
        MediaType,
        Checksum,
        sensitivity,
        attemptId: AttemptId,
        bytes: bytes,
        scanState: scanState,
        state: state,
        expiresAt: expiresAt,
        failureCode: failureCode,
        version: Version,
        createdAt: CreatedAt,
        updatedAt: UpdatedAt);

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
