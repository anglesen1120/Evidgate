// The world, the seeded artifact and its kind policy, the call the consumer makes, and the two seams the cases replace:
// the parse runner and the logger.
using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Evidgate.Modules.Ingestion.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceParseTaskTests
{
    // ---------------------------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------------------------

    private async Task<World> StartAsync()
    {
        var database = await _fixture.SharedDatabaseAsync();
        Guid user;
        Guid org;
        Guid project;

        await using (var setupHost = _hosts.RealResolver(database))
        {
            using var setup = setupHost.CreateClient();
            user = Guid.CreateVersion7();
            org = await CreateOrgAsync(setup, user, Unique.Slug("acme"));
            project = await CreateProjectAsync(setup, user, org, Unique.Slug("checkout"));
        }

        // Seeded before the host exists, so a seeding failure cannot leave a private host undisposed.
        var runId = await RunRowSeeder.SeedRunAsync(_fixture, database, org);

        // The store configuration is this test's own, so the host stays private instead of pooled.
        var host = _hosts.WithPrincipal(
            database,
            org,
            project,
            RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: "jti-parse-1"),
            _store.Configuration());

        return new World(host, database, org, project, runId);
    }

    /// <summary>
    /// A second org in the shared database, with a project and one ready artifact, for the cross-tenant case. The slug is
    /// suffixed here so sibling tests that ask for the same stem still get a distinct org.
    /// </summary>
    private async Task<World> CreateOtherWorldAsync(string slug)
    {
        var database = await _fixture.SharedDatabaseAsync();
        var unique = Unique.Slug(slug);
        Guid org;
        Guid project;

        await using (var setupHost = _hosts.RealResolver(database))
        {
            using var setup = setupHost.CreateClient();
            var user = Guid.CreateVersion7();
            org = await CreateOrgAsync(setup, user, unique);
            project = await CreateProjectAsync(setup, user, org, unique + "-project");
        }

        // Seeded before the host exists, so a seeding failure cannot leave a private host undisposed.
        var runId = await RunRowSeeder.SeedRunAsync(_fixture, database, org);

        // The store configuration is this test's own, so the host stays private instead of pooled.
        var host = _hosts.WithPrincipal(
            database,
            org,
            project,
            RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: "jti-parse-2"),
            _store.Configuration());

        return new World(host, database, org, project, runId);
    }

    /// <summary>One <c>ready</c> <c>raw_report</c> artifact whose final object the store really holds.</summary>
    private Task<Seeded> SeedReadyAsync(World world) => SeedAsync(world, ArtifactState.Ready, failureCode: null);

    /// <summary>
    /// Inserts one artifact row (as the migration role, the same way the 06.1.2-T2 suite seeds the rows a caller must
    /// not resolve) and puts the report's bytes at its final key, so the handler's read is a real store read.
    /// </summary>
    /// <param name="world">The tenant the artifact belongs to.</param>
    /// <param name="state">The row's <c>artifact.state</c>.</param>
    /// <param name="failureCode">The reason code a rejected row carries.</param>
    /// <param name="objectKey">
    /// Mints the <c>object_key</c> the row carries instead of its own final key, for the defence-in-depth case. It is a
    /// function of the row's id because the id is minted here and the 0028 shape CHECK keys on it; the bytes are put at
    /// the store only for the default (minted) key, so a test that overrides it proves the handler refused before
    /// reading anything.
    /// </param>
    /// <param name="projectId">
    /// The project the row names, or null for the run's own: no key ties the two columns together, so a row of another
    /// project lets a case prove which one the read asks about.
    /// </param>
    private async Task<Seeded> SeedAsync(
        World world,
        string state,
        string? failureCode,
        Func<Guid, string>? objectKey = null,
        Guid? projectId = null)
    {
        var artifactId = Guid.CreateVersion7();
        var owner = projectId ?? world.Project;

        // The final-key shape the table's own check constraint states (0028:81-84), so the row is the real promotion
        // target rather than a key only this test agrees with.
        var key = objectKey is null ? MintedKey(world, owner, artifactId) : objectKey(artifactId);

        if (objectKey is null)
        {
            await _store.PutAsync(key, ReportBody);
        }

        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.artifact (id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, media_type, checksum, sensitivity, scan_state, state, bytes, failure_code) "
            + $"values ('{artifactId:D}', '{world.Org:D}', '{owner:D}', '{world.RunId:D}', "
            + $"'{ExecutionKey}', 1, '{ArtifactKind.RawReport}', '{key}', '{XmlType}', "
            + $"'{ReportChecksum}', 'normal', 'pending', '{state}', {ReportBody.LongLength}, "
            + (failureCode is null ? "null" : $"'{failureCode}'") + ")");

        return new Seeded(artifactId, key);
    }

    /// <summary>
    /// The final key the row's own coordinates mint, rendered exactly as <c>ArtifactKeyMinter.Mint</c> does and as the
    /// 0028 shape CHECK expects.
    /// </summary>
    /// <param name="world">The tenant and run the artifact belongs to.</param>
    /// <param name="projectId">The project the row names, which the key carries.</param>
    /// <param name="artifactId">The artifact's id.</param>
    private static string MintedKey(World world, Guid projectId, Guid artifactId) =>
        $"org/{world.Org:D}/project/{projectId:D}/run/{world.RunId:D}/attempt/{ExecutionKey}/1/{artifactId:D}";

    /// <summary>
    /// One <c>artifact_kind_policy</c> row for <c>raw_report</c> in the given project, so the handler's cap is the
    /// row's (the resolution takes the lower of row and default).
    /// </summary>
    /// <param name="world">The tenant the policy belongs to.</param>
    /// <param name="maxBytes">The row's cap.</param>
    /// <param name="projectId">The project the row names, or null for the run's own.</param>
    private Task SeedKindPolicyAsync(World world, long maxBytes, Guid? projectId = null) =>
        _fixture.ExecuteAsync(
            world.Database,
            "insert into public.artifact_kind_policy (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity) "
            + $"values ('{Guid.CreateVersion7():D}', '{world.Org:D}', '{projectId ?? world.Project:D}', '{ArtifactKind.RawReport}', "
            + $"{maxBytes.ToString(CultureInfo.InvariantCulture)}, array['{XmlType}'], 'normal')");


    /// <summary>
    /// The call the task consumer makes, minus the queue: the production handler over this tenant's scope, invoked
    /// through the same <c>TaskHandlerAdapter</c> the consumer uses, so the payload binding is the consumer's. A
    /// <paramref name="commit"/> of <see langword="false"/> disposes the scope uncommitted, which is the rollback a
    /// crashed worker performs. The scope it returns records what the attempt registered for its commit, which is how
    /// a test tells a deferred measurement from one the attempt took inside the open transaction.
    /// </summary>
    private static async Task<RecordingTenantScope> InvokeAsync(
        World world,
        Guid artifactId,
        StubParseRunner runner,
        CancellationToken? cancellationToken = null,
        ILogger<ParseArtifactTaskHandler>? logger = null,
        IOptions<IngestionLimits>? limits = null,
        bool commit = true)
    {
        var token = cancellationToken ?? Token;

        var handler = new ParseArtifactTaskHandler(
            world.Host.Services.GetRequiredService<ITenantDbContextFactory<EvidenceDbContext>>(),
            world.Host.Services.GetRequiredService<IObjectStorage>(),
            runner,
            limits ?? world.Host.Services.GetRequiredService<IOptions<IngestionLimits>>(),
            logger ?? NullLogger<ParseArtifactTaskHandler>.Instance);

        var invoker = new TaskHandlerAdapter<ParseArtifactPayload>(handler);
        invoker.TaskType.ShouldBe(ParseTask);

        var payload = JsonSerializer.Serialize(new { artifact_id = artifactId });
        var factory = world.Host.Services.GetRequiredService<ITenantScopeFactory>();

        var scope = new RecordingTenantScope(
            await factory.BeginAsync(world.Org, world.Project, cancellationToken: token));
        try
        {
            await invoker.InvokeAsync(payload, scope, token);

            if (commit)
            {
                await scope.CommitAsync(token);
            }
        }
        finally
        {
            await scope.DisposeAsync();
        }

        return scope;
    }

    /// <summary>One freeze of the world's run with no requirements, through the production service.</summary>
    private static async Task<FreezeResult> FreezeAsync(World world)
    {
        using var services = world.Host.Services.CreateScope();
        var freeze = services.ServiceProvider.GetRequiredService<IEvidenceFreeze>();
        var factory = world.Host.Services.GetRequiredService<ITenantScopeFactory>();

        await using var scope = await factory.BeginAsync(world.Org, world.Project, cancellationToken: Token);
        var result = await freeze.FreezeAsync(new FreezeRequest(world.RunId, []), Token);
        await scope.CommitAsync(Token);

        return result;
    }

    private Task<string?> ColumnAsync(World world, Guid artifactId, string column) =>
        _fixture.ScalarAsync<string?>(
            world.Database,
            $"select {column}::text from public.artifact where id = '{artifactId:D}'");

    private Task<int> VersionAsync(World world, Guid artifactId) =>
        _fixture.ScalarAsync<int>(world.Database, $"select version from public.artifact where id = '{artifactId:D}'");
}

/// <summary>
/// The one seam the handler adds, replaced: a runner whose answer the test states, which records every call and the
/// bytes it was handed, and which can block until the caller's cancellation to model a parse in flight.
/// </summary>
internal sealed class StubParseRunner : IParseRunner
{
    /// <summary>The result every call answers; <see langword="null"/> unless <see cref="Throws"/> is set.</summary>
    public ParseRunResult? Result { get; init; }

    /// <summary>
    /// The exception the run raises instead of answering — the shape a cancelled run has, where the runner has
    /// already tree-killed its child and rethrows <see cref="OperationCanceledException"/>.
    /// </summary>
    public Exception? Throws { get; init; }

    /// <summary>
    /// The work the test needs done while the child is "running" — a concurrent write to the artifact row from a second
    /// connection, which is how a lost race is modelled without a second worker.
    /// </summary>
    public Func<Task>? DuringRun { get; init; }

    /// <summary>How many runs were started.</summary>
    public int Calls { get; private set; }

    /// <summary>The input bytes of every run, in order: proof the handler handed over the stored report.</summary>
    public List<byte[]> Inputs { get; } = [];

    /// <summary>The entry cap of every run, in order: proof the handler handed over its configured cap.</summary>
    public List<long> MaxCases { get; } = [];

    /// <inheritdoc />
    public async Task<ParseRunResult> RunAsync(ParseRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Calls++;

        MaxCases.Add(request.MaxCases);

        using var buffer = new MemoryStream();
        await request.Input.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        Inputs.Add(buffer.ToArray());

        if (DuringRun is { } during)
        {
            await during().ConfigureAwait(false);
        }

        if (Throws is not null)
        {
            throw Throws;
        }

        if (Result is null)
        {
            throw new InvalidOperationException("the stub has no answer for this run");
        }

        return Result;
    }
}

/// <summary>
/// A logger that keeps every formatted line, so a test can assert what an operator would read: the level, the message
/// and its one structured line — never a provider's formatting, so the check is on the template the handler wrote.
/// </summary>
internal sealed class RecordingLogger : ILogger<ParseArtifactTaskHandler>
{
    /// <summary>Every line the handler logged, in order.</summary>
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        Entries.Add((logLevel, formatter(state, exception)));
    }
}
