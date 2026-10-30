// 06.3.6-T1.4 on the queue half of AC8 ("parsed once"): the parse task through the real outbox hop, the real task
// queue, a real PostgreSQL and a real SeaweedFS container. The runner is replaced by a fake that models one attempt
// cancelled mid-parse, so the test drives the production consumer, lease and guard code without spawning a child.
using System.Text.Json;
using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// <c>WorkerKilledMidParse_NoOrphanChild_TaskRetried_ParsedOnce</c> (06.3.6-T1.4, AC8): an attempt whose worker goes
/// away mid-parse leaves the artifact untouched, the queue makes the row runnable again, the second attempt writes the
/// verdict, and the artifact carries exactly one terminal write whatever the number of attempts.
/// </summary>
/// <remarks>
/// <b>What is real:</b> the outbox row, the dispatcher hop, the task row, the claim/lease, the consumer's cancellation
/// handling, the tenant scope, the guarded <c>UPDATE</c> and the artifact row.
/// <b>What is not:</b> the child process. <see cref="BlockingParseRunner"/> stands in for the runner, so the evidence
/// that the child is gone is the runner's own cancellation being observed and its token never being ignored — proving
/// the runner is cancelled and its input disposed, not that an OS process was reaped. The real child teardown
/// (tree kill, temp-directory removal) is 06.3.6-T1.3's <c>ChildProcessParseRunner</c> test surface, which runs on
/// Linux only; this test deliberately does not claim it.
/// </remarks>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class EvidenceParseRetryTests(PostgresTenancyFixture fixture, SeaweedFsStore store)
    : IClassFixture<SeaweedFsStore>
{
    private const string ExecutionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string XmlType = "application/xml";

    private const string ParseTask = "artifact_parse";

    private static readonly byte[] ReportBody =
        "<report><testcase name=\"checkout\" outcome=\"passed\"/></report>"u8.ToArray();

    private readonly PostgresTenancyFixture _fixture = fixture;

    private readonly SeaweedFsStore _store = store;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WorkerKilledMidParse_NoOrphanChild_TaskRetried_ParsedOnce()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(_fixture.AppConnectionString(database));
        Guid org;
        Guid project;

        using (var setup = api.CreateClient())
        {
            var user = Guid.CreateVersion7();
            org = await CreateOrgAsync(setup, user, "acme");
            project = await CreateProjectAsync(setup, user, org, "checkout");
        }

        var runner = new BlockingParseRunner();
        var settings = _store.Configuration();
        settings["EVIDGATE:PARSE:OUTOFPROCESS"] = "true";

        // One second of visibility and one second of backoff: a failed (here: cancelled) attempt parks the row in
        // `retry` with `available_at = now() + backoff`, and the claimer only takes due rows — so the redelivery below
        // is the queue's own path, a second after the attempt failed, and not the test inserting a task row.
        settings["EVIDGATE:Tasks:VisibilityTimeoutSeconds"] = "1";
        settings["EVIDGATE:Tasks:BackoffBaseSeconds"] = "1";
        settings["EVIDGATE:Tasks:BackoffMaxSeconds"] = "1";

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        await using var host = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(database),
            org,
            project,
            RequestPrincipal.Service(projectScope: null, [Permissions.Artifact.Write], credentialId: "jti-parse-retry"),
            settings,
            services =>
            {
                // The Api host composes neither the queue mechanism nor the out-of-process runner (both are the
                // Worker's), so the test registers the mechanism here over the host's own app-role data source — the
                // composition ReconcileRunTests and IngestTraceCorrelationTests use. It also binds the options set
                // above (one second of visibility and backoff), so the redelivery below is the queue's own path.
                services.AddEvidgateTasks(configuration);

                // The extension registers the handler and its queue route, so the consumer can claim the row at all;
                // the fake replaces the runner it registered (the last registration wins) so the attempt can be
                // cancelled mid-run without spawning a child.
                services.AddParseRunner(configuration);
                services.AddSingleton<IParseRunner>(runner);
            });

        var runId = await RunRowSeeder.SeedRunAsync(_fixture, database, org);
        var artifactId = await SeedReadyAsync(database, org, project, runId);

        // The outbox row the completion writes, carried through the real dispatcher (the hop the API host never
        // runs) so the task row is the queue's own, with the queue the dispatcher routed it to.
        await InsertParseOutboxAsync(database, org, artifactId);

        using var dispatcher = new OutboxDispatcher(
            host.Services.GetRequiredService<NpgsqlDataSource>(),
            Options.Create(new TasksOptions()),
            NullLogger<OutboxDispatcher>.Instance);

        (await dispatcher.DispatchBatchAsync(Token)).Enqueued.ShouldBe(1);

        var taskId = await _fixture.ScalarAsync<Guid>(
            database, $"select id from public.task where task_type = '{ParseTask}'");
        var queue = await _fixture.ScalarAsync<string>(
            database, $"select queue from public.task where id = '{taskId:D}'");

        var consumer = host.Services.GetRequiredService<TaskConsumer>();

        // ---- attempt 1: the worker goes away while the child is still running --------------------
        using (var workerGone = new CancellationTokenSource())
        {
            // The fake cancels the consumer's token from inside the run — the host stopping mid-parse. The consumer
            // releases a cancelled handler (state `queued`, attempt given back) instead of judging the artifact,
            // which is what this asserts.
            using var stop = workerGone;
            runner.CancelAfterStart = stop;

            (await consumer.PollOnceAsync(queue, stop.Token))
                .ShouldBe(1, "the cancelled attempt was claimed and released, not skipped");
        }

        // The fake's evidence, not an OS fact: the runner saw the cancellation and stopped; that the real child was
        // reaped is 06.3.6-T1.3's Linux-only teardown surface, and this test does not claim it.
        runner.ObservedCancellation.ShouldBeTrue("the runner must see the cancellation, never run past its lease");
        runner.Calls.ShouldBe(1);
        (await StateAsync(database, artifactId)).ShouldBe("ready", "a cancelled attempt is not a verdict");
        (await FailureCodeAsync(database, artifactId)).ShouldBeNull();

        // ---- the redelivery: the row is runnable again and the second attempt finishes -----------
        await WaitForRunnableAsync(database, taskId);

        runner.NextResult = new ParseRunFailed(ParseRunFailureKind.ExitedAbnormally, 152, 0, 0);

        (await consumer.PollOnceAsync(queue, Token)).ShouldBe(1, "the redelivered row runs once more");

        runner.Calls.ShouldBe(2);

        // The queue's own record: a host stop gives its attempt back, so the redelivered run is attempt one.
        (await _fixture.ScalarAsync<int>(database, $"select attempts from public.task where id = '{taskId:D}'"))
            .ShouldBe(1, "the cancelled attempt was released, not lost and not charged");
        (await _fixture.ScalarAsync<string>(database, $"select state from public.task where id = '{taskId:D}'"))
            .ShouldBe("done", "the redelivered attempt finished the row");

        (await StateAsync(database, artifactId)).ShouldBe("rejected");
        (await FailureCodeAsync(database, artifactId)).ShouldBe(ParseRunRejection.Timeout);

        // Exactly one terminal write: the guard's own evidence is the version, which the row's trigger increments once
        // per written statement, so a second write would show up here.
        (await _fixture.ScalarAsync<int>(database, $"select version from public.artifact where id = '{artifactId:D}'"))
            .ShouldBe(2, "one guarded UPDATE reached the row, whatever the number of attempts");

        // The artifact is decided now, so a third delivery parses nothing.
        (await host.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(Token)).Enqueued.ShouldBe(0);
        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.task where task_type = '{ParseTask}' and state = 'queued'"))
            .ShouldBe(0, "no second parse row was ever scheduled");
    }

    /// <summary>One ready <c>raw_report</c> artifact whose bytes the store holds at its final key.</summary>
    private async Task<Guid> SeedReadyAsync(string database, Guid org, Guid project, Guid runId)
    {
        var artifactId = Guid.CreateVersion7();
        var objectKey = $"org/{org:D}/project/{project:D}/run/{runId:D}/attempt/{ExecutionKey}/1/{artifactId:D}";

        await _store.PutAsync(objectKey, ReportBody);

        await _fixture.ExecuteAsync(
            database,
            "insert into public.artifact (id, org_id, project_id, run_id, execution_key, attempt_no, kind, object_key, media_type, checksum, sensitivity, scan_state, state, bytes) "
            + $"values ('{artifactId:D}', '{org:D}', '{project:D}', '{runId:D}', '{ExecutionKey}', 1, 'raw_report', "
            + $"'{objectKey}', '{XmlType}', '{ParseArtifactTaskTestsChecksum()}', 'normal', 'pending', 'ready', {ReportBody.LongLength})");

        return artifactId;
    }

    private static string ParseArtifactTaskTestsChecksum() =>
        "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(ReportBody));

    /// <summary>The exact row <c>CompleteArtifactUpload.EnqueueAsync</c> writes for a raw report.</summary>
    private Task InsertParseOutboxAsync(string database, Guid org, Guid artifactId) =>
        _fixture.ExecuteAsync(
            database,
            "insert into public.outbox (id, org_id, aggregate_type, aggregate_id, task_type, payload, idempotency_key) "
            + $"values ('{Guid.CreateVersion7()}', '{org:D}', 'artifact', '{artifactId:D}', '{ParseTask}', "
            + $"'{JsonSerializer.Serialize(new { artifact_id = artifactId })}'::jsonb, 'evidence:artifact:{artifactId:D}:parse')");

    /// <summary>
    /// Waits for the queue to make the row runnable again once the attempt's lease is gone — the redelivery the test
    /// depends on. Polled with a bounded budget, so a queue that never releases fails the test instead of hanging it.
    /// </summary>
    /// <param name="database">The migrated database.</param>
    /// <param name="taskId">The claimed task row.</param>
    private async Task WaitForRunnableAsync(string database, Guid taskId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var runnable = await _fixture.ScalarAsync<bool>(
                database,
                $"select state in ('queued', 'retry') and available_at <= now() from public.task where id = '{taskId:D}'");

            if (runnable)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), Token);
        }

        throw new Shouldly.ShouldAssertException(
            $"task {taskId:D} never became runnable again within 30 s: the cancelled attempt's lease was not released");
    }

    private Task<string?> StateAsync(string database, Guid artifactId) =>
        _fixture.ScalarAsync<string?>(database, $"select state from public.artifact where id = '{artifactId:D}'");

    private Task<string?> FailureCodeAsync(string database, Guid artifactId) =>
        _fixture.ScalarAsync<string?>(database, $"select failure_code from public.artifact where id = '{artifactId:D}'");
}

/// <summary>
/// The runner seam, controlled: the first run models a parse in flight that only ends when its token is cancelled, the
/// second answers what the test sets. It records that it observed the cancellation, which is the strongest statement a
/// fake can make about "the child is gone"; that the real child is reaped is the runner's own Linux-only surface.
/// </summary>
internal sealed class BlockingParseRunner : IParseRunner
{
    /// <summary>
    /// Cancelled from inside the first run, so the attempt ends mid-parse exactly as a killed worker's lease would end
    /// it. Set by the test; the runner only ever cancels its own caller's token, which is the shape a shutdown has.
    /// </summary>
    public CancellationTokenSource? CancelAfterStart { get; set; }

    /// <summary>True once a run's cancellation token fired while the run was in flight.</summary>
    public bool ObservedCancellation { get; private set; }

    /// <summary>How many runs were started.</summary>
    public int Calls { get; private set; }

    /// <summary>The answer the second and later runs give.</summary>
    public ParseRunResult? NextResult { get; set; }

    /// <inheritdoc />
    public async Task<ParseRunResult> RunAsync(ParseRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Calls++;

        if (Calls == 1)
        {
            if (CancelAfterStart is { } cancel)
            {
                await cancel.CancelAsync();
            }

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ObservedCancellation = true;
                throw;
            }
        }

        return NextResult ?? throw new InvalidOperationException("the second run needs an answer");
    }
}
