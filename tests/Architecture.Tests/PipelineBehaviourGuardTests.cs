using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Idempotency;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Behaviours;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.Modules.Sample.Contracts;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// The three behaviours that enforce a gate — tenant transaction, authorization and
/// idempotency — refuse a message whose handler names [Permission],
/// [AuthenticatedMember] or [CredentialAuthenticated] while the response cannot carry
/// the Result they would refuse it with. A message whose handler names no gate keeps
/// passing through, which is what these tests pair with every refusal.
/// </summary>
public sealed class PipelineBehaviourGuardTests
{
    [Fact]
    public async Task AuthorizationBehaviour_Gated_Dto_Throws_Before_Next()
    {
        using var permissioned = ProviderFor<PermissionedDtoCommand>(new PermissionedDtoHandler());
        await AssertRefusedAsync(
            Authorization<PermissionedDtoCommand>(permissioned),
            new PermissionedDtoCommand(Guid.CreateVersion7()));

        using var member = ProviderFor<MemberGatedDtoCommand>(new MemberGatedDtoHandler());
        await AssertRefusedAsync(
            Authorization<MemberGatedDtoCommand>(member),
            new MemberGatedDtoCommand(Guid.CreateVersion7()));

        using var credential = ProviderFor<CredentialGatedDtoCommand>(new CredentialGatedDtoHandler());
        await AssertRefusedAsync(
            Authorization<CredentialGatedDtoCommand>(credential),
            new CredentialGatedDtoCommand(Guid.CreateVersion7()));
    }

    [Fact]
    public async Task TenantTransactionBehaviour_Gated_Dto_Throws_Before_Next()
    {
        using var permissioned = ProviderFor<PermissionedDtoCommand>(new PermissionedDtoHandler());
        await AssertRefusedAsync(
            TenantTransaction<PermissionedDtoCommand>(permissioned),
            new PermissionedDtoCommand(Guid.CreateVersion7()));

        using var member = ProviderFor<MemberGatedDtoCommand>(new MemberGatedDtoHandler());
        await AssertRefusedAsync(
            TenantTransaction<MemberGatedDtoCommand>(member),
            new MemberGatedDtoCommand(Guid.CreateVersion7()));

        using var credential = ProviderFor<CredentialGatedDtoCommand>(new CredentialGatedDtoHandler());
        await AssertRefusedAsync(
            TenantTransaction<CredentialGatedDtoCommand>(credential),
            new CredentialGatedDtoCommand(Guid.CreateVersion7()));
    }

    [Fact]
    public async Task IdempotencyBehaviour_Gated_Dto_Throws_Before_Claiming_The_Key()
    {
        var store = Substitute.For<IIdempotencyStore>();

        using var permissioned = ProviderFor<PermissionedIdempotentDtoCommand>(new PermissionedIdempotentDtoHandler());
        await AssertRefusedAsync(
            Idempotency<PermissionedIdempotentDtoCommand>(permissioned, store),
            new PermissionedIdempotentDtoCommand("key-permission"));

        using var member = ProviderFor<MemberGatedIdempotentDtoCommand>(new MemberGatedIdempotentDtoHandler());
        await AssertRefusedAsync(
            Idempotency<MemberGatedIdempotentDtoCommand>(member, store),
            new MemberGatedIdempotentDtoCommand("key-member"));

        using var credential = ProviderFor<CredentialGatedIdempotentDtoCommand>(new CredentialGatedIdempotentDtoHandler());
        await AssertRefusedAsync(
            Idempotency<CredentialGatedIdempotentDtoCommand>(credential, store),
            new CredentialGatedIdempotentDtoCommand("key-credential"));

        store.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task AuthorizationBehaviour_Ungated_Dto_Still_Runs()
    {
        using var provider = ProviderFor<UngatedDtoCommand>(new UngatedDtoHandler());

        await AssertPassedThroughAsync(
            Authorization<UngatedDtoCommand>(provider),
            new UngatedDtoCommand(Guid.CreateVersion7()));
    }

    [Fact]
    public async Task TenantTransactionBehaviour_Ungated_Dto_Still_Runs()
    {
        using var provider = ProviderFor<UngatedDtoCommand>(new UngatedDtoHandler());
        var scopes = Substitute.For<ITenantScopeFactory>();

        var behaviour = new TenantTransactionBehaviour<UngatedDtoCommand, SampleDto>(
            provider,
            scopes,
            Substitute.For<IRequestTenantContextAccessor>(),
            Substitute.For<IAuditTrail>(),
            Substitute.For<ITenantDb>());

        await AssertPassedThroughAsync(behaviour, new UngatedDtoCommand(Guid.CreateVersion7()));
        scopes.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task IdempotencyBehaviour_Ungated_Dto_Still_Runs()
    {
        using var provider = ProviderFor<UngatedIdempotentDtoCommand>(new UngatedIdempotentDtoHandler());
        var store = Substitute.For<IIdempotencyStore>();

        await AssertPassedThroughAsync(
            Idempotency<UngatedIdempotentDtoCommand>(provider, store),
            new UngatedIdempotentDtoCommand("key-ungated"));

        store.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task IdempotencyBehaviour_Gated_Command_Without_The_Marker_Still_Runs()
    {
        // The marker decides whether this stage inspects the message at all: a gated
        // command that is not an IIdempotentCommand still passes, as it always has.
        using var provider = ProviderFor<PermissionedDtoCommand>(new PermissionedDtoHandler());
        var store = Substitute.For<IIdempotencyStore>();

        await AssertPassedThroughAsync(
            Idempotency<PermissionedDtoCommand>(provider, store),
            new PermissionedDtoCommand(Guid.CreateVersion7()));

        store.ReceivedCalls().ShouldBeEmpty();
    }

    private static async Task AssertRefusedAsync<TMessage>(
        IPipelineBehavior<TMessage, SampleDto> behaviour,
        TMessage message)
        where TMessage : ICommand<SampleDto>
    {
        var calls = 0;

        var thrown = await Should.ThrowAsync<InvalidOperationException>(
            async () =>
            {
                await behaviour.Handle(
                    message,
                    (_, _) =>
                    {
                        calls++;
                        return ValueTask.FromResult(new SampleDto());
                    },
                    TestContext.Current.CancellationToken);
            });

        thrown.Message.ShouldContain(typeof(TMessage).Name);
        thrown.Message.ShouldContain(nameof(SampleDto));
        thrown.Message.ShouldContain("must answer Result");
        calls.ShouldBe(0);
    }

    private static async Task AssertPassedThroughAsync<TMessage>(
        IPipelineBehavior<TMessage, SampleDto> behaviour,
        TMessage message)
        where TMessage : ICommand<SampleDto>
    {
        var calls = 0;
        var expected = new SampleDto { Id = Guid.CreateVersion7() };

        var response = await behaviour.Handle(
            message,
            (_, _) =>
            {
                calls++;
                return ValueTask.FromResult(expected);
            },
            TestContext.Current.CancellationToken);

        calls.ShouldBe(1);
        response.ShouldBeSameAs(expected);
    }

    /// <summary>
    /// A provider that offers the closed handler interface the pipeline's lookup
    /// resolves — the attribute on that instance is the only input the guard reads.
    /// </summary>
    private static ServiceProvider ProviderFor<TMessage>(object handler)
        where TMessage : ICommand<SampleDto>
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ICommandHandler<TMessage, SampleDto>), handler);

        return services.BuildServiceProvider();
    }

    private static AuthorizationBehaviour<TMessage, SampleDto> Authorization<TMessage>(IServiceProvider services)
        where TMessage : ICommand<SampleDto> =>
        new AuthorizationBehaviour<TMessage, SampleDto>(
            services,
            Substitute.For<IPermissionEvaluator>(),
            Substitute.For<IRequestTenantContextAccessor>(),
            Substitute.For<IRequestPrincipalAccessor>(),
            Substitute.For<IAuditTrail>());

    private static TenantTransactionBehaviour<TMessage, SampleDto> TenantTransaction<TMessage>(IServiceProvider services)
        where TMessage : ICommand<SampleDto> =>
        new TenantTransactionBehaviour<TMessage, SampleDto>(
            services,
            Substitute.For<ITenantScopeFactory>(),
            Substitute.For<IRequestTenantContextAccessor>(),
            Substitute.For<IAuditTrail>(),
            Substitute.For<ITenantDb>());

    private static IdempotencyBehaviour<TMessage, SampleDto> Idempotency<TMessage>(
        IServiceProvider services,
        IIdempotencyStore store)
        where TMessage : ICommand<SampleDto> =>
        new IdempotencyBehaviour<TMessage, SampleDto>(services, store, Substitute.For<IAuditTrail>());

    private sealed record PermissionedDtoCommand(Guid Id) : ICommand<SampleDto>;

    [Permission("sample:write")]
    private sealed class PermissionedDtoHandler : ICommandHandler<PermissionedDtoCommand, SampleDto>
    {
        public ValueTask<SampleDto> Handle(PermissionedDtoCommand command, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SampleDto { Id = command.Id });
    }

    private sealed record MemberGatedDtoCommand(Guid Id) : ICommand<SampleDto>;

    [AuthenticatedMember]
    private sealed class MemberGatedDtoHandler : ICommandHandler<MemberGatedDtoCommand, SampleDto>
    {
        public ValueTask<SampleDto> Handle(MemberGatedDtoCommand command, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SampleDto { Id = command.Id });
    }

    private sealed record CredentialGatedDtoCommand(Guid Id) : ICommand<SampleDto>;

    [CredentialAuthenticated]
    private sealed class CredentialGatedDtoHandler : ICommandHandler<CredentialGatedDtoCommand, SampleDto>
    {
        public ValueTask<SampleDto> Handle(CredentialGatedDtoCommand command, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SampleDto { Id = command.Id });
    }

    private sealed record UngatedDtoCommand(Guid Id) : ICommand<SampleDto>;

    private sealed class UngatedDtoHandler : ICommandHandler<UngatedDtoCommand, SampleDto>
    {
        public ValueTask<SampleDto> Handle(UngatedDtoCommand command, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SampleDto { Id = command.Id });
    }

    private sealed record PermissionedIdempotentDtoCommand(string IdempotencyKey)
        : ICommand<SampleDto>, IIdempotentCommand;

    [Permission("sample:write")]
    private sealed class PermissionedIdempotentDtoHandler
        : ICommandHandler<PermissionedIdempotentDtoCommand, SampleDto>
    {
        public ValueTask<SampleDto> Handle(PermissionedIdempotentDtoCommand command, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SampleDto());
    }

    private sealed record MemberGatedIdempotentDtoCommand(string IdempotencyKey)
        : ICommand<SampleDto>, IIdempotentCommand;

    [AuthenticatedMember]
    private sealed class MemberGatedIdempotentDtoHandler
        : ICommandHandler<MemberGatedIdempotentDtoCommand, SampleDto>
    {
        public ValueTask<SampleDto> Handle(MemberGatedIdempotentDtoCommand command, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SampleDto());
    }

    private sealed record CredentialGatedIdempotentDtoCommand(string IdempotencyKey)
        : ICommand<SampleDto>, IIdempotentCommand;

    [CredentialAuthenticated]
    private sealed class CredentialGatedIdempotentDtoHandler
        : ICommandHandler<CredentialGatedIdempotentDtoCommand, SampleDto>
    {
        public ValueTask<SampleDto> Handle(CredentialGatedIdempotentDtoCommand command, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SampleDto());
    }

    private sealed record UngatedIdempotentDtoCommand(string IdempotencyKey)
        : ICommand<SampleDto>, IIdempotentCommand;

    private sealed class UngatedIdempotentDtoHandler
        : ICommandHandler<UngatedIdempotentDtoCommand, SampleDto>
    {
        public ValueTask<SampleDto> Handle(UngatedIdempotentDtoCommand command, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SampleDto());
    }
}
