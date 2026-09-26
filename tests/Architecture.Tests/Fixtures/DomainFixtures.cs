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
