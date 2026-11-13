using System.Data.Common;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.GitHub;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.BuildingBlocks.SharedKernel.Security;
using Evidgate.Modules.Catalog.Application;
using Evidgate.Modules.Catalog.Application.Commands;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Domain;
using Evidgate.Modules.Catalog.Infrastructure.Persistence;
using Mediator;
using Shouldly;
using Xunit;

namespace Catalog.UnitTests;

/// <summary>
/// The repository-binding flow's pure parts: the server-derived secret reference, the request shape and its fingerprint,
/// the replay short-circuit, the verification-failure mapping and the store's sync-state writes. The routes, the trigger
/// and the tenant boundary are proven against PostgreSQL in <c>Tenancy.IntegrationTests</c>.
/// </summary>
public sealed class GitRepoBindingTests
{
    private const string BindingKey = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void WebhookSecretReference_IsAStoreReference_DerivedFromTheBindingId()
    {
        var id = Guid.CreateVersion7();

        var reference = GitRepoSyncStates.WebhookSecretReference(id);

        SecretShape.IsReference(reference).ShouldBeTrue("the column's CHECK accepts exactly this form");
        SecretShape.SecretNameLooksLikeValue(reference["secret:".Length..])
            .ShouldBeFalse("a reference is a name, never a value a scanner would mask");
        reference.Length.ShouldBeLessThanOrEqualTo("secret:".Length + 64);
    }

    [Fact]
    public void WebhookSecretReference_IsStablePerBinding_AndDiffersBetweenBindings()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        GitRepoSyncStates.WebhookSecretReference(first).ShouldBe(GitRepoSyncStates.WebhookSecretReference(first));
        GitRepoSyncStates.WebhookSecretReference(first)
            .ShouldNotBe(GitRepoSyncStates.WebhookSecretReference(second));
    }

    [Fact]
    public void Reference_IsNotRead_FromTheRequestShape()
    {
        // The request carries the two ids and nothing else: the secret reference is not a member a caller can name.
        typeof(RegisterGitRepoBindingCommand).GetProperties()
            .Select(property => property.Name)
            .ShouldNotContain(name => name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0L, 1L, "main", false)]
    [InlineData(-1L, 1L, "main", false)]
    [InlineData(1L, 0L, "main", false)]
    [InlineData(1L, 1L, "main", true)]
    [InlineData(1L, 1L, null, true)]
    [InlineData(1L, 1L, "", false)]
    [InlineData(1L, 1L, " ", false)]
    public void Validator_RefusesWhatTheColumnsRefuse(
        long installationId,
        long repositoryId,
        string? branch,
        bool valid)
    {
        var command = new RegisterGitRepoBindingCommand(
            Guid.CreateVersion7(),
            installationId,
            repositoryId,
            branch,
            BindingKey);

        var result = new RegisterGitRepoBindingCommandValidator().Validate(command);

        result.IsValid.ShouldBe(valid, string.Join("; ", result.Errors.Select(error => error.ErrorCode)));
    }

    [Fact]
    public void Fingerprint_ExcludesTheVerifiedBranch_SoARenameOrAProbeStillReplays()
    {
        var project = Guid.CreateVersion7();

        var verified = Fingerprint(new RegisterGitRepoBindingCommand(project, 7, 5, "main", BindingKey));
        var renamed = Fingerprint(new RegisterGitRepoBindingCommand(project, 7, 5, "trunk", BindingKey));
        var probe = Fingerprint(new RegisterGitRepoBindingCommand(project, 7, 5, null, BindingKey));

        renamed.ShouldBe(verified, "GitHub's answer is mutable; folding it in would 409 a retry after a branch rename");
        probe.ShouldBe(verified, "the replay probe must hash to the request whose response it replays");
    }

    [Fact]
    public void Operation_IsTheMarkersDefault_SoTheProbeReadsWhatTheBehaviourClaimed()
    {
        var command = new RegisterGitRepoBindingCommand(Guid.CreateVersion7(), 7, 5, "main", BindingKey);

        ((IIdempotentCommand)command).Operation.ShouldBe(typeof(RegisterGitRepoBindingCommand).FullName);
        RegisterGitRepoBindingCommand.OperationName.ShouldBe(typeof(RegisterGitRepoBindingCommand).FullName);
    }

    [Fact]
    public async Task Register_StoredAnswer_IsServedWithoutReachingGitHub()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("a replay must not call GitHub"));
        var bench = new GitHubTestBench(handler);
        var expected = Binding(installationId: 7, repositoryId: 5);
        var units = new BindingUnits(replays: true, expected);

        var result = await Flow(units, bench)
            .RegisterAsync(expected.ProjectId, 7, 5, BindingKey, TestContext.Current.CancellationToken);

        result.Value.ShouldBeSameAs(expected);
        handler.Requests.ShouldBeEmpty("the request that owned the key already wrote the answer");
        units.Registered!.DefaultBranch.ShouldBeNull("a replay writes nothing, so it carries no verified branch");
    }

    [Fact]
    public async Task Register_FirstRequest_VerifiesThroughTheProvedInstallation_AndForwardsItsBranch()
    {
        var handler = GitHubReadStubs.Handler(_ => GitHubResponses.Json(GitHubResponses.RepositoryJson));
        var bench = new GitHubTestBench(handler);
        var expected = Binding(installationId: 7, repositoryId: 5);
        var units = new BindingUnits(replays: false, expected);

        var result = await Flow(units, bench)
            .RegisterAsync(expected.ProjectId, 7, 5, BindingKey, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        handler.Requests[^1].Uri.AbsolutePath.ShouldBe("/repositories/5");
        units.Registered!.DefaultBranch.ShouldBe("main");
    }

    [Fact]
    public async Task Register_Verification_NeverResolvesTheTenantInstallation()
    {
        // The verification runs with no tenant scope open: resolving the org's installation there would need one.
        var handler = GitHubReadStubs.Handler(_ => GitHubResponses.Json(GitHubResponses.RepositoryJson));
        var bench = new GitHubTestBench(handler);
        var expected = Binding(installationId: 9, repositoryId: 5);
        var units = new BindingUnits(replays: false, expected);

        var result = await Flow(units, bench)
            .RegisterAsync(expected.ProjectId, 9, 5, BindingKey, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        bench.Resolver.Calls.ShouldBe(0, "the id the first unit proved is the only installation the read may use");
    }

    [Fact]
    public async Task Register_UnconfiguredApp_Is503_AndTheWriteUnitIsNeverSent()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("no App, no outbound call"));
        var bench = new GitHubTestBench(handler, GitHubTestBench.DefaultOptions(appId: null));
        var expected = Binding(installationId: 7, repositoryId: 5);
        var units = new BindingUnits(replays: false, expected);

        var result = await Flow(units, bench)
            .RegisterAsync(expected.ProjectId, 7, 5, BindingKey, TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe(GitHubErrorCodes.NotConfigured);
        result.Error.Status.ShouldBe(503);
        handler.Requests.ShouldBeEmpty();
        units.Registered.ShouldBeNull();
    }

    [Theory]
    [InlineData(GitHubErrorCodes.NotFound, "repository_unreachable", 422)]
    [InlineData(GitHubErrorCodes.Forbidden, "repository_unreachable", 422)]
    [InlineData(GitHubErrorCodes.Unauthorized, "repository_unreachable", 422)]
    [InlineData(GitHubErrorCodes.PermissionNotGranted, "repository_unreachable", 422)]
    [InlineData(GitHubErrorCodes.NotConfigured, "github_not_configured", 503)]
    [InlineData(GitHubErrorCodes.RateLimited, GitHubErrorCodes.RateLimited, 429)]
    [InlineData(GitHubErrorCodes.Unavailable, GitHubErrorCodes.Unavailable, 503)]
    [InlineData("installation_unavailable", "installation_unavailable", 409)]
    public void VerificationFailure_MapsToTheDocumentedRefusal(string code, string expectedCode, int expectedStatus)
    {
        var error = code switch
        {
            GitHubErrorCodes.RateLimited => new Error(code, "limited", 429, Retryable: true),
            GitHubErrorCodes.Unavailable => new Error(code, "down", 503, Retryable: true),
            "installation_unavailable" => Error.Conflict(code, "no live installation"),
            _ => new Error(code, "refused", 404),
        };

        var mapped = GitRepoBindingErrors.FromVerification(error);

        mapped.Code.ShouldBe(expectedCode);
        mapped.Status.ShouldBe(expectedStatus);
    }

    [Fact]
    public async Task SetSyncState_WritesTheColumnValue_AndAnswersTheStoredState()
    {
        var db = new RecordingTenantDb("disabled");
        var store = new GitRepoBindingStore(db);
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var binding = Guid.CreateVersion7();

        var stored = await store.SetSyncStateAsync(org, project, binding, GitRepoSyncState.Disabled, TestContext.Current.CancellationToken);

        stored.ShouldBe(GitRepoSyncState.Disabled);
        db.ScalarParameters.ShouldNotBeNull();
        db.ScalarParameters!.GetType().GetProperty("sync_state")!.GetValue(db.ScalarParameters)
            .ShouldBe("disabled");
        db.ScalarParameters.GetType().GetProperty("org_id")!.GetValue(db.ScalarParameters).ShouldBe(org);
        db.ScalarParameters.GetType().GetProperty("project_id")!.GetValue(db.ScalarParameters).ShouldBe(project);
        db.ScalarParameters.GetType().GetProperty("id")!.GetValue(db.ScalarParameters).ShouldBe(binding);
    }

    [Fact]
    public async Task SetSyncState_NoVisibleRow_AnswersNull_SoTheCallerCanRefuse()
    {
        var store = new GitRepoBindingStore(new RecordingTenantDb(null));

        var stored = await store.SetSyncStateAsync(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            GitRepoSyncState.Active,
            TestContext.Current.CancellationToken);

        stored.ShouldBeNull();
    }

    [Fact]
    public async Task SetSyncState_AnUnparsableStoredValue_AnswersNullRatherThanAGuess()
    {
        var store = new GitRepoBindingStore(new RecordingTenantDb("suspended"));

        var stored = await store.SetSyncStateAsync(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            GitRepoSyncState.Active,
            TestContext.Current.CancellationToken);

        stored.ShouldBeNull("only the two documented states are a state");
    }

    /// <summary>The document the idempotency store hashes for this command, exactly as the behaviour serializes it.</summary>
    private static string Fingerprint(RegisterGitRepoBindingCommand command) => JsonSerializer.Serialize(command);

    private static GitRepoBindingDto Binding(long installationId, long repositoryId) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), installationId, repositoryId, "main", "active");

    private static GitRepoBindingRegistration Flow(IMediator mediator, GitHubTestBench bench) =>
        new(mediator, bench.Client);

    /// <summary>
    /// The two units as a fake rather than a substitute: <see cref="IMediator.Send{TResponse}"/> answers a
    /// <see cref="ValueTask{TResult}"/>, which NSubstitute's setup chain would produce outside an await (CA2012).
    /// </summary>
    /// <param name="replays">Whether the first unit reports a stored answer for the key.</param>
    /// <param name="registered">The answer the write unit replays or returns.</param>
    private sealed class BindingUnits(bool replays, GitRepoBindingDto registered) : IMediator
    {
        /// <summary>The write unit's command as the flow built it, or null when the flow stopped before it.</summary>
        internal RegisterGitRepoBindingCommand? Registered { get; private set; }

        public ValueTask<TResponse> Send<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken = default)
        {
            // The record types are matched through object: TResponse is unconstrained, so the compiler cannot see that
            // a binding command implements ICommand<TResponse>.
            if ((object)command is PrepareGitRepoBindingCommand)
            {
                return ValueTask.FromResult((TResponse)(object)Result.Success(replays));
            }

            if ((object)command is not RegisterGitRepoBindingCommand write)
            {
                throw new InvalidOperationException($"{command.GetType()} is not a binding unit");
            }

            Registered = write;
            return ValueTask.FromResult((TResponse)(object)Result.Success(registered));
        }

        public ValueTask<TResponse> Send<TResponse>(
            IQuery<TResponse> query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("the binding flow sends commands");

        public ValueTask<TResponse> Send<TResponse>(
            IRequest<TResponse> request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("the binding flow sends commands");

        public ValueTask<object?> Send(object message, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("the binding flow sends commands");

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
            IStreamCommand<TResponse> stream,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("the binding flow streams nothing");

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
            IStreamQuery<TResponse> stream,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("the binding flow streams nothing");

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
            IStreamRequest<TResponse> stream,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("the binding flow streams nothing");

        public IAsyncEnumerable<object?> CreateStream(object stream, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("the binding flow streams nothing");

        public ValueTask Publish(object notification, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("the binding flow publishes nothing");

        public ValueTask Publish<TNotification>(
            TNotification notification,
            CancellationToken cancellationToken = default)
            where TNotification : INotification =>
            throw new NotSupportedException("the binding flow publishes nothing");
    }

    /// <summary>An <see cref="ITenantDb"/> that answers one scalar and records the parameters it was given.</summary>
    private sealed class RecordingTenantDb(string? scalar) : ITenantDb
    {
        internal object? ScalarParameters { get; private set; }

        public DbConnection Connection => throw new NotSupportedException("no connection in this unit test");

        public DbTransaction Transaction => throw new NotSupportedException("no transaction in this unit test");

        public Task<IReadOnlyList<T>> QueryAsync<T>(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("the binding store runs no multi-row query");

        public Task<T?> QuerySingleOrDefaultAsync<T>(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("the binding store runs no single-row query");

        public Task<int> ExecuteAsync(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("the sync-state write is a scalar");

        public Task<T?> ScalarAsync<T>(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default)
        {
            ScalarParameters = parameters;
            return Task.FromResult((T?)(object?)scalar);
        }
    }
}
