using Evidgate.BuildingBlocks.SharedKernel.Authorization;
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
