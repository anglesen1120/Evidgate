using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Evidgate.Api.OperatorIngress;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// 01.5.1-T4.2 / AC4 + AC5 (00 §9.1): the dead-letter operator routes exist only
/// on the operator ingress, behind a gate whose default denies, with the 00 §9.2
/// envelope for every failure shape — and never on <c>/v1</c> or in the served
/// OpenAPI document.
/// </summary>
/// <remarks>
/// The gate is a test double: the real <c>platform_operator</c> check lands with
/// the 02.x authentication story, so the route behaviour is proven with
/// <see cref="PermitAllOperatorGate"/> and the denial contract with the
/// registered <c>DenyAllOperatorGate</c>.
/// </remarks>
[Collection(PostgresSuite.Name)]
[Trait("Category", "Docker")]
public sealed class OperatorDeadLetterEndpointTests
{
    private const string DeadConnectionString =
        "Host=127.0.0.1;Port=1;Username=evidgate_app;Password=y;Database=evidgate;Timeout=2";

    private readonly PostgresFixture _postgres;

    public OperatorDeadLetterEndpointTests(PostgresFixture postgres) => _postgres = postgres;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The test double that lets every request through.</summary>
    private sealed class PermitAllOperatorGate : IOperatorGate
    {
        public ValueTask<bool> IsOperatorAsync(HttpContext httpContext) => ValueTask.FromResult(true);
    }

    /// <summary>Api factory whose operator gate permits (the 02.x gate stub replaced for the test).</summary>
    private static EvidgateApiFactory Permitted(string connectionString) =>
        new(connectionString, configureServices: services =>
            services.AddSingleton<IOperatorGate>(new PermitAllOperatorGate()));

    /// <summary>A seeded task row; returns the id assigned to it.</summary>
    private static async Task<Guid> SeedTaskAsync(
        string connectionString,
        string queue,
        string state,
        DateTime createdAt,
        string idempotencyKey)
    {
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into public.task
              (id, queue, task_type, payload, idempotency_key, state, attempts, max_attempts, last_error, created_at)
            values
              (@id, @queue, 'operator.test', '{}'::jsonb, @idempotency_key, @state, 3, 3, 'InvalidOperationException: boom', @created_at)
            """;
        command.Parameters.Add(new NpgsqlParameter("@id", id));
        command.Parameters.Add(new NpgsqlParameter("@queue", queue));
        command.Parameters.Add(new NpgsqlParameter("@idempotency_key", idempotencyKey));
        command.Parameters.Add(new NpgsqlParameter("@state", state));
        command.Parameters.Add(new NpgsqlParameter("@created_at", createdAt));
        await command.ExecuteNonQueryAsync(Token);
        return id;
    }

    /// <summary>The error envelope's code for a failing response.</summary>
    private static string ErrorCode(JsonDocument document) =>
        document.RootElement.GetProperty("error").GetProperty("code").GetString()!;

    /// <summary>
    /// AC4's list half: pages walk <c>task_dead_idx</c> order
    /// (<c>created_at, id</c>), `next_cursor` continues the keyset with no dup
    /// and no gap, the `queue` filter narrows without disturbing the order.
    /// </summary>
    [Fact]
    public async Task List_Pages_MultiQueue_WithKeysetContinuation()
    {
        var connectionString = await _postgres.CreateMigratedDatabaseAsync();
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database!;
        var admin = _postgres.AdminConnectionString(database);

        var oldest = DateTime.UtcNow.AddHours(-2);
        var seeded = new List<(Guid Id, string Queue)>();
        for (var index = 0; index < 5; index++)
        {
            var queue = index < 3 ? "ops" : "sweep";
            seeded.Add((
                await SeedTaskAsync(
                    admin, queue, "dead_letter", oldest.AddMinutes(index), $"dead:{index}"),
                queue));
        }

        await using var factory = Permitted(connectionString);
        using var client = factory.CreateClient();

        var collected = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = "/internal/operator/tasks/dead-letter?limit=2"
                + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
            using var response = await client.GetAsync(url, Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(Token), cancellationToken: Token);
            var items = body.RootElement.GetProperty("items");
            Assert.True(items.GetArrayLength() <= 2);
            collected.AddRange(items.EnumerateArray()
                .Select(item => item.GetProperty("id").GetGuid()));
            cursor = body.RootElement.GetProperty("next_cursor").ValueKind == JsonValueKind.Null
                ? null
                : body.RootElement.GetProperty("next_cursor").GetString();
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(3, pages);
        // (created_at, id) ascending: the seeded order IS the (created_at) order.
        Assert.Equal([.. seeded.Select(entry => entry.Id)], collected);

        // The queue filter narrows within the same ordering contract.
        using var filtered = await client.GetAsync(
            "/internal/operator/tasks/dead-letter?queue=sweep&limit=100", Token);
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        var filteredBody = await JsonDocument.ParseAsync(
            await filtered.Content.ReadAsStreamAsync(Token), cancellationToken: Token);
        var filteredItems = filteredBody.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, filteredItems.Length);
        Assert.All(filteredItems, item => Assert.Equal("sweep", item.GetProperty("queue").GetString()));
    }

    /// <summary>
    /// 00 §9.1 bounds and the malformed-input envelope: a non-integer `limit` and
    /// a cursor the endpoint did not produce are 400 `request_invalid`; an
    /// over-large `limit` is clamped, not rejected.
    /// </summary>
    [Fact]
    public async Task List_InvalidParams_400_And_Limit_Clamped()
    {
        var connectionString = await _postgres.CreateMigratedDatabaseAsync();
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database!;
        var admin = _postgres.AdminConnectionString(database);
        await SeedTaskAsync(admin, "ops", "dead_letter", DateTime.UtcNow.AddMinutes(-1), "dead:one");

        await using var factory = Permitted(connectionString);
        using var client = factory.CreateClient();

        using var badLimit = await client.GetAsync(
            "/internal/operator/tasks/dead-letter?limit=abc", Token);
        Assert.Equal(HttpStatusCode.BadRequest, badLimit.StatusCode);
        Assert.Equal("request_invalid", ErrorCode(await JsonDocument.ParseAsync(
            await badLimit.Content.ReadAsStreamAsync(Token), cancellationToken: Token)));

        using var badCursor = await client.GetAsync(
            "/internal/operator/tasks/dead-letter?cursor=not-a-cursor", Token);
        Assert.Equal(HttpStatusCode.BadRequest, badCursor.StatusCode);
        Assert.Equal("request_invalid", ErrorCode(await JsonDocument.ParseAsync(
            await badCursor.Content.ReadAsStreamAsync(Token), cancellationToken: Token)));

        using var clamped = await client.GetAsync(
            "/internal/operator/tasks/dead-letter?limit=9999", Token);
        Assert.Equal(HttpStatusCode.OK, clamped.StatusCode);
        var clampedBody = await JsonDocument.ParseAsync(
            await clamped.Content.ReadAsStreamAsync(Token), cancellationToken: Token);
        Assert.Equal(1, clampedBody.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, clampedBody.RootElement.GetProperty("next_cursor").ValueKind);
    }

    /// <summary>
    /// AC5: replaying a dead-lettered row requeues it — <c>state = 'queued'</c>,
    /// <c>attempts = 0</c>, fresh <c>available_at</c>, lease cleared.
    /// </summary>
    [Fact]
    public async Task Replay_DeadLetter_Requeues()
    {
        var connectionString = await _postgres.CreateMigratedDatabaseAsync();
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database!;
        var admin = _postgres.AdminConnectionString(database);
        var taskId = await SeedTaskAsync(
            admin, "ops", "dead_letter", DateTime.UtcNow.AddMinutes(-10), "dead:replay");

        await using var factory = Permitted(connectionString);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            $"/internal/operator/tasks/dead-letter/{taskId}/replay", content: null, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(Token), cancellationToken: Token);
        Assert.Equal(taskId, body.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("queued", body.RootElement.GetProperty("state").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("attempts").GetInt32());

        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync(Token);
        await using var check = connection.CreateCommand();
        check.CommandText = "select state, attempts, locked_until is null, available_at > now() - interval '1 minute' from public.task where id = @id";
        check.Parameters.Add(new NpgsqlParameter("@id", taskId));
        await using var reader = await check.ExecuteReaderAsync(Token);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal("queued", reader.GetString(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.True(reader.GetBoolean(2), "the lease is released");
        Assert.True(reader.GetBoolean(3), "the row is immediately claimable");
    }

    /// <summary>AC5: a replay on a non-dead-letter row is a 409 `task_not_dead_letter`.</summary>
    [Fact]
    public async Task Replay_NonDeadLetter_409()
    {
        var connectionString = await _postgres.CreateMigratedDatabaseAsync();
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database!;
        var admin = _postgres.AdminConnectionString(database);
        var taskId = await SeedTaskAsync(
            admin, "ops", "queued", DateTime.UtcNow.AddMinutes(-5), "live:queued");

        await using var factory = Permitted(connectionString);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            $"/internal/operator/tasks/dead-letter/{taskId}/replay", content: null, Token);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("task_not_dead_letter", ErrorCode(await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(Token), cancellationToken: Token)));

        // The state guard is the idempotency contract: the row is untouched.
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync(Token);
        await using var check = connection.CreateCommand();
        check.CommandText = "select state from public.task where id = @id";
        check.Parameters.Add(new NpgsqlParameter("@id", taskId));
        Assert.Equal("queued", (string)(await check.ExecuteScalarAsync(Token))!);
    }

    /// <summary>AC5: a replay on an id that never existed is a 404 `task_not_found`.</summary>
    [Fact]
    public async Task Replay_UnknownId_404()
    {
        var connectionString = await _postgres.CreateMigratedDatabaseAsync();

        await using var factory = Permitted(connectionString);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            $"/internal/operator/tasks/dead-letter/{Guid.CreateVersion7()}/replay", content: null, Token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("task_not_found", ErrorCode(await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(Token), cancellationToken: Token)));
    }

    /// <summary>
    /// The default gate is fail-closed (01.5.1-T4.2, the DenyAllPermissionChecker
    /// precedent): with nothing registered to answer "is this an operator",
    /// a request without an authenticated principal gets 401 `unauthenticated` —
    /// on both routes, so the group filter is proven, not just one endpoint's.
    /// </summary>
    [Fact]
    public async Task Gate_Denies_Unauthenticated_401()
    {
        await using var factory = new EvidgateApiFactory(DeadConnectionString);
        using var client = factory.CreateClient();

        using var list = await client.GetAsync("/internal/operator/tasks/dead-letter", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal("unauthenticated", ErrorCode(await JsonDocument.ParseAsync(
            await list.Content.ReadAsStreamAsync(Token), cancellationToken: Token)));

        using var replay = await client.PostAsync(
            $"/internal/operator/tasks/dead-letter/{Guid.CreateVersion7()}/replay", content: null, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("unauthenticated", ErrorCode(await JsonDocument.ParseAsync(
            await replay.Content.ReadAsStreamAsync(Token), cancellationToken: Token)));
    }

    /// <summary>
    /// The other gate denial (01.5.1 §5's 403): a request that presents an
    /// authenticated principal but not the <c>platform_operator</c> grant gets
    /// 403 `forbidden`. 02.x's auth middleware will be the caller that sets the
    /// principal; the test sets it through the raw TestServer context.
    /// </summary>
    [Fact]
    public async Task Gate_Denies_AuthenticatedNonOperator_403()
    {
        await using var factory = new EvidgateApiFactory(DeadConnectionString);

        var context = await factory.Server.SendAsync(http =>
        {
            http.Request.Method = "GET";
            http.Request.Path = "/internal/operator/tasks/dead-letter";
            http.User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim("sub", "tenant-caller")], "test"));
        }, Token);

        Assert.Equal(403, context.Response.StatusCode);
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync(Token);
        Assert.Contains("\"forbidden\"", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// 00 §9.1 / T16: the operator routes are absent from <c>/v1</c> and from the
    /// served OpenAPI document — the route-shape assertion, not just the
    /// convention comment.
    /// </summary>
    [Fact]
    public async Task DeadLetter_NotServedOnV1_OrInOpenApi()
    {
        await using var factory = Permitted(DeadConnectionString);
        using var client = factory.CreateClient();

        using var v1Route = await client.GetAsync("/v1/tasks/dead-letter", Token);
        Assert.Equal(HttpStatusCode.NotFound, v1Route.StatusCode);
        Assert.Equal("not_found", ErrorCode(await JsonDocument.ParseAsync(
            await v1Route.Content.ReadAsStreamAsync(Token), cancellationToken: Token)));

        using var openApi = await client.GetAsync("/openapi/v1.json", Token);
        Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
        var document = await openApi.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("/internal/operator", document, StringComparison.Ordinal);
        Assert.DoesNotContain("dead-letter", document, StringComparison.Ordinal);
        Assert.DoesNotContain("dead_letter", document, StringComparison.Ordinal);
    }
}
