using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Mediator;

// Synthetic module fixtures for the reflection rules. The namespace mimics a real
// module (Evidgate.Modules.Sample.Domain) but the containing assembly is
// Architecture.Tests, so the real-solution scan never sees them; tests pass them
// to the rules explicitly. Do not move these types into a production project.
namespace Evidgate.Modules.Sample.Domain;

/// <summary>Clean fixture: a Domain type that only references other Domain types.</summary>
internal sealed class SampleAggregate
{
    public SampleValue Value { get; } = new();
}

internal sealed class SampleValue;

/// <summary>Seeded violation fixture: a Domain type referencing Infrastructure.</summary>
internal sealed class DomainTypeTouchingInfrastructure
{
    // Property of an Infrastructure type — picked up by DirectDependencies.
    public Evidgate.Modules.Sample.Infrastructure.EfRepository Repository { get; } = new();
}

/// <summary>Seeded violation fixture: a Domain type referencing Infrastructure via ctor injection.</summary>
internal sealed class DomainViaConstructorInjection
{
    public DomainViaConstructorInjection(Evidgate.Modules.Sample.Infrastructure.EfRepository repository) =>
        _ = repository; // read so the parameter is used; the dependency is what matters
}

/// <summary>Seeded violation fixture: a public type outside .Contracts/.Endpoints.</summary>
public sealed class PublicTypeOutsideContractsOrEndpoints;

public sealed class CreateSampleCommand : ICommand<Guid>;

/// <summary>Clean fixture: handler carrying the required permission declaration.</summary>
[Permission("run:create")]
internal sealed class CreateSampleHandler : ICommandHandler<CreateSampleCommand, Guid>
{
    public ValueTask<Guid> Handle(CreateSampleCommand command, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Guid.Empty);
}

/// <summary>Seeded violation fixture: a Mediator handler without [Permission].</summary>
internal sealed class HandlerMissingPermission : ICommandHandler<CreateSampleCommand, Guid>
{
    public ValueTask<Guid> Handle(CreateSampleCommand command, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Guid.Empty);
}

/// <summary>
/// Seeded violation fixture: [Permission] present but the string is not a valid
/// lower-snake resource:action — must be reported, not throw.
/// </summary>
[Permission("RUN CREATE")]
internal sealed class HandlerWithInvalidPermission : ICommandHandler<CreateSampleCommand, Guid>
{
    public ValueTask<Guid> Handle(CreateSampleCommand command, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Guid.Empty);
}

/// <summary>
/// Seeded violation fixture for the C8 rule: a pipeline handler answering a bare
/// DTO instead of Result/Result&lt;T&gt;, which would run outside the C2 tenant
/// stages (01.4.2-T4). It carries a valid permission so only the contract rule
/// reports it.
/// </summary>
internal sealed record CommandReturningDto : ICommand<Evidgate.Modules.Sample.Contracts.SampleDto>;

[Permission("run:create")]
internal sealed class HandlerReturningDto : ICommandHandler<CommandReturningDto, Evidgate.Modules.Sample.Contracts.SampleDto>
{
    public ValueTask<Evidgate.Modules.Sample.Contracts.SampleDto> Handle(
        CommandReturningDto command,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new Evidgate.Modules.Sample.Contracts.SampleDto());
}

/// <summary>Seeded violation fixture for the C8 rule: a Unit-returning command and its handler.</summary>
internal sealed record CommandReturningUnit : ICommand;

[Permission("run:create")]
internal sealed class HandlerReturningUnit : ICommandHandler<CommandReturningUnit>
{
    public ValueTask<Unit> Handle(CommandReturningUnit command, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Unit.Value);
}

/// <summary>Clean fixture for the C8 rule: a handler answering Result&lt;T&gt;.</summary>
internal sealed record CommandReturningResult : ICommand<Result<Guid>>;

[Permission("run:create")]
internal sealed class HandlerReturningResult : ICommandHandler<CommandReturningResult, Result<Guid>>
{
    public ValueTask<Result<Guid>> Handle(CommandReturningResult command, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success(Guid.Empty));
}

/// <summary>Clean fixture for the audit rule (02.7.1-T2.1): a Result-returning mutation with its audited action.</summary>
[Permission("run:create")]
[AuditedAction("run.create")]
internal sealed class AuditedHandlerReturningResult : ICommandHandler<CommandReturningResult, Result<Guid>>
{
    public ValueTask<Result<Guid>> Handle(CommandReturningResult command, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success(Guid.Empty));
}

/// <summary>
/// Seeded violation fixture for the gated-handler rule: [AuthenticatedMember] on a handler
/// answering a bare DTO, so the stage enforcing the gate could not refuse it.
/// </summary>
[AuthenticatedMember]
internal sealed class HandlerWithAuthenticatedMemberReturningDto
    : ICommandHandler<CommandReturningDto, Evidgate.Modules.Sample.Contracts.SampleDto>
{
    public ValueTask<Evidgate.Modules.Sample.Contracts.SampleDto> Handle(
        CommandReturningDto command,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new Evidgate.Modules.Sample.Contracts.SampleDto());
}

/// <summary>
/// Seeded violation fixture for the gated-handler rule: [CredentialAuthenticated] on a
/// handler answering a bare DTO instead of the Result the pipeline can refuse.
/// </summary>
[CredentialAuthenticated]
internal sealed class HandlerWithCredentialReturningDto
    : ICommandHandler<CommandReturningDto, Evidgate.Modules.Sample.Contracts.SampleDto>
{
    public ValueTask<Evidgate.Modules.Sample.Contracts.SampleDto> Handle(
        CommandReturningDto command,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new Evidgate.Modules.Sample.Contracts.SampleDto());
}

/// <summary>
/// Negative control for the gated-handler rule: the same bare DTO answer with no gate at
/// all, which that rule must leave to the general handler-shape rule.
/// </summary>
internal sealed class HandlerWithoutGateReturningDto
    : ICommandHandler<CommandReturningDto, Evidgate.Modules.Sample.Contracts.SampleDto>
{
    public ValueTask<Evidgate.Modules.Sample.Contracts.SampleDto> Handle(
        CommandReturningDto command,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new Evidgate.Modules.Sample.Contracts.SampleDto());
}

/// <summary>
/// Seeded violation fixture (02.1.1): [AuthenticatedMember] on a handler that is not
/// in the allowlist — the hole in 00 §5.7 S2 must stay closed by name.
/// </summary>
[AuthenticatedMember]
internal sealed class HandlerWithUnlistedAuthenticatedMember : ICommandHandler<CreateSampleCommand, Guid>
{
    public ValueTask<Guid> Handle(CreateSampleCommand command, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Guid.Empty);
}

/// <summary>Seeded violation fixture (02.1.1): both gates on one handler.</summary>
[AuthenticatedMember]
[Permission("run:create")]
internal sealed class HandlerWithBothGates : ICommandHandler<CreateSampleCommand, Guid>
{
    public ValueTask<Guid> Handle(CreateSampleCommand command, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Guid.Empty);
}

/// <summary>Seeded violation fixture (02.1.1): a non-allowlisted type taking the tenant setter.</summary>
internal static class TenantPublishingFixture
{
    internal static void Publish(Evidgate.BuildingBlocks.Pipeline.Tenancy.IRequestTenantContextSetter setter) =>
        setter.Publish(Guid.Empty, projectId: null).Dispose();
}

/// <summary>
/// Base fixture for the inherited-gate case: [Permission] here is what the pipeline reads
/// on the derived handler too, because the attribute is declared Inherited and the runtime
/// lookup passes <c>inherit: true</c>.
/// </summary>
[Permission("run:create")]
internal abstract class PermissionGatedHandlerBase
    : ICommandHandler<CommandReturningDto, Evidgate.Modules.Sample.Contracts.SampleDto>
{
    public ValueTask<Evidgate.Modules.Sample.Contracts.SampleDto> Handle(
        CommandReturningDto command,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new Evidgate.Modules.Sample.Contracts.SampleDto());
}

/// <summary>
/// Seeded violation fixture for the gated-handler rule: it declares no attribute itself and
/// inherits [Permission] from <see cref="PermissionGatedHandlerBase"/>, so a rule that only
/// looked at the type's own attributes would let a gated DTO answer through.
/// </summary>
internal sealed class HandlerInheritingPermissionReturningDto : PermissionGatedHandlerBase;

/// <summary>Seeded violation fixture: a stream message whose handler carries a gate.</summary>
internal sealed record StreamQueryReturningDto : IStreamQuery<Evidgate.Modules.Sample.Contracts.SampleDto>;

/// <summary>
/// Base fixture for the bypass case: an abstract stream handler is never dispatched, so the
/// rule must stay silent about it and report the concrete subclass that inherits its gate.
/// </summary>
[Permission("run:create")]
internal abstract class GatedStreamHandlerBase
    : IStreamQueryHandler<StreamQueryReturningDto, Evidgate.Modules.Sample.Contracts.SampleDto>
{
    public abstract IAsyncEnumerable<Evidgate.Modules.Sample.Contracts.SampleDto> Handle(
        StreamQueryReturningDto query,
        CancellationToken cancellationToken);
}

/// <summary>
/// Seeded violation fixture for the gated-handler rule: a gated stream handler. Its message is
/// consumed through the stream handler interface, never through <c>IPipelineBehavior</c>, so no
/// stage could enforce the permission it names.
/// </summary>
internal sealed class HandlerWithGateStreamingDto : GatedStreamHandlerBase
{
    public override IAsyncEnumerable<Evidgate.Modules.Sample.Contracts.SampleDto> Handle(
        StreamQueryReturningDto query,
        CancellationToken cancellationToken) =>
        System.Linq.AsyncEnumerable.Empty<Evidgate.Modules.Sample.Contracts.SampleDto>();
}

/// <summary>Seeded violation fixture: a published notification whose handler carries a gate.</summary>
internal sealed record SampleNotificationFixture : INotification;

/// <summary>
/// Seeded violation fixture for the gated-handler rule: notifications are published to every
/// handler outside the request pipeline, so a gate declared here is never enforced.
/// </summary>
[Permission("run:create")]
internal sealed class HandlerWithGateOnNotification : INotificationHandler<SampleNotificationFixture>
{
    public ValueTask Handle(SampleNotificationFixture notification, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

/// <summary>
/// Clean fixture: the same stream handler with no gate at all. The gated-handler rule only
/// covers types that name a gate, so this one must not be reported.
/// </summary>
internal sealed class HandlerStreamingDtoWithoutGate
    : IStreamQueryHandler<StreamQueryReturningDto, Evidgate.Modules.Sample.Contracts.SampleDto>
{
    public IAsyncEnumerable<Evidgate.Modules.Sample.Contracts.SampleDto> Handle(
        StreamQueryReturningDto query,
        CancellationToken cancellationToken) =>
        System.Linq.AsyncEnumerable.Empty<Evidgate.Modules.Sample.Contracts.SampleDto>();
}

/// <summary>Clean fixture: the same notification handler with no gate at all.</summary>
internal sealed class HandlerOnNotificationWithoutGate : INotificationHandler<SampleNotificationFixture>
{
    public ValueTask Handle(SampleNotificationFixture notification, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
