using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Evidence.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class SecretScanConsumerTests(PostgresTenancyFixture fixture, SeaweedFsStore store)
    : IClassFixture<SeaweedFsStore>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string PlantedKey => string.Concat("AK", "IA", new string('Q', 16));
    private static byte[] FlaggedBody => Encoding.UTF8.GetBytes("<report> " + PlantedKey + " </report>");

    [Fact]
    public async Task Consumer_OutboxDerivedTask_ClaimsRegisteredHandlerAndPersistsDone()
    {
        await using var world = await StartAsync();
        var artifact = await SeedAsync(world, FlaggedBody);
        Guid outbox;
        await using (var scope = await world.Host.Services.GetRequiredService<ITenantScopeFactory>()
            .BeginAsync(world.Org, projectId: null, readOnlyTransaction: false, Token))
        {
            outbox = await world.Host.Services.GetRequiredService<IOutboxWriter>().EnqueueAsync(
                "artifact_secret_scan", JsonSerializer.Serialize(new { artifact_id = artifact }),
                "artifact", artifact, artifact.ToString("D"), Token);
            await scope.CommitAsync(Token);
        }
        (await ArtifactValueAsync<string>(world, artifact, "scan_state")).ShouldBe("pending");
        await world.Host.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(Token);
        var task = await fixture.ScalarAsync<Guid>(world.Database,
            $"select id from public.task where idempotency_key='outbox:{outbox:D}'");
        var queue = await TaskValueAsync<string>(world, task, "queue");
        (await world.Host.Services.GetRequiredService<TaskConsumer>().PollOnceAsync(queue, Token)).ShouldBeGreaterThan(0);
        (await TaskValueAsync<string>(world, task, "state")).ShouldBe("done");
        (await ArtifactValueAsync<string>(world, artifact, "scan_state")).ShouldBe("flagged");
    }

    [Fact]
    public async Task Consumer_MissingObject_PersistsRetryThenDeadLetterWithoutVerdict()
    {
        await using var world = await StartAsync();
        var artifact = await SeedAsync(world, "<report/>"u8.ToArray(), upload: false);
        var (task, queue) = await EnqueueAsync(world, artifact);
        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();

        (await consumer.PollOnceAsync(queue, Token)).ShouldBe(1);
        (await TaskValueAsync<string>(world, task, "state")).ShouldBe("retry");
        (await TaskValueAsync<int>(world, task, "attempts")).ShouldBe(1);
        (await TaskValueAsync<bool>(world, task, "locked_until is null and available_at > now()")).ShouldBeTrue();
        (await consumer.PollOnceAsync(queue, Token)).ShouldBe(0);
        await fixture.ExecuteAsync(world.Database, $"update public.task set available_at=now() where id='{task:D}'");
        (await consumer.PollOnceAsync(queue, Token)).ShouldBe(1);
        (await TaskValueAsync<string>(world, task, "state")).ShouldBe("dead_letter");
        (await TaskValueAsync<int>(world, task, "attempts")).ShouldBe(2);
        (await TaskValueAsync<string>(world, task, "last_error")).ShouldContain("absent");
        (await ArtifactValueAsync<string>(world, artifact, "scan_state")).ShouldBe("pending");
        (await EventCountAsync(world, artifact)).ShouldBe(0);
    }

    [Fact]
    public async Task Consumer_CleanObject_PersistsDoneAndClean()
    {
        await using var world = await StartAsync();
        var artifact = await SeedAsync(world, "<report/>"u8.ToArray());
        var (task, queue) = await EnqueueAsync(world, artifact);
        (await world.Host.Services.GetRequiredService<TaskConsumer>().PollOnceAsync(queue, Token)).ShouldBe(1);
        (await TaskValueAsync<string>(world, task, "state")).ShouldBe("done");
        (await ArtifactValueAsync<string>(world, artifact, "scan_state")).ShouldBe("clean");
        (await ArtifactValueAsync<string>(world, artifact, "sensitivity")).ShouldBe("normal");
        (await EventCountAsync(world, artifact)).ShouldBe(0);
    }

    [Fact]
    public async Task Consumer_FlaggedObjectReplay_DoesNotWriteTwiceOrLeakMatchedValue()
    {
        await using var world = await StartAsync();
        var artifact = await SeedAsync(world, FlaggedBody);
        var (first, firstQueue) = await EnqueueAsync(world, artifact);
        var consumer = world.Host.Services.GetRequiredService<TaskConsumer>();
        (await consumer.PollOnceAsync(firstQueue, Token)).ShouldBe(1);
        (await TaskValueAsync<string>(world, first, "state")).ShouldBe("done");
        var version = await ArtifactValueAsync<int>(world, artifact, "version");
        var (second, secondQueue) = await EnqueueAsync(world, artifact);
        (await consumer.PollOnceAsync(secondQueue, Token)).ShouldBe(1);
        (await TaskValueAsync<string>(world, second, "state")).ShouldBe("done");
        (await ArtifactValueAsync<int>(world, artifact, "version")).ShouldBe(version);
        (await ArtifactValueAsync<string>(world, artifact, "scan_state")).ShouldBe("flagged");
        (await ArtifactValueAsync<string>(world, artifact, "sensitivity")).ShouldBe("sensitive");
        (await ArtifactValueAsync<string>(world, artifact, "state")).ShouldBe("ready");
        (await EventCountAsync(world, artifact)).ShouldBe(1);
        var rows = await fixture.ScalarAsync<string>(world.Database,
            $"select string_agg(row_to_json(e)::text, ',') from public.masking_event e where artifact_id='{artifact:D}'");
        rows.ShouldNotContain(PlantedKey);
        world.Logs.Messages.ShouldNotBeEmpty();
        string.Join('\n', world.Logs.Messages).ShouldNotContain(PlantedKey);
    }

    [Fact]
    public async Task Consumer_RejectPolicy_PersistsLegalTransitionAndRejectedAction()
    {
        await using var world = await StartAsync();
        await PolicyAsync(world, ArtifactKind.RawReport, reject: true, maxBytes: 1048576);
        var artifact = await SeedAsync(world, FlaggedBody);
        var (task, queue) = await EnqueueAsync(world, artifact);
        (await world.Host.Services.GetRequiredService<TaskConsumer>().PollOnceAsync(queue, Token)).ShouldBe(1);
        (await TaskValueAsync<string>(world, task, "state")).ShouldBe("done");
        (await ArtifactValueAsync<string>(world, artifact, "state")).ShouldBe("pending_delete");
        (await ArtifactValueAsync<string>(world, artifact, "failure_code")).ShouldBe("secret_rejected");
        (await ArtifactValueAsync<string>(world, artifact, "scan_state")).ShouldBe("flagged");
        (await fixture.ScalarAsync<string>(world.Database,
            $"select action from public.masking_event where artifact_id='{artifact:D}'")).ShouldBe("rejected");
    }

    [Fact]
    public async Task Consumer_CrossOrgPayload_LeavesForeignArtifactUntouchedWithPositiveControl()
    {
        await using var owner = await StartAsync();
        await using var other = await StartAsync();
        var artifact = await SeedAsync(owner, FlaggedBody);
        var (foreignTask, foreignQueue) = await EnqueueAsync(other, artifact);
        (await other.Host.Services.GetRequiredService<TaskConsumer>().PollOnceAsync(foreignQueue, Token)).ShouldBe(1);
        (await TaskValueAsync<string>(other, foreignTask, "state")).ShouldBe("done");
        (await ArtifactValueAsync<string>(owner, artifact, "scan_state")).ShouldBe("pending");
        (await EventCountAsync(owner, artifact)).ShouldBe(0);
        var (_, ownerQueue) = await EnqueueAsync(owner, artifact);
        (await owner.Host.Services.GetRequiredService<TaskConsumer>().PollOnceAsync(ownerQueue, Token)).ShouldBe(1);
        (await ArtifactValueAsync<string>(owner, artifact, "scan_state")).ShouldBe("flagged");
        (await EventCountAsync(owner, artifact)).ShouldBe(1);
    }

    [Fact]
    public async Task Consumer_OversizedObject_RetriesWithoutCleanVerdict()
    {
        await using var world = await StartAsync();
        await PolicyAsync(world, ArtifactKind.RawReport, reject: false, maxBytes: 1024);
        var artifact = await SeedAsync(world, new byte[1025]);
        var (task, queue) = await EnqueueAsync(world, artifact);
        (await world.Host.Services.GetRequiredService<TaskConsumer>().PollOnceAsync(queue, Token)).ShouldBe(1);
        (await TaskValueAsync<string>(world, task, "state")).ShouldBe("retry");
        (await ArtifactValueAsync<string>(world, artifact, "scan_state")).ShouldBe("pending");
        (await EventCountAsync(world, artifact)).ShouldBe(0);
    }

    [Fact]
    public async Task Consumer_CompressedTraceWithSecret_DetectsAndRejectsSecret()
    {
        await using var world = await StartAsync();
        await PolicyAsync(world, "trace", reject: true, maxBytes: 1048576);
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var entry = await archive.CreateEntry("trace.trace", CompressionLevel.SmallestSize).OpenAsync(Token);
            await entry.WriteAsync(FlaggedBody, Token);
        }
        var zipped = buffer.ToArray();
        SecretPatternScanner.Scan(FlaggedBody, Token).ShouldHaveSingleItem();
        var artifact = await SeedAsync(world, zipped, kind: "trace", mediaType: "application/zip");
        var (task, queue) = await EnqueueAsync(world, artifact);
        (await world.Host.Services.GetRequiredService<TaskConsumer>().PollOnceAsync(queue, Token)).ShouldBe(1);
        (await TaskValueAsync<string>(world, task, "state")).ShouldBe("done");
        (await ArtifactValueAsync<string>(world, artifact, "scan_state")).ShouldBe("flagged");
        (await ArtifactValueAsync<string>(world, artifact, "state")).ShouldBe("pending_delete");
    }

    private async Task<World> StartAsync()
    {
        var database = await fixture.SharedDatabaseAsync();
        await using var setupHost = TenancyApiFactory.RealResolver(fixture.AppConnectionString(database));
        using var setup = setupHost.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(setup, user, Unique.Slug("scan"));
        var project = await CreateProjectAsync(setup, user, org, Unique.Slug("scan"));
        var logs = new CaptureLogs();
        var host = TenancyApiFactory.WithPrincipal(fixture.AppConnectionString(database), org, project,
            RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: "scan-test"),
            store.Configuration(), services =>
            {
                services.AddEvidgateTasks(new ConfigurationBuilder().Build());
                services.AddLogging(logging => logging.AddProvider(logs));
            });
        var run = await RunRowSeeder.SeedRunAsync(fixture, database, org);
        return new World(host, database, org, project, run, logs);
    }

    private async Task<Guid> SeedAsync(World world, byte[] bytes, bool upload = true,
        string kind = ArtifactKind.RawReport, string mediaType = "application/xml")
    {
        var id = Guid.CreateVersion7();
        var execution = new string('b', 64);
        var key = $"org/{world.Org:D}/project/{world.Project:D}/run/{world.Run:D}/attempt/{execution}/1/{id:D}";
        if (upload)
        {
            await store.PutAsync(key, bytes);
        }
        await fixture.ExecuteAsync(world.Database,
            "insert into public.artifact (id,org_id,project_id,run_id,execution_key,attempt_no,kind,object_key,media_type,checksum,sensitivity,scan_state,state,bytes) "
            + $"values ('{id:D}','{world.Org:D}','{world.Project:D}','{world.Run:D}','{execution}',1,'{kind}','{key}','{mediaType}',"
            + $"'sha256:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))}','normal','pending','ready',{bytes.Length})");
        return id;
    }

    private Task PolicyAsync(World world, string kind, bool reject, long maxBytes) =>
        fixture.ExecuteAsync(world.Database,
            "insert into public.artifact_kind_policy (id,org_id,project_id,kind,max_bytes,media_types,default_sensitivity,reject_on_secret) "
            + $"values ('{Guid.CreateVersion7():D}','{world.Org:D}','{world.Project:D}','{kind}',{maxBytes},"
            + $"array['application/xml','application/zip'],'normal',{reject.ToString().ToLowerInvariant()})");

    private static async Task<(Guid Task, string Queue)> EnqueueAsync(World world, Guid artifact)
    {
        var queue = "scan-" + Guid.NewGuid().ToString("N");
        var id = await world.Host.Services.GetRequiredService<ITaskQueueWriter>().EnqueueAsync(new TaskEnqueue
        {
            Queue = queue,
            TaskType = "artifact_secret_scan",
            OrgId = world.Org,
            PayloadJson = JsonSerializer.Serialize(new { artifact_id = artifact }),
            IdempotencyKey = queue,
            MaxAttempts = 2,
        }, Token);
        id.ShouldNotBeNull();
        return (id.Value, queue);
    }

    private Task<T> ArtifactValueAsync<T>(World world, Guid id, string expression) =>
        fixture.ScalarAsync<T>(world.Database, $"select {expression} from public.artifact where id='{id:D}'");

    private Task<T> TaskValueAsync<T>(World world, Guid id, string expression) =>
        fixture.ScalarAsync<T>(world.Database, $"select {expression} from public.task where id='{id:D}'");

    private Task<long> EventCountAsync(World world, Guid artifact) => fixture.ScalarAsync<long>(world.Database,
        $"select count(*) from public.masking_event where artifact_id='{artifact:D}'");

    private sealed record World(TenancyApiFactory Host, string Database, Guid Org, Guid Project, Guid Run, CaptureLogs Logs) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private sealed class CaptureLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(Messages);
        public void Dispose() { }
    }

    private sealed class CaptureLogger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            messages.Enqueue(formatter(state, exception));
    }
}
