// 06.2.3-T1 (AC2, AC3, AC5, AC6, AC7 and the P5 read rule) on the path the criteria name: the `artifact_secret_scan`
// handler over the real Api composition, a real PostgreSQL and a real SeaweedFS container. Only the logger is injected by
// the test, so the scope discipline, the event insert, the guarded write and the store read are the production ones.
using System.Globalization;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The <c>artifact_secret_scan</c> task handler against the shared migrated database and one SeaweedFS container: a
/// clean artifact becomes <c>clean</c>, a flagged one becomes <c>flagged</c> and <c>sensitive</c>, a rejecting project
/// moves it to <c>pending_delete</c> with <c>secret_rejected</c>, a replay writes no second event, an artifact of
/// another tenant is not scanned at all, and neither a row nor a log line carries the matched value.
/// </summary>
/// <remarks>
/// The scan reads real objects from the store, and the storage seam asserts that no tenant scope is open while it does
/// (00 §5.7 P5): the handler is registered transactionless for exactly that reason. What the queue does with a throwing
/// handler is the consumer's own half, in <c>EvidenceSecretScanTaskTests.Consumer.cs</c>.
/// </remarks>
[Trait("Category", "Docker")]
public sealed partial class EvidenceSecretScanTaskTests(PostgresTenancyFixture fixture, SeaweedFsStore store, TenancyHostPool hosts)
    : IClassFixture<SeaweedFsStore>, IClassFixture<TenancyHostPool>
{
    /// <summary>The task's wire name (06.2.3 §4).</summary>
    private const string ScanTask = "artifact_secret_scan";

    /// <summary>A well-formed execution key: sha256 hex (00 §7), the key the artifact's pair carries.</summary>
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>The media type the seeded artifacts declare.</summary>
    private const string XmlType = "application/xml";

    /// <summary>The AWS arm's prefix and a well-formed body, kept apart so no credential-shaped literal sits here.</summary>
    private const string AwsPrefix = "AK" + "IA";

    private const string AwsBody = "IOSFODNN7EXAMPL" + "E";

    /// <summary>A PEM header, split for the same reason.</summary>
    private const string PemHeader = "-----BEGIN RSA " + "PRIVATE KEY" + "-----";

    private static readonly byte[] CleanBody =
        "<report><testcase name=\"checkout\" outcome=\"passed\"/></report>"u8.ToArray();

    private readonly PostgresTenancyFixture _fixture = fixture;

    private readonly SeaweedFsStore _store = store;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed record World(
        TenancyApiFactory Host,
        string Database,
        Guid Org,
        Guid Project,
        Guid RunId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await Host.DisposeAsync();
    }

    /// <summary>One seeded row: an artifact whose bytes the store really holds.</summary>
    private sealed record Seeded(Guid ArtifactId, string ObjectKey);

    /// <summary>
    /// The composition the consumer sees: the scan's <c>task_type</c> resolves to exactly one handler, and that handler
    /// opens its own transactions. A registration through the scoped helper would still answer the type but would run
    /// the object read inside the consumer's transaction, which is the rule 00 §5.7 P5 forbids — so this asserts the
    /// flag rather than only the presence.
    /// </summary>
    [Fact]
    public async Task ScanTask_Resolves_To_One_Transactionless_Handler()
    {
        await using var world = await StartAsync();
        var invokers = world.Host.Services.GetServices<ITaskHandlerInvoker>()
            .Where(invoker => invoker.TaskType == ScanTask)
            .ToList();

        invokers.ShouldHaveSingleItem("one handler per task_type: the registry refuses a duplicate type");
        invokers[0].OpensOwnTransactions.ShouldBeTrue(
            "the scan reads the object with no transaction open, so it is registered transactionless");
        invokers[0].RequiresTenant.ShouldBeTrue("the scan is tenant-bound: task.org_id is its only tenant source");
    }

    /// <summary>AC3: an artifact whose bytes carry no class is scanned once, ends <c>clean</c> and writes no event.</summary>
    [Fact]
    public async Task Scan_CleanFixture_MarksClean_NoEvents()
    {
        await using var world = await StartAsync();
        var seeded = await SeedReadyAsync(world, CleanBody);
        var versionBefore = await VersionAsync(world, seeded.ArtifactId);

        await InvokeAsync(world, seeded.ArtifactId);

        (await ColumnAsync(world, seeded.ArtifactId, "scan_state")).ShouldBe(ArtifactScanState.Clean);
        (await ColumnAsync(world, seeded.ArtifactId, "sensitivity")).ShouldBe(
            ArtifactSensitivity.Normal, "a clean artifact keeps the sensitivity it was minted with");
        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.Ready);
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBeNull();
        (await EventCountAsync(world, seeded.ArtifactId)).ShouldBe(0, "nothing matched, so no class has a row");
        (await VersionAsync(world, seeded.ArtifactId)).ShouldBe(versionBefore + 1);
    }

    /// <summary>
    /// AC2: an artifact whose bytes carry two classes ends <c>flagged</c>, is raised to <c>sensitive</c>, stays evidence,
    /// and carries one event per class with the count the scan made.
    /// </summary>
    [Fact]
    public async Task Scan_PlantedAwsKey_FlagsAndRaisesSensitivity()
    {
        await using var world = await StartAsync();
        var body = BodyWith(AwsPrefix + AwsBody, PemHeader);
        var seeded = await SeedReadyAsync(world, body);

        await InvokeAsync(world, seeded.ArtifactId);

        (await ColumnAsync(world, seeded.ArtifactId, "scan_state")).ShouldBe(ArtifactScanState.Flagged);
        (await ColumnAsync(world, seeded.ArtifactId, "sensitivity")).ShouldBe(ArtifactSensitivity.Sensitive);
        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(
            ArtifactState.Ready, "a flagged artifact is still evidence unless the project rejects it");
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBeNull();

        var events = await EventsAsync(world, seeded.ArtifactId);
        events.ShouldBe(
            [
                (SecretPatternClass.AwsAccessKey, 1, MaskingEventAction.Flagged),
                (SecretPatternClass.PrivateKeyPem, 1, MaskingEventAction.Flagged),
            ]);
    }

    /// <summary>
    /// AC5: a project whose kind policy rejects on a secret moves the artifact <c>ready → pending_delete</c> with
    /// <c>secret_rejected</c>, raises it to <c>sensitive</c>, and records the class with the <c>rejected</c> action so
    /// the row and the state agree.
    /// </summary>
    [Fact]
    public async Task Scan_RejectOnSecretTrue_TransitionsReadyToPendingDelete()
    {
        await using var world = await StartAsync();
        await SeedKindPolicyAsync(world, rejectOnSecret: true);
        var seeded = await SeedReadyAsync(world, BodyWith(AwsPrefix + AwsBody));

        await InvokeAsync(world, seeded.ArtifactId);

        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.PendingDelete);
        (await ColumnAsync(world, seeded.ArtifactId, "failure_code")).ShouldBe(ArtifactFailureCode.SecretRejected);
        (await ColumnAsync(world, seeded.ArtifactId, "scan_state")).ShouldBe(ArtifactScanState.Flagged);
        (await ColumnAsync(world, seeded.ArtifactId, "sensitivity")).ShouldBe(ArtifactSensitivity.Sensitive);
        (await EventsAsync(world, seeded.ArtifactId)).ShouldBe(
            [(SecretPatternClass.AwsAccessKey, 1, MaskingEventAction.Rejected)]);
    }

    /// <summary>
    /// AC6: a second attempt of the same row writes no second event and does not move the artifact again — the replay
    /// is the no-op of a decided row, whatever the scan would find now.
    /// </summary>
    [Fact]
    public async Task Scan_ReplayedOutboxRow_NoDuplicateEvents()
    {
        await using var world = await StartAsync();
        var seeded = await SeedReadyAsync(world, BodyWith(AwsPrefix + AwsBody));

        await InvokeAsync(world, seeded.ArtifactId);
        var versionAfterFirst = await VersionAsync(world, seeded.ArtifactId);

        await InvokeAsync(world, seeded.ArtifactId);

        (await EventCountAsync(world, seeded.ArtifactId)).ShouldBe(1);
        (await VersionAsync(world, seeded.ArtifactId)).ShouldBe(
            versionAfterFirst, "a replayed attempt commits nothing at all");
    }

    /// <summary>
    /// A rejection followed by a replay of the same row leaves one row per class whose action still says
    /// <c>rejected</c>: a class never carries two disagreeing events, so a replay can neither add <c>flagged</c> beside
    /// <c>rejected</c> nor rewrite the first finding.
    /// </summary>
    [Fact]
    public async Task Scan_Replay_After_Rejection_Keeps_One_Row_Per_Class()
    {
        await using var world = await StartAsync();
        await SeedKindPolicyAsync(world, rejectOnSecret: true);
        var seeded = await SeedReadyAsync(world, BodyWith(AwsPrefix + AwsBody));

        await InvokeAsync(world, seeded.ArtifactId);
        await InvokeAsync(world, seeded.ArtifactId);

        (await EventsAsync(world, seeded.ArtifactId)).ShouldBe(
            [(SecretPatternClass.AwsAccessKey, 1, MaskingEventAction.Rejected)]);
        (await ColumnAsync(world, seeded.ArtifactId, "state")).ShouldBe(ArtifactState.PendingDelete);
    }

    /// <summary>
    /// An artifact that is not <c>ready</c> is not this task's work: a row still uploading has no final object, and a
    /// row already leaving evidence is not scanned for bytes that are about to be deleted.
    /// </summary>
    [Fact]
    public async Task Scan_Not_Ready_Artifact_Is_A_No_Op()
    {
        await using var world = await StartAsync();
        var seeded = await SeedAsync(world, ArtifactState.PendingDelete, BodyWith(AwsPrefix + AwsBody), storeBody: true);

        await InvokeAsync(world, seeded.ArtifactId);

        (await ColumnAsync(world, seeded.ArtifactId, "scan_state")).ShouldBe(ArtifactScanState.Pending);
        (await EventCountAsync(world, seeded.ArtifactId)).ShouldBe(0);
    }

    /// <summary>
    /// AC7: the task's tenant is the row's, so a payload naming another org's artifact finds no row at all and writes
    /// nothing — the other org's artifact stays pending, with no event, and this attempt is a no-op rather than a
    /// cross-tenant read.
    /// </summary>
    [Fact]
    public async Task Scan_Another_Tenants_Artifact_Is_Not_Reached()
    {
        await using var owner = await StartAsync();
        var seeded = await SeedReadyAsync(owner, BodyWith(AwsPrefix + AwsBody));
        await using var other = await StartAsync();

        await InvokeAsync(other, seeded.ArtifactId);

        (await ColumnAsync(owner, seeded.ArtifactId, "scan_state")).ShouldBe(ArtifactScanState.Pending);
        (await EventCountAsync(owner, seeded.ArtifactId)).ShouldBe(0);
    }

    /// <summary>
    /// 00 §5.7 P5: the object read runs with no tenant scope open. The storage seam asserts it, so a handler that read
    /// inside the consumer's transaction (or kept its read scope open across the call) fails here.
    /// </summary>
    [Fact]
    public async Task Scan_Reads_The_Object_With_No_Tenant_Scope_Open()
    {
        await using var world = await StartAsync();
        var seeded = await SeedReadyAsync(world, BodyWith(AwsPrefix + AwsBody));
        var accessor = world.Host.Services.GetRequiredService<ITenantScopeAccessor>();
        var storage = new ScopeAssertingStorage(world.Host.Services.GetRequiredService<IObjectStorage>(), accessor);

        await InvokeAsync(world, seeded.ArtifactId, storage);

        storage.Reads.ShouldBe(1, "the assertion must have run for the read that produced the verdict");
        (await ColumnAsync(world, seeded.ArtifactId, "scan_state")).ShouldBe(ArtifactScanState.Flagged);
    }

    /// <summary>
    /// AC7 second half: nothing stored or logged carries the matched value. The events hold the class and the count,
    /// the artifact keeps its own columns, and the handler's log line names the artifact and the classes only.
    /// </summary>
    [Fact]
    public async Task MaskingEvent_NeverStoresMatchedSecretValue()
    {
        await using var world = await StartAsync();
        var key = AwsPrefix + AwsBody;
        var seeded = await SeedReadyAsync(world, BodyWith(key, PemHeader));
        var logger = new CapturingLogger<RunArtifactSecretScanHandler>();

        await InvokeAsync(world, seeded.ArtifactId, logger: logger);

        logger.Messages.ShouldNotBeEmpty("the attempt logs its verdict");
        foreach (var message in logger.Messages)
        {
            message.ShouldNotContain(key, Case.Sensitive);
            message.ShouldNotContain(AwsBody, Case.Sensitive);
        }

        var stored = await _fixture.ScalarAsync<string?>(
            world.Database,
            "select coalesce(string_agg(pattern_class || ':' || hit_count || ':' || action, ',' order by pattern_class), '') "
            + $"from public.masking_event where artifact_id = '{seeded.ArtifactId:D}'");
        stored.ShouldNotBeNull();
        stored.ShouldNotContain(AwsBody, Case.Sensitive);
        stored.ShouldBe($"{SecretPatternClass.AwsAccessKey}:1:{MaskingEventAction.Flagged},"
            + $"{SecretPatternClass.PrivateKeyPem}:1:{MaskingEventAction.Flagged}");
    }

    // Harness: the tenant, the artifact row and the object behind it.

    /// <summary>
    /// One tenant on the shared database, or on <paramref name="database"/> when a test needs a database of its own, with
    /// <paramref name="services"/> applied to the host: the queue mechanism is composed that way by the test that drives
    /// the real consumer, and by no other, because a consumer claims rows of every type in the database it polls.
    /// </summary>
    /// <param name="database">The database to use; the shared one when null.</param>
    /// <param name="services">Extra registrations for this host, such as the task queue.</param>
    private async Task<World> StartAsync(string? database = null, Action<IServiceCollection>? services = null)
    {
        database ??= await _fixture.SharedDatabaseAsync();
        // Dedicated consumer databases deliberately receive private leases.
        await using var api = hosts.RealResolver(database);
        Guid user;
        Guid org;
        Guid project;

        using (var setup = api.CreateClient())
        {
            user = Guid.CreateVersion7();
            org = await CreateOrgAsync(setup, user, Unique.Slug("acme"));
            project = await CreateProjectAsync(setup, user, org, Unique.Slug("checkout"));
        }

        var principal = RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: "jti-scan-1");
        // Queue consumers carry per-test registrations and poll their dedicated database.
        var host = services is null
            ? hosts.WithPrincipalConfigured(database, org, project, principal, _store.Configuration())
            : TenancyApiFactory.WithPrincipal(
                _fixture.AppConnectionString(database), org, project, principal, _store.Configuration(), services);

        var runId = await RunRowSeeder.SeedRunAsync(_fixture, database, org);

        return new World(host, database, org, project, runId);
    }

    /// <summary>The call the task consumer makes, minus the queue: the production handler, bound through the adapter.</summary>
    /// <param name="world">The tenant the claimed row belongs to.</param>
    /// <param name="artifactId">The artifact the payload names.</param>
    /// <param name="storage">The storage seam to use; the host's own adapter when null.</param>
    /// <param name="logger">The logger to capture into; a discarding one when null.</param>
    private static async Task InvokeAsync(
        World world,
        Guid artifactId,
        IObjectStorage? storage = null,
        ILogger<RunArtifactSecretScanHandler>? logger = null)
    {
        var handler = new RunArtifactSecretScanHandler(
            world.Host.Services.GetRequiredService<ITenantScopeFactory>(),
            world.Host.Services.GetRequiredService<ITenantDbContextFactory<EvidenceDbContext>>(),
            world.Host.Services.GetRequiredService<ITenantDb>(),
            storage ?? world.Host.Services.GetRequiredService<IObjectStorage>(),
            world.Host.Services.GetRequiredService<IParsing>(),
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RunArtifactSecretScanHandler>.Instance);

        var invoker = new TransactionlessTaskHandlerAdapter<RunArtifactSecretScanPayload>(handler);
        invoker.TaskType.ShouldBe(ScanTask);

        var payload = JsonSerializer.Serialize(new { artifact_id = artifactId });
        await invoker.InvokeWithoutScopeAsync(payload, world.Org, Token);
    }

    /// <summary>A report body carrying the given samples as the object's own bytes.</summary>
    /// <param name="samples">The secret-shaped samples to place in the body.</param>
    private static byte[] BodyWith(params string[] samples) =>
        Encoding.UTF8.GetBytes("<report>" + string.Join(' ', samples) + "</report>");

    private Task<string?> ColumnAsync(World world, Guid artifactId, string column) =>
        _fixture.ScalarAsync<string?>(
            world.Database,
            $"select {column}::text from public.artifact where id = '{artifactId:D}'");

    private Task<int> VersionAsync(World world, Guid artifactId) =>
        _fixture.ScalarAsync<int>(world.Database, $"select version from public.artifact where id = '{artifactId:D}'");

    private Task<long> EventCountAsync(World world, Guid artifactId) =>
        _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.masking_event where artifact_id = '{artifactId:D}'");

    /// <summary>The artifact's events as (class, count, action), ordered by class.</summary>
    /// <param name="world">The tenant whose database is read.</param>
    /// <param name="artifactId">The artifact whose events are read.</param>
    private async Task<IReadOnlyList<(string Class, int HitCount, string Action)>> EventsAsync(
        World world,
        Guid artifactId)
    {
        var rows = await _fixture.ColumnAsync<string>(
            world.Database,
            "select pattern_class || '|' || hit_count || '|' || action from public.masking_event "
            + $"where artifact_id = '{artifactId:D}' order by pattern_class");

        return rows
            .Select(row => row.Split('|'))
            .Select(parts => (parts[0], int.Parse(parts[1], CultureInfo.InvariantCulture), parts[2]))
            .ToList();
    }

    /// <summary>
    /// The object store with the P5 rule made an assertion: every read must happen while no tenant scope is open, so a
    /// handler that keeps its read scope across the call fails instead of passing unnoticed.
    /// </summary>
    private sealed class ScopeAssertingStorage(IObjectStorage inner, ITenantScopeAccessor accessor) : IObjectStorage
    {
        /// <summary>How many whole-object reads this seam served.</summary>
        public int Reads { get; private set; }

        public PresignedPut PresignPut(PresignPutRequest request) => inner.PresignPut(request);

        public PresignedGet PresignGet(string objectKey) => inner.PresignGet(objectKey);

        public Task<ObjectHead?> HeadAsync(string objectKey, CancellationToken cancellationToken) =>
            inner.HeadAsync(objectKey, cancellationToken);

        public Task<byte[]?> ReadPrefixAsync(
            string objectKey,
            int maxBytes,
            CancellationToken cancellationToken,
            string? ifMatch = null) =>
            inner.ReadPrefixAsync(objectKey, maxBytes, cancellationToken, ifMatch);

        public Task<byte[]?> GetAsync(string objectKey, long maxBytes, CancellationToken cancellationToken)
        {
            accessor.Current.ShouldBeNull(
                "00 §5.7 P5: the object read must run with no tenant transaction open");
            Reads++;
            return inner.GetAsync(objectKey, maxBytes, cancellationToken);
        }

        public Task CopyAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken) =>
            inner.CopyAsync(sourceKey, destinationKey, cancellationToken);

        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) =>
            inner.DeleteAsync(objectKey, cancellationToken);
    }

    /// <summary>An <see cref="ILogger{TCategoryName}"/> that keeps what it was told, so a test can assert on it.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        /// <summary>The rendered messages this logger received, in order.</summary>
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Messages.Add(formatter(state, exception));
        }
    }
}
