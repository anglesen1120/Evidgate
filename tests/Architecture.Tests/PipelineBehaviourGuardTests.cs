using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Idempotency;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Behaviours;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Results;
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
/// the Result they would refuse it with. Command, query and request messages all traverse
/// that pipeline, so each shape is exercised, and a message with no gate or with a Result
/// response keeps passing through — paired here with every refusal.
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

        // Queries and requests traverse the same pipeline, so the guard covers them alike.
        using var query = ProviderFor(typeof(IQueryHandler<GatedQuery, SampleDto>), new GatedQueryHandler());
        await AssertRefusedAsync(
            Authorization<GatedQuery>(query),
            new GatedQuery(Guid.CreateVersion7()));

        using var request = ProviderFor(typeof(IRequestHandler<GatedRequest, SampleDto>), new GatedRequestHandler());
        await AssertRefusedAsync(
            Authorization<GatedRequest>(request),
            new GatedRequest(Guid.CreateVersion7()));
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

        using var query = ProviderFor(typeof(IQueryHandler<GatedQuery, SampleDto>), new GatedQueryHandler());
        await AssertRefusedAsync(
            TenantTransaction<GatedQuery>(query),
            new GatedQuery(Guid.CreateVersion7()));

        using var request = ProviderFor(typeof(IRequestHandler<GatedRequest, SampleDto>), new GatedRequestHandler());
        await AssertRefusedAsync(
            TenantTransaction<GatedRequest>(request),
            new GatedRequest(Guid.CreateVersion7()));
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

        using var query = ProviderFor(
            typeof(IQueryHandler<GatedIdempotentQuery, SampleDto>),
            new GatedIdempotentQueryHandler());
        await AssertRefusedAsync(
            Idempotency<GatedIdempotentQuery>(query, store),
            new GatedIdempotentQuery("key-query"));

        using var request = ProviderFor(
            typeof(IRequestHandler<GatedIdempotentRequest, SampleDto>),
            new GatedIdempotentRequestHandler());
        await AssertRefusedAsync(
            Idempotency<GatedIdempotentRequest>(request, store),
            new GatedIdempotentRequest("key-request"));

        store.ReceivedCalls().ShouldBeEmpty();
    }

    /// <summary>
    /// The guard covers only a response that cannot carry a Result: a gated message answering
    /// <c>Result&lt;T&gt;</c> keeps its stage's normal behaviour instead of throwing.
    /// </summary>
    [Fact]
    public async Task Gated_Result_Message_Crosses_The_Guard_And_Runs_Its_Stage()
    {
        var tenant = TenantAccessor();
        using var provider = ProviderFor(
            typeof(ICommandHandler<GatedResultCommand, Result<Guid>>),
            typeof(ICommandHandler<GatedResultTwinCommand, SampleDto>),
            new GatedResultCommandHandler());

        // Non-vacuity: the same instance trips every guard as a DTO, so a pass-through cannot be a no-op.
        await AssertSame_Handler_Is_Seen_As_Gated(provider);

        // A stage of its own: no principal, so authorization refuses through the Result.
        var authorizationCalls = 0;
        var anonymous = Substitute.For<IRequestPrincipalAccessor>();
        anonymous.Current.Returns((IRequestPrincipal?)null);
        var denied = await new AuthorizationBehaviour<GatedResultCommand, Result<Guid>>(
                provider,
                Substitute.For<IPermissionEvaluator>(),
                tenant,
                anonymous,
                Substitute.For<IAuditTrail>())
            .Handle(
                new GatedResultCommand("key-result"),
                (_, _) =>
                {
                    authorizationCalls++;
                    return ValueTask.FromResult(Result.Success(Guid.CreateVersion7()));
                },
                TestContext.Current.CancellationToken);

        authorizationCalls.ShouldBe(0);
        ResultFactory.TryGetFailure(denied, out var error).ShouldBeTrue();
        error!.Status.ShouldBe(403);

        var scope = Substitute.For<ITenantScope>();
        scope.CommitAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var scopes = Substitute.For<ITenantScopeFactory>();
        scopes.BeginAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(scope));
        var audit = Substitute.For<IAuditTrail>();
        audit.FlushAsync(Arg.Any<Guid>(), Arg.Any<ITenantDb>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(0));

        var handled = await new TenantTransactionBehaviour<GatedResultCommand, Result<Guid>>(
                provider,
                scopes,
                tenant,
                audit,
                Substitute.For<ITenantDb>())
            .Handle(
                new GatedResultCommand("key-result"),
                (_, _) => ValueTask.FromResult(Result.Success(Guid.CreateVersion7())),
                TestContext.Current.CancellationToken);

        handled.IsSuccess.ShouldBeTrue();
        await scope.Received(1).CommitAsync(Arg.Any<CancellationToken>());

        var recordId = Guid.CreateVersion7();
        var store = Substitute.For<IIdempotencyStore>();
        store.TryBeginAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new IdempotencyBeginResult(IdempotencyOutcome.Proceed, recordId)));
        store.CompleteAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var stored = await new IdempotencyBehaviour<GatedResultCommand, Result<Guid>>(
                provider,
                store,
                Substitute.For<IAuditTrail>())
            .Handle(
                new GatedResultCommand("key-result"),
                (_, _) => ValueTask.FromResult(Result.Success(Guid.CreateVersion7())),
                TestContext.Current.CancellationToken);

        stored.IsSuccess.ShouldBeTrue();
        await store.Received(1).CompleteAsync(recordId, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
        where TMessage : IMessage
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
        where TMessage : IMessage
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

    /// <summary>The handler's closed interface: the attribute on that instance is all the guard reads.</summary>
    private static ServiceProvider ProviderFor(Type handlerContract, object handler)
    {
        var services = new ServiceCollection();
        services.AddSingleton(handlerContract, handler);

        return services.BuildServiceProvider();
    }

    /// <summary>One handler instance offered under the two closed contracts a non-vacuity pair needs.</summary>
    private static ServiceProvider ProviderFor(Type firstContract, Type secondContract, object handler)
    {
        var services = new ServiceCollection();
        services.AddSingleton(firstContract, handler);
        services.AddSingleton(secondContract, handler);

        return services.BuildServiceProvider();
    }

    /// <summary>The same instance must trip every guard as a DTO, or the Result case is a no-op.</summary>
    private static async Task AssertSame_Handler_Is_Seen_As_Gated(ServiceProvider provider)
    {
        await AssertRefusedAsync(Authorization<GatedResultTwinCommand>(provider), new GatedResultTwinCommand("key-twin"));
        await AssertRefusedAsync(TenantTransaction<GatedResultTwinCommand>(provider), new GatedResultTwinCommand("key-twin"));
        await AssertRefusedAsync(
            Idempotency<GatedResultTwinCommand>(provider, Substitute.For<IIdempotencyStore>()),
            new GatedResultTwinCommand("key-twin"));
    }

    private static ServiceProvider ProviderFor<TMessage>(object handler) where TMessage : ICommand<SampleDto> =>
        ProviderFor(typeof(ICommandHandler<TMessage, SampleDto>), handler);

    private static AuthorizationBehaviour<TMessage, SampleDto> Authorization<TMessage>(IServiceProvider services)
        where TMessage : IMessage =>
        new AuthorizationBehaviour<TMessage, SampleDto>(
            services,
            Substitute.For<IPermissionEvaluator>(),
            Substitute.For<IRequestTenantContextAccessor>(),
            Substitute.For<IRequestPrincipalAccessor>(),
            Substitute.For<IAuditTrail>());

    private static IRequestTenantContextAccessor TenantAccessor()
    {
        var accessor = Substitute.For<IRequestTenantContextAccessor>();
        accessor.Require().Returns(new RequestTenantContext(Guid.CreateVersion7(), ProjectId: null));

        return accessor;
    }

    private static TenantTransactionBehaviour<TMessage, SampleDto> TenantTransaction<TMessage>(IServiceProvider services)
        where TMessage : IMessage =>
        new TenantTransactionBehaviour<TMessage, SampleDto>(
            services,
            Substitute.For<ITenantScopeFactory>(),
            Substitute.For<IRequestTenantContextAccessor>(),
            Substitute.For<IAuditTrail>(),
            Substitute.For<ITenantDb>());

    private static IdempotencyBehaviour<TMessage, SampleDto> Idempotency<TMessage>(
        IServiceProvider services,
        IIdempotencyStore store)
        where TMessage : IMessage =>
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

    private sealed record GatedQuery(Guid Id) : IQuery<SampleDto>;

    [Permission("sample:write")]
    private sealed class GatedQueryHandler : IQueryHandler<GatedQuery, SampleDto>
    {
        public ValueTask<SampleDto> Handle(GatedQuery query, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SampleDto { Id = query.Id });
    }

    private sealed record GatedRequest(Guid Id) : IRequest<SampleDto>;

    [CredentialAuthenticated]
    private sealed class GatedRequestHandler : IRequestHandler<GatedRequest, SampleDto>
    {
        public ValueTask<SampleDto> Handle(GatedRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SampleDto { Id = request.Id });
    }

    private sealed record GatedIdempotentQuery(string IdempotencyKey) : IQuery<SampleDto>, IIdempotentCommand;

    [AuthenticatedMember]
    private sealed class GatedIdempotentQueryHandler : IQueryHandler<GatedIdempotentQuery, SampleDto>
    {
        public ValueTask<SampleDto> Handle(GatedIdempotentQuery query, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SampleDto());
    }

    private sealed record GatedIdempotentRequest(string IdempotencyKey) : IRequest<SampleDto>, IIdempotentCommand;

    [Permission("sample:write")]
    private sealed class GatedIdempotentRequestHandler : IRequestHandler<GatedIdempotentRequest, SampleDto>
    {
        public ValueTask<SampleDto> Handle(GatedIdempotentRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SampleDto());
    }

    private sealed record GatedResultCommand(string IdempotencyKey) : ICommand<Result<Guid>>, IIdempotentCommand;

    private sealed record GatedResultTwinCommand(string IdempotencyKey) : ICommand<SampleDto>, IIdempotentCommand;

    [Permission("sample:write")]
    private sealed class GatedResultCommandHandler
        : ICommandHandler<GatedResultCommand, Result<Guid>>,
          ICommandHandler<GatedResultTwinCommand, SampleDto>
    {
        public ValueTask<Result<Guid>> Handle(GatedResultCommand command, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success(Guid.Empty));

        public ValueTask<SampleDto> Handle(GatedResultTwinCommand command, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SampleDto());
    }
}
