using Evidgate.BuildingBlocks.Persistence.Audit;

namespace Evidgate.BuildingBlocks.Pipeline.Authorization;

/// <summary>Maps the request principal to the <c>actor_type</c>/<c>actor_id</c> of an audit row (02.7.1-T2.1).</summary>
public static class AuditActorResolver
{
    /// <summary>The audit actor for <paramref name="principal"/>; a missing principal is the system.</summary>
    /// <param name="principal">The caller of the flow, if any.</param>
    public static AuditActor From(IRequestPrincipal? principal) => principal switch
    {
        null => new AuditActor(AuditActorTypes.System, "system"),
        { Kind: PrincipalKind.Member, UserId: { } user } => new AuditActor(AuditActorTypes.User, user.ToString("D")),
        { Kind: PrincipalKind.ServicePrincipal } service => new AuditActor(
            AuditActorTypes.CiProducer,
            service.CredentialId ?? (service.ProjectScope is { } project ? $"project:{project:D}" : "unbound")),
        { Kind: PrincipalKind.Operator } => new AuditActor(AuditActorTypes.PlatformOperator, "operator"),
        _ => new AuditActor(AuditActorTypes.System, "system"),
    };
}
