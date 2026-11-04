using Evidgate.BuildingBlocks.Persistence.Tenancy;

namespace Evidgate.Modules.Evidence.Application.Artifacts;

/// <summary>
/// The artifact projection shared by metadata reads and download issuance. It resolves effective sensitivity
/// from policy, stored classification and scan state, and masks content when sensitivity or deletion gates it.
/// </summary>
internal static class ArtifactReadModel
{
    /// <summary>
    /// The shared column list. <c>effective_sensitivity</c> is <c>sensitive</c> when the project's policy now
    /// classifies the kind sensitive (raising a policy gates the rows that already exist), when the row was minted
    /// sensitive, or when the scan has not proved it clean (<c>pending</c> is not yet scanned, <c>flagged</c> found
    /// a secret, and an unknown state fails closed). Published or not, the policy can only ever raise: lowering it
    /// needs the audited override route and never rewrites a stored classification. A row being deleted or already
    /// deleted gates the same three columns whatever permission the caller holds: its bytes are gone or going.
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
               gated.content_gated,
               case when gated.content_gated then null else a.object_key end as object_key,
               case when gated.content_gated then null else a.checksum end as checksum,
               case when gated.content_gated then null else a.bytes end as bytes
          from public.artifact a
          join public.project p
            on p.org_id = a.org_id
           and p.id = a.project_id
          left join public.artifact_kind_policy kp
            on kp.org_id = a.org_id
           and kp.project_id = a.project_id
           and kp.kind = a.kind
          cross join lateral (
              select a.state in ('pending_delete', 'deleted')
                     or ((a.scan_state <> 'clean'
                          or a.sensitivity = 'sensitive'
                          or kp.default_sensitivity = 'sensitive')
                         and not @can_read_sensitive) as content_gated
          ) gated
        """;

    /// <summary>One artifact by id, as the tenant scope sees it (RLS already hides another org's row).</summary>
    private const string ByIdSql = Projection + """

         where a.id = @artifact_id
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
/// <param name="ContentGated">True when the caller may not see the content-bearing members, or when the content is
/// being deleted.</param>
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
