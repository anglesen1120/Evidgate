using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Authoring.Application.Intake;
using Evidgate.Modules.Evidence.Contracts;

namespace Evidgate.Modules.Authoring.Infrastructure.Intake.Jobs;

/// <summary>What one claimed extraction needs: the intake's own facts, its persisted caps, the artifact and the lease.</summary>
/// <param name="IntakeId">The intake.</param>
/// <param name="Kind">The intake kind.</param>
/// <param name="MediaType">The declared media type of the intake.</param>
/// <param name="DeclaredBytes">The declared length of the intake.</param>
/// <param name="Caps">The caps persisted on the row, or null when the column cannot be read (the job fails closed).</param>
/// <param name="Artifact">The eligible artifact, or null when it is absent, not ready or not in the project.</param>
/// <param name="LeaseId">The lease that fences this attempt's terminal write.</param>
internal sealed record IntakeClaim(
    Guid IntakeId,
    string Kind,
    string MediaType,
    long DeclaredBytes,
    IntakeCaps? Caps,
    IntakeArtifact? Artifact,
    Guid LeaseId);

/// <summary>
/// The database side of the extraction job: claim (row and per-tenant slot), terminal write and slot release. Each call
/// is one short tenant transaction; none of them spans the object fetch or the parse.
/// </summary>
internal sealed class IntakeExtractionStore(
    ITenantScopeFactory scopes,
    ITenantDb db,
    IIntakeArtifactReader artifacts)
{
    /// <summary>How long a deferred claim waits before the queue offers it again.</summary>
    internal static readonly TimeSpan SlotWait = TimeSpan.FromSeconds(5);

    /// <summary>Added to the extraction timeout so a slow commit never loses the lease to its own deadline.</summary>
    private static readonly TimeSpan LeaseMargin = TimeSpan.FromSeconds(30);

    private const string ReadSql = """
        select kind,
               project_id as ProjectId,
               artifact_id as ArtifactId,
               media_type as MediaType,
               declared_bytes as DeclaredBytes,
               caps::text as Caps,
               state
          from public.authoring_intake
         where id = @id
           for update
        """;

    private const string DeleteExpiredSql =
        "delete from public.authoring_intake_slot where org_id = @org_id and expires_at <= now()";

    private const string HoldsSlotSql =
        "select slot from public.authoring_intake_slot where org_id = @org_id and intake_id = @id";

    // The unique (org_id, slot) key is the arbiter: a racing claim for the same number conflicts and gets no row, so
    // the limit holds without a lock and the loser is deferred rather than over-admitted.
    private const string TakeSlotSql = """
        insert into public.authoring_intake_slot (id, org_id, slot, intake_id, lease_id, expires_at)
        select @slot_id, @org_id, s, @id, @lease_id, now() + make_interval(secs => @lease_seconds)
          from generate_series(0, @max_slots - 1) as s
         where not exists (select 1 from public.authoring_intake_slot x where x.org_id = @org_id and x.slot = s)
         order by s
         limit 1
        on conflict (org_id, slot) do nothing
        returning slot
        """;

    private const string StartSql =
        "update public.authoring_intake set state = 'extracting' where id = @id and state = 'received'";

    private const string HoldsLeaseSql = """
        select slot
          from public.authoring_intake_slot
         where org_id = @org_id and intake_id = @id and lease_id = @lease_id
           for update
        """;

    private const string FinishSql = """
        update public.authoring_intake
           set state = @state,
               extracted_text = @text,
               extracted_text_hash = @hash,
               failure_code = @failure_code
         where id = @id
           and state = 'extracting'
        """;

    private const string ReleaseSql =
        "delete from public.authoring_intake_slot where org_id = @org_id and intake_id = @id and lease_id = @lease_id";

    private const string RenewSql = """
        update public.authoring_intake_slot
           set expires_at = now() + make_interval(secs => @lease_seconds)
         where org_id = @org_id and intake_id = @id and lease_id = @lease_id
        """;

    /// <summary>
    /// Takes the extraction: moves a received intake to extracting and holds one of the tenant's slots under a new
    /// lease. A slot whose lease expired is reclaimed first, which is how a crashed attempt is recovered.
    /// </summary>
    /// <param name="orgId">The tenant of the claimed task row.</param>
    /// <param name="intakeId">The intake.</param>
    /// <param name="cancellationToken">Cancels the claim.</param>
    /// <returns>The claim, or null when the intake is absent or already ready or rejected (nothing to do).</returns>
    /// <exception cref="TaskDeferredException">Every slot of the tenant is held by a live lease.</exception>
    internal async Task<IntakeClaim?> ClaimAsync(Guid orgId, Guid intakeId, CancellationToken cancellationToken)
    {
        await using var scope = await scopes
            .BeginAsync(orgId, projectId: null, readOnlyTransaction: false, cancellationToken)
            .ConfigureAwait(false);

        var row = await db
            .QuerySingleOrDefaultAsync<IntakeRow>(ReadSql, new { id = intakeId }, cancellationToken)
            .ConfigureAwait(false);

        if (row is null || row.State is "ready" or "rejected")
        {
            return null;
        }

        var caps = IntakeCapsJson.Deserialize(row.Caps);
        var effective = caps ?? IntakeCaps.Default;
        var lease = Guid.CreateVersion7();
        var seconds = (effective.ExtractionTimeout + LeaseMargin).TotalSeconds;

        await db.ExecuteAsync(DeleteExpiredSql, new { org_id = orgId }, cancellationToken).ConfigureAwait(false);

        var held = await db
            .QuerySingleOrDefaultAsync<int?>(HoldsSlotSql, new { org_id = orgId, id = intakeId }, cancellationToken)
            .ConfigureAwait(false);
        var taken = held is not null
            ? null
            : await db
                .QuerySingleOrDefaultAsync<int?>(
                    TakeSlotSql,
                    new
                    {
                        slot_id = Guid.CreateVersion7(),
                        org_id = orgId,
                        id = intakeId,
                        lease_id = lease,
                        lease_seconds = seconds,
                        max_slots = Math.Clamp(effective.MaxConcurrentExtractionsPerTenant, 1, 64),
                    },
                    cancellationToken)
                .ConfigureAwait(false);

        if (taken is null)
        {
            // Either another attempt of this very intake holds a live lease, or the tenant's slots are all taken:
            // the work waits, and the queue neither spends a retry nor dead-letters it.
            throw new TaskDeferredException(SlotWait);
        }

        await db.ExecuteAsync(StartSql, new { id = intakeId }, cancellationToken).ConfigureAwait(false);

        var artifact = row is { ArtifactId: { } artifactId } && caps is not null
            ? await artifacts.ReadEligibleAsync(orgId, row.ProjectId, artifactId, cancellationToken).ConfigureAwait(false)
            : null;

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new IntakeClaim(intakeId, row.Kind, row.MediaType, row.DeclaredBytes, caps, artifact, lease);
    }

    /// <summary>
    /// Writes the terminal state and frees the slot in one transaction, only while this attempt still holds its lease
    /// and the row is still extracting. A replay or a recovered attempt that lost the lease writes nothing.
    /// </summary>
    /// <param name="orgId">The tenant.</param>
    /// <param name="claim">The claim being finished.</param>
    /// <param name="outcome">The extraction result.</param>
    /// <param name="releaseSlot">False while the abandoned parser still runs: the slot then stays charged and its lease is renewed until the parser exits.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>True when this call moved the row to ready or rejected.</returns>
    internal async Task<bool> FinishAsync(
        Guid orgId,
        IntakeClaim claim,
        ExtractionOutcome outcome,
        bool releaseSlot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);

        await using var scope = await scopes
            .BeginAsync(orgId, projectId: null, readOnlyTransaction: false, cancellationToken)
            .ConfigureAwait(false);

        var holds = await db
            .QuerySingleOrDefaultAsync<int?>(
                HoldsLeaseSql,
                new { org_id = orgId, id = claim.IntakeId, lease_id = claim.LeaseId },
                cancellationToken)
            .ConfigureAwait(false);

        if (holds is null)
        {
            return false;
        }

        var applied = await db
            .ExecuteAsync(
                FinishSql,
                new
                {
                    id = claim.IntakeId,
                    state = outcome.Succeeded ? "ready" : "rejected",
                    text = outcome.Text,
                    hash = outcome.Text is null ? null : IntakeText.Hash(outcome.Text),
                    failure_code = outcome.FailureCode,
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (releaseSlot)
        {
            await db
                .ExecuteAsync(ReleaseSql, new { org_id = orgId, id = claim.IntakeId, lease_id = claim.LeaseId }, cancellationToken)
                .ConfigureAwait(false);
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);

        return applied == 1;
    }

    /// <summary>
    /// Frees the slot of an attempt that ended without a terminal write (cancelled, or failed in the database). The
    /// row stays extracting, so the next attempt recovers it; a lease that already expired is a no-op.
    /// </summary>
    /// <param name="orgId">The tenant.</param>
    /// <param name="claim">The claim being abandoned.</param>
    /// <param name="cancellationToken">A bounded token that is not the cancelled attempt's own.</param>
    internal async Task ReleaseAsync(Guid orgId, IntakeClaim claim, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);

        await using var scope = await scopes
            .BeginAsync(orgId, projectId: null, readOnlyTransaction: false, cancellationToken)
            .ConfigureAwait(false);

        await db
            .ExecuteAsync(ReleaseSql, new { org_id = orgId, id = claim.IntakeId, lease_id = claim.LeaseId }, cancellationToken)
            .ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Extends the lease of a slot whose parser is still running after its attempt gave up on it, so the tenant's
    /// capacity stays charged until the parser really exits. A lease that is gone is a no-op.
    /// </summary>
    /// <param name="orgId">The tenant.</param>
    /// <param name="claim">The claim whose parser is still running.</param>
    /// <param name="cancellationToken">A bounded token.</param>
    internal async Task RenewAsync(Guid orgId, IntakeClaim claim, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);

        await using var scope = await scopes
            .BeginAsync(orgId, projectId: null, readOnlyTransaction: false, cancellationToken)
            .ConfigureAwait(false);

        var seconds = ((claim.Caps ?? IntakeCaps.Default).ExtractionTimeout + LeaseMargin).TotalSeconds;
        await db
            .ExecuteAsync(
                RenewSql,
                new { org_id = orgId, id = claim.IntakeId, lease_id = claim.LeaseId, lease_seconds = seconds },
                cancellationToken)
            .ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record IntakeRow(
        string Kind,
        Guid ProjectId,
        Guid? ArtifactId,
        string MediaType,
        long DeclaredBytes,
        string Caps,
        string State);
}
