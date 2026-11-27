using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Trackers.Application;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The consent unit of the tracker callback, run on hand-written store doubles (the store interface is internal, so it
/// cannot be a proxy). A refusal here is what the transaction behaviour rolls back, which leaves the consent unused.
/// </summary>
public sealed class TrackerConsentHandlerTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Project = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    [Theory]
    [InlineData("not-a-state", "code", null)]
    [InlineData(null, "code", null)]
    public async Task Take_MalformedState_IsRefusedBeforeAnyLookup(string? state, string? code, string? error)
    {
        var (handler, store, _) = Build(new FakeStore(new ConsentLookup.Unknown()));

        var result = await handler.Handle(new TakeTrackerConsentCommand(TrackerProvider.Linear, state, code, error), TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe(ApiErrorCode.TrackerConsentInvalid);
        store.Takes.ShouldBe(0);
    }

    [Fact]
    public async Task Take_MissingOrOversizedCode_IsRefusedBeforeAnyLookup()
    {
        var (handler, store, _) = Build(new FakeStore(new ConsentLookup.Unknown()));
        var state = TrackerState.New(Org);

        foreach (var code in new string?[] { null, "", new string('c', TrackerState.MaxCodeLength + 1) })
        {
            var result = await handler.Handle(new TakeTrackerConsentCommand(TrackerProvider.Linear, state, code, null), TestContext.Current.CancellationToken);
            result.Error!.Code.ShouldBe(ApiErrorCode.TrackerConsentInvalid);
        }

        var tooLong = await handler.Handle(
            new TakeTrackerConsentCommand(TrackerProvider.Linear, state, null, new string('e', TrackerState.MaxErrorLength + 1)),
            TestContext.Current.CancellationToken);
        tooLong.Error!.Code.ShouldBe(ApiErrorCode.TrackerConsentInvalid);
        store.Takes.ShouldBe(0);
    }

    [Fact]
    public async Task Take_UnknownExpiredAndReplayedStates_AnswerOneBody()
    {
        var bodies = new List<string>();
        foreach (ConsentLookup lookup in new ConsentLookup[] { new ConsentLookup.Unknown(), new ConsentLookup.Expired(), new ConsentLookup.Replayed() })
        {
            var (handler, _, _) = Build(new FakeStore(lookup));
            var result = await handler.Handle(Take(TrackerProvider.Linear), TestContext.Current.CancellationToken);
            result.IsSuccess.ShouldBeFalse();
            bodies.Add($"{result.Error!.Status}|{result.Error.Code}|{result.Error.Message}");
        }

        bodies.Distinct().Count().ShouldBe(1);
        bodies[0].ShouldStartWith("400|" + ApiErrorCode.TrackerConsentInvalid);
    }

    [Fact]
    public async Task Take_ConsentOfAnotherUser_IsForbidden()
    {
        var (handler, _, _) = Build(new FakeStore(new ConsentLookup.WrongUser()));

        var result = await handler.Handle(Take(TrackerProvider.Linear), TestContext.Current.CancellationToken);

        result.Error!.Status.ShouldBe(403);
    }

    [Fact]
    public async Task Take_ConsentForAnotherProviderOrOrg_IsRefusedAsInvalid()
    {
        var other = Consent() with { Provider = TrackerProvider.JiraCloud };
        var (handler, _, _) = Build(new FakeStore(new ConsentLookup.Taken(other)));
        (await handler.Handle(Take(TrackerProvider.Linear), TestContext.Current.CancellationToken)).Error!.Code
            .ShouldBe(ApiErrorCode.TrackerConsentInvalid);

        var foreign = Consent() with { OrgId = Guid.NewGuid() };
        var (orgHandler, _, _) = Build(new FakeStore(new ConsentLookup.Taken(foreign)));
        (await orgHandler.Handle(Take(TrackerProvider.Linear), TestContext.Current.CancellationToken)).Error!.Code
            .ShouldBe(ApiErrorCode.TrackerConsentInvalid);
    }

    [Fact]
    public async Task Take_CredentialScopedToAnotherProject_IsRefused()
    {
        var (handler, _, _) = Build(new FakeStore(new ConsentLookup.Taken(Consent())), projectScope: Guid.NewGuid());

        var result = await handler.Handle(Take(TrackerProvider.Linear), TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe(ApiErrorCode.ProjectScopeMismatch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("access_denied")]
    public async Task Take_ValidConsent_IsTakenOnceAndAudited(string? providerError)
    {
        var (handler, store, audit) = Build(new FakeStore(new ConsentLookup.Taken(Consent())));
        var command = new TakeTrackerConsentCommand(TrackerProvider.Linear, TrackerState.New(Org), providerError is null ? "code" : null, providerError);

        var result = await handler.Handle(command, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ProjectId.ShouldBe(Project);
        store.Takes.ShouldBe(1);
        store.LastHash!.Length.ShouldBe(32);
        audit.Received(1).Describe(Arg.Is<AuditObject>(o => o.ProjectId == Project));
    }

    [Fact]
    public void Commands_NeverPrintTheirSecrets()
    {
        var token = new TrackerToken("access-canary", "refresh-canary", null, ["read"]);
        var consent = Consent() with { PkceVerifier = "verifier-canary" };
        var state = TrackerState.New(Org);

        var text = string.Join(
            '|',
            token,
            new CompleteTrackerConnectionCommand(consent, token),
            new TakeTrackerConsentCommand(TrackerProvider.Linear, state, "code-canary", null));

        text.ShouldNotContain("canary");
        text.ShouldNotContain(state);
    }

    [Fact]
    public void State_IsSha256HashedAndCarriesTheOrgOnly()
    {
        var first = TrackerState.New(Org);
        var second = TrackerState.New(Org);

        first.ShouldNotBe(second);
        TrackerState.Org(first).ShouldBe(Org);
        TrackerState.Org("junk").ShouldBeNull();
        TrackerState.Org(new string('a', TrackerState.MaxStateLength + 1)).ShouldBeNull();
        TrackerState.Hash(first).ShouldNotBe(TrackerState.Hash(second));
    }

    private static TakeTrackerConsentCommand Take(TrackerProvider provider) =>
        new(provider, TrackerState.New(Org), "code", null);

    private static PendingConsent Consent() =>
        new(Org, Project, User, TrackerProvider.Linear, "https://evidgate.test/v1/tracker-connections/linear/callback", "verifier");

    private static (TakeTrackerConsentCommandHandler Handler, FakeStore Store, IAuditTrail Audit) Build(FakeStore store, Guid? projectScope = null)
    {
        var scope = Substitute.For<ITenantScope>();
        scope.OrgId.Returns(Org);
        var scopes = Substitute.For<ITenantScopeAccessor>();
        scopes.Require().Returns(scope);

        var principal = Substitute.For<IRequestPrincipal>();
        principal.UserId.Returns(User);
        principal.ProjectScope.Returns(projectScope);
        var principals = Substitute.For<IRequestPrincipalAccessor>();
        principals.Current.Returns(principal);

        var tenantContext = Substitute.For<IRequestTenantContext>();
        tenantContext.OrgId.Returns(Org);
        var tenant = Substitute.For<IRequestTenantContextAccessor>();
        tenant.Current.Returns(tenantContext);

        var audit = Substitute.For<IAuditTrail>();
        return (new TakeTrackerConsentCommandHandler(scopes, principals, tenant, store, audit), store, audit);
    }

    private sealed class FakeStore(ConsentLookup lookup) : ITrackerConnectionStore
    {
        public int Takes { get; private set; }

        public byte[]? LastHash { get; private set; }

        public Task<ConsentLookup> TakeConsentAsync(byte[] stateHash, Guid userId, CancellationToken ct)
        {
            Takes++;
            LastHash = stateHash;
            return Task.FromResult(lookup);
        }

        public Task SaveConsentAsync(byte[] stateHash, PendingConsent consent, TimeSpan ttl, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<TrackerConnectionView> CreateAsync(NewTrackerConnection connection, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TrackerConnectionView>> ListAsync(Guid projectId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> DisconnectAsync(Guid projectId, Guid connectionId, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
