using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Security;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;

namespace Evidgate.Modules.Evidence.Application.Freeze;

/// <summary>
/// <see cref="IEvidenceFreeze"/> over the caller's tenant transaction. It never opens a connection or a transaction,
/// never commits and never rolls back: the read, the conditional resolution and the canonical hash all run on the
/// ambient scope, so the caller's rollback also undoes the <c>attempt_id</c> resolution, and no network call is made.
/// <para>
/// One read statement answers every <c>ready</c> artifact of the run whose parse has decided, with the attempt its
/// <c>(execution_key, attempt_no)</c> resolves to; a report still awaiting its parse verdict is left out, so the caller
/// reports it as a missing requirement instead of freezing evidence a later rejection would contradict. The requirement
/// comparison, the orphan classification and the hash are decided entirely in memory over that result, and at most one
/// <c>UPDATE</c> persists the pairs that resolved for the first time — two statements whatever the number of artifacts
/// or requirements.
/// </para>
/// <para>
/// Both bounds fail closed instead of handing back a result the stored rows disagree with: a run above
/// <see cref="FreezeRequest.MaxReadyArtifacts"/> ready artifacts is refused before any write, and a resolution
/// <c>UPDATE</c> that touches fewer rows than it was sent (an artifact left <c>ready</c>, or a concurrent freeze won the
/// race) throws so the caller rolls its transaction back and retries.
/// </para>
/// </summary>
/// <param name="db">The Dapper path over the ambient scope; it demands an active scope before it touches the database.</param>
/// <param name="scopes">The tenant-scope accessor; <c>Require()</c> is the guard that throws before any statement.</param>
internal sealed class EvidenceFreeze(ITenantDb db, ITenantScopeAccessor scopes) : IEvidenceFreeze
{
    /// <summary><c>[a-z]</c>, the one-character class the reason code starts with.</summary>
    private static readonly SearchValues<char> LowerAlpha = SearchValues.Create("abcdefghijklmnopqrstuvwxyz");

    /// <summary><c>[a-z0-9_]</c>, the class of the reason code's later characters.</summary>
    private static readonly SearchValues<char> LowerSnake = SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789_");

    /// <summary>
    /// The <c>missing[]</c> code shape, <c>[a-z][a-z0-9_]{0,63}</c>, in one forward pass. No wall-clock setting: a slow
    /// host must never turn this refusal into an exception. A trailing newline is refused, as <c>\z</c> refuses it.
    /// </summary>
    internal static bool IsReasonCode(string? reasonCode) =>
        AsciiShape.IsMatch(reasonCode, 1, 64, LowerAlpha, LowerSnake);

    /// <inheritdoc />
    public async Task<FreezeResult> FreezeAsync(FreezeRequest request, CancellationToken cancellationToken)
    {
        Validate(request);

        // The tenant-scope guard runs before any statement: with no open scope this throws and nothing is sent. The
        // read and the write both name the scope's org and project explicitly, never a caller value.
        var scope = scopes.Require();

        // Statement 1: every ready artifact of the run with its resolved case_attempt, one statement however many
        // artifacts and requirements the call carries. A raw report whose parse has not decided is not one of them:
        // an unfinished artifact_parse task row or an undispatched parse outbox row keeps it out. An unknown or foreign
        // run simply answers no row. The limit is one past the cap, so the row that proves the run is too large is
        // still read.
        var rows = await db.QueryAsync<ReadyArtifactRow>(
            FreezeSql.SelectReadyArtifacts,
            new
            {
                org_id = scope.OrgId,
                project_id = scope.ProjectId,
                run_id = request.RunId,
                max_rows = FreezeRequest.MaxReadyArtifacts + 1,
            },
            cancellationToken).ConfigureAwait(false);

        // Fail closed before any write: a run above the cap is refused as a whole, so the read is never unbounded and
        // a partial revision can never be built from a truncated ready set.
        if (rows.Count > FreezeRequest.MaxReadyArtifacts)
        {
            throw new InvalidOperationException(
                $"run {request.RunId} has more than {FreezeRequest.MaxReadyArtifacts} ready artifacts, so its evidence "
                + "cannot be frozen in one revision; nothing was written.");
        }

        var pending = new List<ReadyArtifactRow>();
        var resolvedAttempts = new Dictionary<(string ExecutionKey, string Kind), HashSet<int>>();

        foreach (var row in rows)
        {
            if (row.ResolvedAttemptId is null)
            {
                // No case_attempt for the pair. The row stays unresolved — a stored attempt_id would be a
                // link to some other attempt, which nothing may silently reinterpret as this one.
                if (row.CurrentAttemptId is not null)
                {
                    throw new InvalidOperationException(
                        $"artifact {row.ArtifactId} of run {request.RunId} holds attempt_id {row.CurrentAttemptId}, but its execution "
                        + $"{row.ExecutionKey} attempt {row.AttemptNo} resolves to no case_attempt.");
                }

                continue;
            }

            if (row.CurrentAttemptId is { } stored && stored != row.ResolvedAttemptId)
            {
                // An already-resolved artifact may only ever name the attempt its pair resolves to; anything else is an
                // integrity violation and nothing is rewritten.
                throw new InvalidOperationException(
                    $"artifact {row.ArtifactId} of run {request.RunId} holds attempt_id {stored}, but its execution "
                    + $"{row.ExecutionKey} attempt {row.AttemptNo} resolves to case_attempt {row.ResolvedAttemptId}.");
            }

            if (row.CurrentAttemptId is null)
            {
                pending.Add(row);
            }

            if (!resolvedAttempts.TryGetValue((row.ExecutionKey, row.Kind), out var attempts))
            {
                attempts = [];
                resolvedAttempts.Add((row.ExecutionKey, row.Kind), attempts);
            }

            attempts.Add(row.AttemptNo);
        }

        if (pending.Count > 0)
        {
            // Statement 2: one bounded UPDATE for the whole batch, idempotent by its `attempt_id is null` guard, so a
            // second call over the same state issues no statement at all. The statement locks and writes the
            // batch in `a.id` order itself (FreezeSql.ResolveAttemptIds), so no in-memory ordering is needed here.
            var resolved = await db.ExecuteAsync(
                FreezeSql.ResolveAttemptIds,
                new
                {
                    org_id = scope.OrgId,
                    project_id = scope.ProjectId,
                    run_id = request.RunId,
                    rows = SerializeResolutions(pending),
                },
                cancellationToken).ConfigureAwait(false);

            // Every row the read sent must still match the guard at write time: a shorter count means an artifact left
            // `ready`, or a concurrent freeze resolved the same pair, between the read and the UPDATE. Reporting the
            // row as resolved would contradict what is stored, so the call fails closed and the caller's rollback
            // undoes its partial write.
            if (resolved != pending.Count)
            {
                throw new InvalidOperationException(
                    $"the freeze of run {request.RunId} changed {resolved} of {pending.Count} artifacts: an artifact left "
                    + "the 'ready' state or was resolved by a concurrent freeze between the read and the update; the "
                    + "caller must roll back and retry.");
            }
        }

        // Result order: (execution_key, attempt_no, kind, artifact id), ordinal, decided here so the
        // result never depends on DB collation or physical row order — the artifacts of one attempt are adjacent.
        var ordered = rows
            .OrderBy(row => row.ExecutionKey, StringComparer.Ordinal)
            .ThenBy(row => row.AttemptNo)
            .ThenBy(row => row.Kind, StringComparer.Ordinal)
            .ThenBy(row => IdText(row.ArtifactId), StringComparer.Ordinal)
            .ToList();

        var ready = new List<FrozenArtifact>(ordered.Count);
        var orphans = new List<OrphanArtifact>();
        // Gaps are the exception, not the shape of a call: a complete freeze reports none, so this list starts empty
        // instead of reserving one slot per ready artifact plus one per requirement.
        var missing = new List<MissingReason>();

        foreach (var row in ordered)
        {
            if (row.ResolvedAttemptId is { } attemptId)
            {
                ready.Add(new FrozenArtifact(
                    row.ArtifactId,
                    row.ExecutionKey,
                    row.AttemptNo,
                    attemptId,
                    row.Kind,
                    row.ObjectKey,
                    row.Checksum,
                    row.Sensitivity));
                continue;
            }

            // Never dropped and never resolved to a wrong attempt — the orphan is reported with its own
            // artifact_missing entry, distinct from any unmet requirement.
            orphans.Add(new OrphanArtifact(
                row.ArtifactId,
                row.ExecutionKey,
                row.AttemptNo,
                row.Kind,
                row.ObjectKey,
                row.Checksum,
                row.Sensitivity));
            missing.Add(new MissingReason(
                FreezeCodes.ArtifactMissing,
                MissingSource.Orphan,
                row.ExecutionKey,
                row.Kind,
                row.AttemptNo,
                row.ArtifactId));
        }

        missing.AddRange(RequirementGaps(request.Required, resolvedAttempts));

        // The canonical hash of the whole ready set (resolved and orphaned together), in the same order, over
        // (artifact_id, object_key, checksum) only — the resolved attempt is metadata about the artifact, not part of
        // what makes it, so resolving an orphan later never changes this digest.
        var hashRows = new HashRow[ordered.Count];
        for (var index = 0; index < ordered.Count; index++)
        {
            var row = ordered[index];
            hashRows[index] = new HashRow(row.ArtifactId, row.ObjectKey, row.Checksum);
        }

        // Serialized straight to UTF-8 and canonicalized from those bytes, so the digest costs no UTF-16 string and no
        // copy. JsonSerializerOptions.Default is the configuration the previous Serialize(string) call used, so the
        // digest of a given ready set is byte-identical to before (the tests recompute it independently).
        return new FreezeResult(
            ready,
            orphans,
            missing,
            CanonicalHash.Compute(JsonSerializer.SerializeToUtf8Bytes(hashRows, JsonSerializerOptions.Default)));
    }

    /// <summary>
    /// The unsatisfied requirements: one <see cref="MissingSource.Requirement"/> gap per distinct requirement,
    /// carrying the caller's own <see cref="RequiredArtifactSpec.ReasonCode"/>, so it never reads as the orphan's
    /// <see cref="FreezeCodes.ArtifactMissing"/>. A requirement is met only by a <b>resolved</b> <c>ready</c> artifact of
    /// the same execution and kind (and the same attempt when the spec names one), never by an orphan. An exact
    /// duplicate spec collapses to one gap; two specs that differ only in their reason code stay two gaps.
    /// </summary>
    /// <param name="required">The caller's requirement set, already validated.</param>
    /// <param name="resolvedAttempts">The attempt numbers of the resolved ready artifacts, per (execution key, kind).</param>
    /// <returns>The gaps ordered (execution_key, kind, attempt_no with null first, reason code), ordinal.</returns>
    private static List<MissingReason> RequirementGaps(
        IReadOnlyList<RequiredArtifactSpec> required,
        Dictionary<(string ExecutionKey, string Kind), HashSet<int>> resolvedAttempts)
    {
        var seen = new HashSet<(string ExecutionKey, string Kind, int? AttemptNo, string ReasonCode)>();
        var gaps = new List<MissingReason>();

        foreach (var spec in required)
        {
            if (!seen.Add((spec.ExecutionKey, spec.Kind, spec.AttemptNo, spec.ReasonCode)))
            {
                continue;
            }

            var satisfied = resolvedAttempts.TryGetValue((spec.ExecutionKey, spec.Kind), out var attempts)
                && (spec.AttemptNo is null ? attempts.Count > 0 : attempts.Contains(spec.AttemptNo.Value));
            if (satisfied)
            {
                continue;
            }

            gaps.Add(new MissingReason(
                spec.ReasonCode,
                MissingSource.Requirement,
                spec.ExecutionKey,
                spec.Kind,
                spec.AttemptNo,
                null));
        }

        // An attempt number starts at 1, so `?? 0` places an any-attempt gap before the per-attempt gaps of the same
        // requirement, exactly as the documented gap order reads.
        gaps.Sort(static (left, right) =>
        {
            var order = string.CompareOrdinal(left.ExecutionKey, right.ExecutionKey);
            if (order != 0)
            {
                return order;
            }

            order = string.CompareOrdinal(left.Kind, right.Kind);
            if (order != 0)
            {
                return order;
            }

            order = (left.AttemptNo ?? 0).CompareTo(right.AttemptNo ?? 0);
            return order != 0 ? order : string.CompareOrdinal(left.Code, right.Code);
        });

        return gaps;
    }

    /// <summary>
    /// The request-shape rules. A malformed request is a caller bug, never a data condition, so it is reported before
    /// the tenant-scope guard and before any statement: a request of the wrong shape must never depend on whether a
    /// scope happens to be open.
    /// </summary>
    /// <param name="request">The request to check.</param>
    /// <exception cref="ArgumentNullException">The request, its requirement list or an element is null.</exception>
    /// <exception cref="ArgumentException">The request is malformed.</exception>
    private static void Validate(FreezeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Required is null)
        {
            throw new ArgumentNullException(
                nameof(request),
                "request.Required is null; an empty list means nothing is required.");
        }

        if (request.RunId == Guid.Empty)
        {
            throw new ArgumentException("request.RunId is empty; a freeze names the run it freezes.", nameof(request));
        }

        if (request.Required.Count > FreezeRequest.MaxRequired)
        {
            throw new ArgumentException(
                $"a freeze request holds at most {FreezeRequest.MaxRequired} requirements, not {request.Required.Count}.",
                nameof(request));
        }

        for (var index = 0; index < request.Required.Count; index++)
        {
            var spec = request.Required[index];
            if (spec is null)
            {
                throw new ArgumentNullException(
                    nameof(request),
                    $"request.Required[{index}] is null; a requirement set holds no null element.");
            }

            if (!ArtifactShape.IsExecutionKey(spec.ExecutionKey))
            {
                throw new ArgumentException(
                    $"request.Required[{index}].ExecutionKey is not the sha256 hex shape (^[0-9a-f]{{64}}$), "
                    + "so no artifact row could ever carry it.",
                    nameof(request));
            }

            if (!ArtifactKind.IsKnown(spec.Kind))
            {
                throw new ArgumentException(
                    $"request.Required[{index}].Kind is outside the closed artifact-kind list.",
                    nameof(request));
            }

            if (!IsReasonCode(spec.ReasonCode))
            {
                throw new ArgumentException(
                    $"request.Required[{index}].ReasonCode is not the lower_snake shape (^[a-z][a-z0-9_]{{0,63}}$).",
                    nameof(request));
            }

            if (spec.AttemptNo is < 1)
            {
                throw new ArgumentException(
                    $"request.Required[{index}].AttemptNo is {spec.AttemptNo}; attempt numbers start at 1.",
                    nameof(request));
            }
        }
    }

    /// <summary>
    /// The one <c>jsonb</c> batch parameter of statement 2, in the recordset's column names. The order is the read's
    /// arbitrary order on purpose: statement 2 sorts and locks the batch by <c>a.id</c> itself
    /// (<c>FreezeSql.ResolveAttemptIds</c>), so no ordering cost is paid here.
    /// </summary>
    /// <param name="rows">The rows that resolved for the first time.</param>
    /// <returns>The batch as JSON text.</returns>
    private static string SerializeResolutions(List<ReadyArtifactRow> rows)
    {
        var batch = new ResolutionRow[rows.Count];
        for (var index = 0; index < rows.Count; index++)
        {
            // Every pending row has a resolved attempt: a null one never enters the batch.
            batch[index] = new ResolutionRow(rows[index].ArtifactId, rows[index].ResolvedAttemptId!.Value);
        }

        return JsonSerializer.Serialize(batch);
    }

    /// <summary>
    /// The artifact id as the lowercase <c>"D"</c> form the ordinal ordering compares. Lowercase hex is the same text
    /// <c>Guid.ToString("D")</c> always produces, independent of culture.
    /// </summary>
    /// <param name="artifactId">The artifact id.</param>
    /// <returns>The id in <c>"D"</c> form.</returns>
    private static string IdText(Guid artifactId) => artifactId.ToString("D");

    /// <summary>One row of statement 1 as Dapper maps it (snake_case → PascalCase, the tenancy type map).</summary>
    private sealed class ReadyArtifactRow
    {
        /// <summary>The artifact id (<c>artifact_id</c>).</summary>
        public Guid ArtifactId { get; set; }

        /// <summary>The planned execution's key (<c>execution_key</c>).</summary>
        public string ExecutionKey { get; set; } = string.Empty;

        /// <summary>The attempt number the artifact named at upload (<c>attempt_no</c>).</summary>
        public int AttemptNo { get; set; }

        /// <summary>The artifact kind (<c>kind</c>).</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>The server-chosen final object key (<c>object_key</c>).</summary>
        public string ObjectKey { get; set; } = string.Empty;

        /// <summary>The <c>sha256:&lt;hex&gt;</c> content address (<c>checksum</c>).</summary>
        public string Checksum { get; set; } = string.Empty;

        /// <summary>The stored sensitivity (<c>sensitivity</c>).</summary>
        public string Sensitivity { get; set; } = string.Empty;

        /// <summary>The attempt id the row already holds (<c>current_attempt_id</c>), or null when unresolved.</summary>
        public Guid? CurrentAttemptId { get; set; }

        /// <summary>The <c>case_attempt</c> id the pair resolves to (<c>resolved_attempt_id</c>), or null for an orphan.</summary>
        public Guid? ResolvedAttemptId { get; set; }
    }

    /// <summary>One resolution of statement 2 as the <c>jsonb_to_recordset</c> batch reads it (the wire names are the column names).</summary>
    /// <param name="Id">The artifact to resolve.</param>
    /// <param name="ResolvedAttemptId">The <c>case_attempt</c> id its pair resolves to.</param>
    private sealed record ResolutionRow(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("resolved_attempt_id")] Guid ResolvedAttemptId);

    /// <summary>
    /// One element of the canonical hash input: the artifact's identity and content address, never its resolved
    /// attempt, so the digest of a set of artifacts does not depend on when their pairs resolved.
    /// </summary>
    /// <param name="ArtifactId">The artifact id.</param>
    /// <param name="ObjectKey">The final object key.</param>
    /// <param name="Checksum">The <c>sha256:&lt;hex&gt;</c> content address.</param>
    private sealed record HashRow(
        [property: JsonPropertyName("artifact_id")] Guid ArtifactId,
        [property: JsonPropertyName("object_key")] string ObjectKey,
        [property: JsonPropertyName("checksum")] string Checksum);
}
