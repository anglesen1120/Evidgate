using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Evidgate.Api.OperatorIngress;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// 01.5.1-T5.2 / AC4 + AC5 (<c>plans/features/01-foundation/01.5.1-durable-internal-tasks.md</c> §2, §9):
/// the dead-letter half of the durable-task chain over the <b>real host</b>. The
/// test servers the operator routes and the production consumer share one migrated
/// database, so these tests cross the boundaries the per-component suites cannot:
/// a task dead-lettered by a running <c>TaskConsumer</c> is listed by
/// <c>GET /internal/operator/tasks/dead-letter</c>, and an operator replay issued
/// over HTTP is executed by a consumer again.
/// </summary>
/// <remarks>
/// Route-shape coverage that already exists and is not repeated here:
/// <c>OperatorDeadLetterEndpointTests</c> owns the keyset pagination and queue
/// filter, the malformed-parameter envelopes, the 409/404 replay guards, the gate
/// denials and the "absent from <c>/v1</c> and the served OpenAPI document" rule;
/// <c>TaskDeadLetterTests</c> owns the retry/backoff/redaction timing with a fake
/// clock. The permitted operator gate is the same test double those suites use
/// (the real <c>platform_operator</c> check lands with 02.x).
/// </remarks>
[Trait("Category", "Docker")]
public sealed class DeadLetterChainTests
{
    /// <summary>The queue this suite's poison tasks are seeded into; the operator list filters by it.</summary>
    private const string Queue = "operator-dead-letter-chain";

    private readonly PostgresFixture _postgres;

    public DeadLetterChainTests(PostgresFixture postgres) => _postgres = postgres;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// AC4's first clause and its list clause in one chain: a handler that always
    /// throws is retried (the row moves to <c>retry</c> with a future
    /// <c>available_at</c> and the redacted <c>last_error</c>), reaches
    /// <c>max_attempts</c>, and lands in <c>dead_letter</c> — and
    /// <c>GET /internal/operator/tasks/dead-letter</c> on the real host returns that
    /// very row with its queue, task type, attempt count and error.
    /// </summary>
    [Fact]
    public async Task PoisonTask_DeadLetters_AtTheCap_AndTheOperatorListReturnsIt()
    {
        await using var appConnectionString = await _postgres.CreateMigratedDatabaseAsync();
        var database = new NpgsqlConnectionStringBuilder(appConnectionString).Database!;
        var admin = _postgres.AdminConnectionString(database);
        var org = await SeedOrgAsync(admin);
        var taskId = await SeedTaskAsync(admin, org, Queue, DeadLetterChainHandler.HandlerTaskType, maxAttempts: 2);

        await using var consumer = DeadLetterChainConsumerHost.Start(_postgres, database, Queue);
        consumer.Recorder.Failure = new InvalidOperationException(DeadLetterChainRecorder.PoisonMessage);

        (await consumer.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);
        consumer.Recorder.TotalRuns.ShouldBe(1);

        // Attempt one: the row waits for its backoff and carries the redacted error.
        (await ScalarAsync<string>(admin, "select state from public.task where id = @id", ("id", taskId)))
            .ShouldBe("retry");
        (await ScalarAsync<bool>(
            admin,
            "select (available_at > now()) and locked_until is null from public.task where id = @id",
            ("id", taskId))).ShouldBeTrue("a retry waits for its backoff and releases the lease");
        (await ScalarAsync<string>(admin, "select last_error from public.task where id = @id", ("id", taskId)))
            .ShouldBe($"InvalidOperationException: {DeadLetterChainRecorder.PoisonMessage}");

        // Make the backoff elapse and poll again: the cap is reached.
        await ExecuteAsync(admin, "update public.task set available_at = now() where id = @id", ("id", taskId));

        (await consumer.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);
        consumer.Recorder.TotalRuns.ShouldBe(2);

        (await ScalarAsync<string>(admin, "select state from public.task where id = @id", ("id", taskId)))
            .ShouldBe("dead_letter");
        (await ScalarAsync<int>(admin, "select attempts from public.task where id = @id", ("id", taskId)))
            .ShouldBe(2);

        // AC4's second clause, over the real host: the operator route sees the row
        // the consumer just dead-lettered.
        await using var factory = Permitted(appConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/internal/operator/tasks/dead-letter?org_id={org:D}&queue={Uri.EscapeDataString(Queue)}", Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(Token), cancellationToken: Token);
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToArray();
        var item = items.Single(entry => entry.GetProperty("id").GetGuid() == taskId);

        item.GetProperty("queue").GetString().ShouldBe(Queue);
        item.GetProperty("task_type").GetString().ShouldBe(DeadLetterChainHandler.HandlerTaskType);
        item.GetProperty("attempts").GetInt32().ShouldBe(2);
        item.GetProperty("max_attempts").GetInt32().ShouldBe(2);
        item.GetProperty("last_error").GetString()
            .ShouldBe($"InvalidOperationException: {DeadLetterChainRecorder.PoisonMessage}");
        body.RootElement.GetProperty("next_cursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>
    /// AC5's execution clause: an operator replay issued over HTTP sets
    /// <c>state = 'queued'</c>, <c>attempts = 0</c> with a fresh
    /// <c>available_at</c> and no lease, and the replayed row <b>actually runs
    /// again</b> — the consumer claims it and the handler executes. The state guard
    /// still holds afterwards (the now-<c>done</c> row is a 409), and the route's
    /// 409/404 shapes are <c>OperatorDeadLetterEndpointTests</c>'.
    /// </summary>
    [Fact]
    public async Task ReplayChain_OperatorReplay_ReExecutesTheTask()
    {
        await using var appConnectionString = await _postgres.CreateMigratedDatabaseAsync();
        var database = new NpgsqlConnectionStringBuilder(appConnectionString).Database!;
        var admin = _postgres.AdminConnectionString(database);
        var org = await SeedOrgAsync(admin);
        var taskId = await SeedTaskAsync(
            admin,
            org,
            Queue,
            DeadLetterChainHandler.HandlerTaskType,
            state: "dead_letter",
            attempts: 3,
            maxAttempts: 3,
            lastError: "InvalidOperationException: earlier failure");

        await using var consumer = DeadLetterChainConsumerHost.Start(_postgres, database, Queue);

        await using var factory = Permitted(appConnectionString);
        using var client = factory.CreateClient();

        using var replayed = await client.PostAsync(
            $"/internal/operator/tasks/dead-letter/{taskId}/replay?org_id={org:D}", content: null, Token);
        replayed.StatusCode.ShouldBe(HttpStatusCode.OK);

        // What the route wrote, read back through the mechanism's own columns: the
        // row is immediately claimable again.
        (await ScalarAsync<string>(admin, "select state from public.task where id = @id", ("id", taskId)))
            .ShouldBe("queued");
        (await ScalarAsync<int>(admin, "select attempts from public.task where id = @id", ("id", taskId)))
            .ShouldBe(0);
        (await ScalarAsync<bool>(
            admin,
            "select locked_until is null and available_at <= now() from public.task where id = @id",
            ("id", taskId))).ShouldBeTrue();

        // The replay is not cosmetic: a consumer runs the row (the handler no longer
        // fails) and the row completes.
        (await consumer.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);
        consumer.Recorder.TotalRuns.ShouldBe(1);

        (await ScalarAsync<string>(admin, "select state from public.task where id = @id", ("id", taskId)))
            .ShouldBe("done");

        // The state guard is the replay's idempotency contract: the completed row is
        // no longer dead-lettered, so a second replay is the 409.
        using var again = await client.PostAsync(
            $"/internal/operator/tasks/dead-letter/{taskId}/replay?org_id={org:D}", content: null, Token);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var envelope = await JsonDocument.ParseAsync(
            await again.Content.ReadAsStreamAsync(Token), cancellationToken: Token);
        envelope.RootElement.GetProperty("error").GetProperty("code").GetString()
            .ShouldBe("task_not_dead_letter");
    }

    /// <summary>
    /// The test double that lets every request through and stamps the authenticated operator principal (the 02.x
    /// operator gate's stand-in): the replay route names that principal as the audit row's actor.
    /// </summary>
    private sealed class PermitAllOperatorGate : IOperatorGate
    {
        /// <summary>The acting operator: the replay's audit row names it.</summary>
        private static readonly Guid OperatorId = Guid.CreateVersion7();

        public ValueTask<bool> IsOperatorAsync(HttpContext httpContext)
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, OperatorId.ToString("D"))],
                authenticationType: "operator-test"));

            return ValueTask.FromResult(true);
        }
    }

    /// <summary>Api factory whose operator gate permits, over the test's migrated database.</summary>
    private static EvidgateApiFactory Permitted(string connectionString) =>
        new(connectionString, configureServices: services =>
            services.AddSingleton<IOperatorGate>(new PermitAllOperatorGate()));

    /// <summary>Seeds one organization: the operator routes name the tenant explicitly.</summary>
    private static async Task<Guid> SeedOrgAsync(string connectionString)
    {
        var org = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "insert into public.organization (id, name, slug) values (@id, 'chain', @slug)";
        command.Parameters.AddWithValue("@id", org);
        command.Parameters.AddWithValue("@slug", "chain-" + org.ToString("N"));
        await command.ExecuteNonQueryAsync(Token);
        return org;
    }

    /// <summary>Seeds one task row; only the row's arrangement goes through raw SQL (the suite's own convention).</summary>
    private static async Task<Guid> SeedTaskAsync(
        string connectionString,
        Guid org,
        string queue,
        string taskType,
        string state = "queued",
        int attempts = 0,
        int maxAttempts = 8,
        string? lastError = null)
    {
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into public.task
              (id, org_id, queue, task_type, payload, idempotency_key, state, attempts, max_attempts, last_error)
            values
              (@id, @org, @queue, @task_type, '{"value":"replay"}'::jsonb, @idempotency_key, @state, @attempts, @max_attempts, @last_error)
            """;
        command.Parameters.AddWithValue("@org", org);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@queue", queue);
        command.Parameters.AddWithValue("@task_type", taskType);
        command.Parameters.AddWithValue("@idempotency_key", $"acceptance:dead-letter:{id}");
        command.Parameters.AddWithValue("@state", state);
        command.Parameters.AddWithValue("@attempts", attempts);
        command.Parameters.AddWithValue("@max_attempts", maxAttempts);
        command.Parameters.AddWithValue("@last_error", (object?)lastError ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(Token);
        return id;
    }

    private static async Task<int> ExecuteAsync(
        string connectionString,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = Command(connection, sql, parameters);
        return await command.ExecuteNonQueryAsync(Token);
    }

    private static async Task<T?> ScalarAsync<T>(
        string connectionString,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = Command(connection, sql, parameters);
        var value = await command.ExecuteScalarAsync(Token);
        return value is null or DBNull ? default : (T)value;
    }

    private static NpgsqlCommand Command(
        NpgsqlConnection connection,
        string sql,
        (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue("@" + name, value);
        }

        return command;
    }
}

/// <summary>The payload of the chain's poison task (the suite's own task type).</summary>
internal sealed record DeadLetterChainPayload(string Value);

/// <summary>
/// What the chain's handler observed: how often it ran and whether it fails. The
/// handler records the run <em>before</em> it throws, so a retry and a replay are
/// both counted (the AC4/AC5 assertions are on those counts).
/// </summary>
internal sealed class DeadLetterChainRecorder
{
    /// <summary>Message of the poison task; also asserted in <c>last_error</c>.</summary>
    internal const string PoisonMessage = "poison task";

    private readonly List<string> _runs = [];

    /// <summary>When set, the handler throws it instead of succeeding.</summary>
    internal Exception? Failure { get; set; }

    /// <summary>Total handler invocations.</summary>
    internal int TotalRuns
    {
        get
        {
            lock (_runs)
            {
                return _runs.Count;
            }
        }
    }

    internal void RecordRun(string value)
    {
        lock (_runs)
        {
            _runs.Add(value);
        }
    }
}

/// <summary>
/// A handler that records every run and can always fail: the poison pill AC4
/// dead-letters, and — with <see cref="DeadLetterChainRecorder.Failure"/> cleared —
/// the task the AC5 replay must execute again.
/// </summary>
internal sealed class DeadLetterChainHandler(DeadLetterChainRecorder recorder) : ITaskHandler<DeadLetterChainPayload>
{
    /// <summary>The <c>task_type</c> this suite's rows carry.</summary>
    internal const string HandlerTaskType = "acceptance.dead-letter-chain";

    /// <inheritdoc />
    public string TaskType => DeadLetterChainHandler.HandlerTaskType;

    /// <inheritdoc />
    public bool RequiresTenant => false;

    /// <inheritdoc />
    public ValueTask HandleAsync(
        DeadLetterChainPayload payload,
        ITenantScope? scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        recorder.RecordRun(payload.Value);

        if (recorder.Failure is not null)
        {
            throw recorder.Failure;
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The production consumer over one test database: <c>AddEvidgateTenancy</c> plus
/// <c>AddEvidgateTasks</c> on the app role's data source, exactly what
/// <c>Evidgate.Worker</c> composes, with the chain's handler registered. Everything
/// else in this file is arrangement (raw SQL) or assertion (the host's own routes).
/// </summary>
internal sealed class DeadLetterChainConsumerHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly NpgsqlDataSource _dataSource;

    private DeadLetterChainConsumerHost(NpgsqlDataSource dataSource, ServiceProvider provider)
    {
        _dataSource = dataSource;
        _provider = provider;
    }

    internal TaskConsumer Consumer => _provider.GetRequiredService<TaskConsumer>();

    internal DeadLetterChainRecorder Recorder => _provider.GetRequiredService<DeadLetterChainRecorder>();

    internal static DeadLetterChainConsumerHost Start(PostgresFixture fixture, string database, string queue)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var connectionString = new NpgsqlConnectionStringBuilder(fixture.AppConnectionString(database))
        {
            MaxPoolSize = 4,
            MaxAutoPrepare = 0,
        };

        var dataSource = NpgsqlDataSource.Create(connectionString.ConnectionString);

        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"{TasksOptions.SectionName}:PollIntervalMilliseconds"] = "50",
            [$"{TasksOptions.SectionName}:WorkerConnectionString"] = fixture.WorkerConnectionString(database),
            [$"{TasksOptions.SectionName}:BatchSize"] = "50",
            [$"{TasksOptions.SectionName}:VisibilityTimeoutSeconds"] = "300",
            [$"{TasksOptions.SectionName}:BackoffBaseSeconds"] = "1",
            [$"{TasksOptions.SectionName}:BackoffMaxSeconds"] = "4",
            [$"{TasksOptions.SectionName}:Queues:0:Name"] = queue,
            [$"{TasksOptions.SectionName}:Queues:0:Concurrency"] = "1",
        };

        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddLogging();
        services.AddEvidgateTenancy();
        services.AddEvidgateTasks(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        services.AddKeyedSingleton<NpgsqlDataSource>(TaskWorkerDataSource.Key, (_, _) =>
            NpgsqlDataSource.Create(fixture.WorkerConnectionString(database)));
        services.AddSingleton<DeadLetterChainRecorder>();
        services.AddTaskHandler<DeadLetterChainPayload, DeadLetterChainHandler>();

        return new DeadLetterChainConsumerHost(dataSource, services.BuildServiceProvider());
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _dataSource.DisposeAsync();
    }
}
