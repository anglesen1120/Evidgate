using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;

namespace Evidgate.Modules.Identity.Login.Audit;

/// <summary>
/// The production <see cref="IIdentityAuditWriter"/> (02.3.1-T2.2, Q5): one tenant scope, one
/// <see cref="IAuditTrail"/> unit and one commit per org, in ascending <see cref="Guid"/> order.
/// </summary>
/// <remarks>
/// <para>
/// <b>One transaction per org.</b> The login and linking paths run at federated-login time, before any
/// tenant is established, so the writer opens the org's scope itself — the same shape
/// <c>Invites.MembershipBinder</c> uses for the bind it records. The audit row is appended through
/// <see cref="IAuditTrail.FlushAsync"/> inside that transaction, which is what gives the org's chain
/// its advisory lock for the length of the inserts and nowhere longer.
/// </para>
/// <para>
/// <b>Ordering and partial progress.</b> The orgs are de-duplicated and sorted, so a failure part-way is
/// reproducible: every org already visited is committed and keeps its row, the org that failed rolls
/// back, and the orgs after it are never touched. The exception propagates — the caller answers
/// 503 <c>unavailable</c> and issues no session rather than a login nothing recorded (fail-closed).
/// </para>
/// <para>
/// <b>Metadata is validated once, up front.</b> An unknown key or a credential-shaped value throws
/// <see cref="ArgumentException"/> before the first scope opens, so a programming error cannot leave
/// the user's orgs half-audited. The allowlist itself is
/// <see cref="AuditMetadata"/>'s; this writer adds no key of its own beyond the provider name.
/// </para>
/// </remarks>
internal sealed class IdentityAuditWriter : IIdentityAuditWriter
{
    /// <summary>The object type of every row: the user the entry is about.</summary>
    private const string ObjectType = "user";

    /// <summary>The metadata key that carries the provider name (on <see cref="AuditMetadata"/>'s allowlist).</summary>
    internal const string ProviderMetadataKey = "provider";

    private readonly ITenantScopeFactory _scopes;
    private readonly ITenantDb _db;
    private readonly IAuditTrail _audit;

    /// <summary>Creates the writer over the tenant scope factory, the Dapper path and the audit buffer.</summary>
    /// <param name="scopes">Opens the one tenant transaction each org's row is committed in.</param>
    /// <param name="db">The Dapper path over the ambient scope; the flush writes through it.</param>
    /// <param name="audit">The per-flow audit buffer every audited action of this deployment records through.</param>
    public IdentityAuditWriter(ITenantScopeFactory scopes, ITenantDb db, IAuditTrail audit)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);

        _scopes = scopes;
        _db = db;
        _audit = audit;
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(
        IdentityAuditEvent auditEvent,
        IReadOnlyCollection<Guid> orgIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        ArgumentNullException.ThrowIfNull(orgIds);

        // The user id is both the actor and the object: the entry says "this user logged in / linked a
        // provider", so the row's subject is the same id. Built once — AuditObject is immutable and the
        // buffer keeps it for the single Record of each org.
        var actor = new AuditActor(AuditActorTypes.User, auditEvent.UserId.ToString("D"));
        var subject = new AuditObject(ObjectType, actor.Id, Metadata: Metadata(auditEvent));

        foreach (var orgId in Ordered(orgIds))
        {
            // A scope of its own per org (00 §5.5 C2): a user with three orgs gets three chains extended
            // and no transaction that spans them.
            await using var scope = await _scopes
                .BeginAsync(orgId, projectId: null, readOnlyTransaction: false, cancellationToken)
                .ConfigureAwait(false);

            // BeginUnit joins an already open unit instead of opening a second one (the buffer's own
            // contract), so this writer is safe on a flow that already records something: the flush then
            // appends whatever the joined unit holds — the buffer's documented meaning — and clears it.
            // The login and linking callbacks run before any pipeline unit, so in practice the unit is
            // this writer's alone. A using declaration, like the pipeline's own: it ends after the commit.
            using var unit = _audit.BeginUnit();

            _audit.Describe(subject);
            _audit.Record(auditEvent.Action, actor);

            await _audit.FlushAsync(orgId, _db, cancellationToken).ConfigureAwait(false);

            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The distinct orgs in ascending <see cref="Guid"/> order: one row per chain even when the caller
    /// passes an id twice, and a write order a failure can be reasoned about from.
    /// </summary>
    /// <param name="orgIds">The orgs the caller resolved for the user.</param>
    private static IEnumerable<Guid> Ordered(IReadOnlyCollection<Guid> orgIds) => orgIds.Distinct().Order();

    /// <summary>
    /// The row's metadata: the caller's extra context plus the provider name, validated against the
    /// allowlist before anything is written. The provider always wins over an extra of the same key, so a
    /// caller cannot record a name other than the one that authenticated the identity.
    /// </summary>
    /// <param name="auditEvent">The event being recorded.</param>
    private static IReadOnlyDictionary<string, string> Metadata(IdentityAuditEvent auditEvent)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);

        if (auditEvent.ExtraMetadata is { Count: > 0 } extra)
        {
            foreach (var (key, value) in extra)
            {
                metadata[key] = value;
            }
        }

        if (auditEvent.Provider is { Length: > 0 } provider)
        {
            metadata[ProviderMetadataKey] = provider;
        }

        // Refuses an unknown key or a credential-shaped value here, before the first scope opens.
        return AuditMetadata.Validate(metadata)!;
    }
}
