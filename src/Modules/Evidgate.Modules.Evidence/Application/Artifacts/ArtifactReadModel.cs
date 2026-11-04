using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Domain;

namespace Evidgate.Modules.Evidence.Application.Artifacts;

/// <summary>
/// The one read projection of an artifact row, shared by the metadata read, the run listing and the download
/// issuance: it resolves the effective sensitivity (the project's current kind-policy default, the row's own
/// stored value and the scan state) and masks the content-bearing columns for a caller without
/// <c>artifact:read_sensitive</c>. Keeping the rule in this single statement is what makes every content path
/// agree; a second copy of it in C# would be a second answer to the same question.
/// </summary>
internal static class ArtifactReadModel
{
    /// <summary>
    /// The shared column list. <c>effective_sensitivity</c> is <c>sensitive</c> when the project's policy now
    /// classifies the kind sensitive (raising a policy gates the rows that already exist), when the row was minted
    /// sensitive, or when the scan has not proved it clean (<c>pending</c> is not yet scanned, <c>flagged</c> found
    /// a secret, and an unknown state fails closed). Published or not, the policy can only ever raise: lowering it
    /// needs the audited override route and never rewrites a stored classification.
    /// </summary>
    private const string Projection = """
        select a.id,
               a.org_id,
               a.project_id,
               a.run_id,
               a.kind,
               a.media_type,
               a.state,
               a.sensitivity as stored_sensitivity,
               a.scan_state,
               a.created_at,
               case when a.scan_state <> 'clean'
                      or a.sensitivity = 'sensitive'
                      or kp.default_sensitivity = 'sensitive'
                    then 'sensitive' else 'normal' end as effective_sensitivity,
               case when (a.scan_state <> 'clean'
                          or a.sensitivity = 'sensitive'
                          or kp.default_sensitivity = 'sensitive')
                         and not @can_read_sensitive
                    then true else false end as content_gated,
               case when (a.scan_state <> 'clean'
                          or a.sensitivity = 'sensitive'
                          or kp.default_sensitivity = 'sensitive')
                         and not @can_read_sensitive
                    then null else a.object_key end as object_key,
               case when (a.scan_state <> 'clean'
                          or a.sensitivity = 'sensitive'
                          or kp.default_sensitivity = 'sensitive')
                         and not @can_read_sensitive
                    then null else a.checksum end as checksum,
               case when (a.scan_state <> 'clean'
                          or a.sensitivity = 'sensitive'
                          or kp.default_sensitivity = 'sensitive')
                         and not @can_read_sensitive
                    then null else a.bytes end as bytes
          from public.artifact a
          join public.project p
            on p.org_id = a.org_id
           and p.id = a.project_id
          left join public.artifact_kind_policy kp
            on kp.org_id = a.org_id
           and kp.project_id = a.project_id
           and kp.kind = a.kind
        """;

    /// <summary>One artifact by id, as the tenant scope sees it (RLS already hides another org's row).</summary>
    private const string ByIdSql = Projection + """

         where a.id = @artifact_id
        """;

    /// <summary>The artifacts of one run, newest last, for any future listing to reuse.</summary>
    private const string ByRunSql = Projection + """

         where a.run_id = @run_id
         order by a.created_at, a.id
        """;

    /// <summary>
    /// The project's current default sensitivity for <paramref name="kind"/>: the policy row when the project has
    /// one, the kind's static default otherwise. The resolver every mint and every read resolves through, so a
    /// project override never applies to one path and not the other.
    /// </summary>
    private const string DefaultSensitivitySql = """
        select kp.default_sensitivity
          from public.project p
          left join public.artifact_kind_policy kp
            on kp.org_id = p.org_id
           and kp.project_id = p.id
           and kp.kind = @kind
         where p.org_id = @org_id
           and p.id = @project_id
           and p.status <> 'deleted'
        """;

    /// <summary>One artifact's row, or <c>null</c> when the scope cannot see it (unknown, another tenant, another project).</summary>
    /// <param name="db">The scoped data access of an open tenant scope.</param>
    /// <param name="artifactId">The addressed artifact.</param>
    /// <param name="canReadSensitive">Whether the caller holds <c>artifact:read_sensitive</c>.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    internal static async Task<ArtifactReadRow?> ReadAsync(
        ITenantDb db,
        Guid artifactId,
        bool canReadSensitive,
        CancellationToken cancellationToken) =>
        await db.QuerySingleOrDefaultAsync<ArtifactReadRow>(
            ByIdSql,
            new { artifact_id = artifactId, can_read_sensitive = canReadSensitive },
            cancellationToken).ConfigureAwait(false);

    /// <summary>Every artifact of one run the scope can see, with the same gating as <see cref="ReadAsync"/>.</summary>
    /// <param name="db">The scoped data access of an open tenant scope.</param>
    /// <param name="runId">The run whose artifacts are listed.</param>
    /// <param name="canReadSensitive">Whether the caller holds <c>artifact:read_sensitive</c>.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    internal static async Task<IReadOnlyList<ArtifactReadRow>> ReadRunAsync(
        ITenantDb db,
        Guid runId,
        bool canReadSensitive,
        CancellationToken cancellationToken) =>
        await db.QueryAsync<ArtifactReadRow>(
            ByRunSql,
            new { run_id = runId, can_read_sensitive = canReadSensitive },
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The effective default sensitivity of <paramref name="kind"/> in the project: the stored override when there
    /// is one, else the kind's static default. A kind with neither answers its static default, which is the
    /// fail-closed map of the contract for the four kinds that have one.
    /// </summary>
    /// <param name="db">The scoped data access of an open tenant scope.</param>
    /// <param name="orgId">The scope's organization.</param>
    /// <param name="projectId">The project whose override applies.</param>
    /// <param name="kind">One of the eight contract kinds.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    internal static async Task<string> ResolveDefaultSensitivityAsync(
        ITenantDb db,
        Guid orgId,
        Guid projectId,
        string kind,
        CancellationToken cancellationToken)
    {
        var stored = await db.QuerySingleOrDefaultAsync<string>(
            DefaultSensitivitySql,
            new { org_id = orgId, project_id = projectId, kind },
            cancellationToken).ConfigureAwait(false);

        return stored ?? ArtifactSensitivity.ForKind(kind);
    }
}

/// <summary>
/// One row of <see cref="ArtifactReadModel"/>: the artifact's descriptive metadata plus the resolved
/// <see cref="EffectiveSensitivity"/> and the <see cref="ContentGated"/> verdict the response renders. The three
/// content-bearing members are already null when the caller may not see them — the masking happens in the
/// statement, never in a serializer or a caller's <c>if</c>.
/// </summary>
/// <param name="Id">The artifact id.</param>
/// <param name="OrgId">The owning organization.</param>
/// <param name="ProjectId">The owning project.</param>
/// <param name="RunId">The run the artifact belongs to.</param>
/// <param name="Kind">One of the eight contract kinds.</param>
/// <param name="MediaType">The stored media type.</param>
/// <param name="State">The artifact's lifecycle state.</param>
/// <param name="StoredSensitivity">The sensitivity written at mint.</param>
/// <param name="ScanState">The secret-scan state, or null when the row carries none.</param>
/// <param name="CreatedAt">When the row was created.</param>
/// <param name="EffectiveSensitivity">The sensitivity every read path enforces.</param>
/// <param name="ContentGated">True when the caller may not see the content-bearing members.</param>
/// <param name="ObjectKey">The storage key, or null when gated.</param>
/// <param name="Checksum">The content address, or null when gated.</param>
/// <param name="Bytes">The stored length, or null when gated.</param>
internal sealed record ArtifactReadRow(
    Guid Id,
    Guid OrgId,
    Guid ProjectId,
    Guid RunId,
    string Kind,
    string MediaType,
    string State,
    string StoredSensitivity,
    string? ScanState,
    DateTimeOffset CreatedAt,
    string EffectiveSensitivity,
    bool ContentGated,
    string? ObjectKey,
    string? Checksum,
    long? Bytes);
