using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using FluentValidation;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Persistence.IntegrationTests;

/// <summary>
/// The production C2 pipeline over one test database (01.4.2-T4): <c>AddEvidgatePipeline</c>
/// with this assembly's generated Mediator, so a test drives the real behaviour
/// chain — telemetry → validation → tenant transaction → authorization →
/// idempotency → handler — instead of calling the behaviours by hand. The scope
/// factory is wrapped so a test can assert WHICH scope was opened (or that none
/// was), and the permission checker and the ambient request tenant are the test's.
/// </summary>
internal sealed class PipelineTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private PipelineTestHost(NpgsqlDataSource dataSource, ServiceProvider provider, RecordingTenantScopeFactory scopes)
    {
        DataSource = dataSource;
        _provider = provider;
        Scopes = scopes;
    }

    internal NpgsqlDataSource DataSource { get; }

    /// <summary>Records every <c>BeginAsync</c> the pipeline caused — the "was a transaction opened?" proof.</summary>
    internal RecordingTenantScopeFactory Scopes { get; }

    internal IMediator Mediator => _provider.GetRequiredService<IMediator>();

    /// <summary>The host-resolved request tenant (02.6.2's slot, set by the test).</summary>
    internal IRequestTenantContextSetter TenantContext => _provider.GetRequiredService<IRequestTenantContextSetter>();

    internal static PipelineTestHost Start(
        PostgresPersistenceFixture fixture,
        string database,
        IPermissionEvaluator permissions,
        Action<IServiceCollection>? configureServices = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(permissions);

        var dataSource = fixture.OpenAppDataSource(database);
        var scopes = new RecordingTenantScopeFactory(dataSource);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(dataSource);
        services.AddSingleton(scopes);
        services.AddSingleton<ITenantScopeFactory>(scopes);

        // Registered before the pipeline so its TryAdd* keeps this fake — the same
        // way the Identity module's real evaluator replaces the deny-all default.
        services.AddSingleton(permissions);

        // The 02.6.2-T1.1 principal slot: the host publishes the caller the
        // authorization stage reads. Registered before the pipeline so its TryAdd*
        // keeps this one; ProbePrincipalAccessor.Current = null is the
        // "host resolved no caller" case.
        services.AddSingleton<ProbePrincipalAccessor>();
        services.AddSingleton<IRequestPrincipalAccessor>(provider =>
            provider.GetRequiredService<ProbePrincipalAccessor>());

        // The probe validators a host registers for itself — the pipeline resolves
        // whatever the container holds, it never scans assemblies (that is a host
        // decision: AddValidatorsFromAssembly).
        services.AddSingleton<IValidator<ProbeInvalidCommand>, ProbeInvalidCommandValidator>();

        services.AddEvidgatePipeline();

        // The generated mediator of THIS assembly, exactly as the hosts register it.
        services.AddMediator();

        configureServices?.Invoke(services);

        return new PipelineTestHost(dataSource, services.BuildServiceProvider(), scopes);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await DataSource.DisposeAsync();
    }
}

/// <summary>One <c>BeginAsync</c> the pipeline performed, as the test observed it.</summary>
/// <param name="OrgId">Tenant the scope was opened for.</param>
/// <param name="ProjectId">Project narrowing, or null.</param>
/// <param name="ReadOnly">True for a query scope (C1/T95).</param>
internal sealed record ScopeOpen(Guid OrgId, Guid? ProjectId, bool ReadOnly);

/// <summary>
/// The real <see cref="NpgsqlTenantScopeFactory"/> plus a record of every scope it
/// opened: "the invalid command never opened a transaction" and "the query opened
/// BEGIN READ ONLY" are asserted on this, not inferred.
/// </summary>
internal sealed class RecordingTenantScopeFactory : ITenantScopeFactory
{
    private readonly NpgsqlTenantScopeFactory _inner;
    private readonly List<ScopeOpen> _opens = [];

    internal RecordingTenantScopeFactory(NpgsqlDataSource dataSource) => _inner = new NpgsqlTenantScopeFactory(dataSource);

    internal IReadOnlyList<ScopeOpen> Opens => _opens;

    /// <inheritdoc />
    public ITenantScope? Current => _inner.Current;

    /// <inheritdoc />
    public ITenantScope Require() => _inner.Require();

    /// <summary>
    /// Opens through the real factory and records the attempt. Deliberately NOT
    /// <c>async</c>: the inner factory publishes the ambient scope with an
    /// <see cref="AsyncLocal{T}"/> write, and a write inside an async method would
    /// stay in that method's ExecutionContext instead of reaching the caller's
    /// flow — the same reason <see cref="NpgsqlTenantScopeFactory.BeginAsync"/> is
    /// not async either.
    /// </summary>
    public Task<ITenantScope> BeginAsync(
        Guid orgId,
        Guid? projectId = null,
        bool readOnlyTransaction = false,
        CancellationToken cancellationToken = default)
    {
        var opening = _inner.BeginAsync(orgId, projectId, readOnlyTransaction, cancellationToken);
        _opens.Add(new ScopeOpen(orgId, projectId, readOnlyTransaction));
        return opening;
    }
}
