// 05.4.1-T1 through the real host and a real PostgreSQL: the shared arrange/observe helpers of the cancel
// endpoint suites. Everything here goes through the production routes or reads the tables as the owner role,
// so a test never restates a rule the handler owns.
#pragma warning disable CA1861
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.Modules.Orchestrator.Contracts;
using Shouldly;
using Xunit;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>One stored cancel request, the way the table decides it.</summary>
internal sealed record CancelRow(
    Guid RequestedBy,
    string? Reason,
    string State,
    DateTimeOffset RequestedAt,
    DateTimeOffset SoftStopDeadlineAt,
    DateTimeOffset KillAt);

/// <summary>Shared helpers of <see cref="CancelRunEndpointTests"/> and <see cref="CancelRunEdgeCaseTests"/>.</summary>
internal static class CancelRunTestData
{
    internal const string StateChangedTask = "orchestrator.run.state_changed";

    internal const string StateChangedAction = "run.state_changed";

    private static string CancelPath(Guid runId) => $"/v1/runs/{runId:D}/cancel";

    /// <summary>The 00 §9.2 <c>error.details</c> rows of a refusal body.</summary>
    internal static List<JsonElement> Details(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()];

    /// <summary>
    /// The whole refusal envelope with its per-request <c>request_id</c> normalised away, so two refusals can be
    /// compared as bodies — a difference in <c>code</c>, <c>message</c>, <c>retryable</c> or <c>details</c> shows.
    /// </summary>
    internal static string Envelope(ApiAnswer answer)
    {
        var root = JsonNode.Parse(answer.Body.GetRawText())?.AsObject()
            ?? throw new InvalidOperationException("the refusal body is not a JSON object");
        var error = root["error"] as JsonObject
            ?? throw new InvalidOperationException("the refusal body carries no error object");
        error["request_id"] = "normalised";

        return root.ToJsonString();
    }

    /// <summary>One <c>POST /v1/runs/{r}/cancel</c> with a fresh idempotency key (the route requires one).</summary>
    internal static Task<ApiAnswer> CancelAsync(
        RunCreationTests.World world,
        Guid runId,
        Action<HttpRequestHeaders>? caller = null,
        string? reason = "probe",
        HttpClient? client = null,
        string? idempotencyKey = null) =>
        (client ?? world.Client).SendAsync(
            HttpMethod.Post,
            CancelPath(runId),
            caller ?? world.Admin,
            reason is null ? null : new { reason },
            idempotencyKey ?? NewKey());

    /// <summary>One raw-body <c>POST /v1/runs/{r}/cancel</c>, black-box over HTTP.</summary>
    internal static async Task<ApiAnswer> CancelRawAsync(
        HttpClient client,
        Guid runId,
        string rawBody,
        Action<HttpRequestHeaders> caller,
        string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, CancelPath(runId));
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey ?? NewKey());
        request.Content = new StringContent(rawBody, System.Text.Encoding.UTF8, "application/json");
        caller(request.Headers);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(IsJson(text) ? text : "{}");

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    private static bool IsJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>One API-created run (state <c>queued</c>, version 0).</summary>
    internal static async Task<Guid> CreateQueuedRunAsync(RunCreationTests.World world)
    {
        var created = await RunCreationTests.CreateRunAsync(world, RunCreationTests.RunBody(world.ManifestId));

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        return created.Id("run_id");
    }

    /// <summary>One committed run in <paramref name="state"/>, driven there through the transition itself.</summary>
    internal static async Task<Guid> RunInStateAsync(
        PostgresTenancyFixture fixture,
        RunCreationTests.World world,
        string state)
    {
        if (state == "created")
        {
            // The one state the API never writes (05.2.1-T4 inserts `queued`): seeded as the migration role.
            var seeded = await RunRowSeeder.SeedRunAsync(fixture, world.Database, world.Org);
            (await StateAsync(fixture, world, seeded)).ShouldBe("created");

            return seeded;
        }

        var runId = await CreateQueuedRunAsync(world);
        foreach (var step in DriveTo(state))
        {
            (await TransitionAsync(world, runId, step)).State.ShouldBe(step);
        }

        (await StateAsync(fixture, world, runId)).ShouldBe(state);

        return runId;
    }

    /// <summary>The committed transitions that drive a fresh run from <c>queued</c> to <paramref name="state"/>.</summary>
    internal static IEnumerable<string> DriveTo(string state) => state switch
    {
        "created" or "queued" => [],
        "running" => ["running"],
        "collecting" => ["running", "collecting"],
        "complete" => ["running", "collecting", "complete"],
        "incomplete" => ["running", "incomplete"],
        "canceled" => ["canceled"],
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "not a run state"),
    };

    /// <summary>
    /// One committed transition in its own tenant transaction — the reconciler's own path (05.3.2 hands the
    /// verdict to this same handler), so the race is decided by the production guard, not a test copy.
    /// </summary>
    internal static async Task<TransitionResult> TransitionAsync(
        RunCreationTests.World world,
        Guid runId,
        string to,
        TransitionActor actor = TransitionActor.System)
    {
        await using var scope = await RunTransitionScope.BeginAsync(world.Api, world.Org, world.Project);
        var result = await scope.TransitionAsync(new TransitionRunStateCommand(runId, to, actor));
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);

        if (result.Value.Outcome == TransitionOutcome.Applied)
        {
            await scope.FlushAuditAsync();
            await scope.CommitAsync();
        }

        return result.Value;
    }

    internal static Task<string> StateAsync(PostgresTenancyFixture fixture, RunCreationTests.World world, Guid runId) =>
        StateAsync(fixture, world.Database, runId);

    /// <summary>The committed run state, by database: a suite that outlives its host (a restart) still reads it.</summary>
    internal static Task<string> StateAsync(PostgresTenancyFixture fixture, string database, Guid runId) =>
        fixture.ScalarAsync<string>(database, $"select state from public.run where id = '{runId}'");

    internal static Task<int> VersionAsync(PostgresTenancyFixture fixture, RunCreationTests.World world, Guid runId) =>
        fixture.ScalarAsync<int>(world.Database, $"select version from public.run where id = '{runId}'");

    /// <summary>The cancel request rows of one run.</summary>
    internal static Task<long> RowCountAsync(PostgresTenancyFixture fixture, RunCreationTests.World world, Guid runId) =>
        fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.run_cancel_request where org_id = '{world.Org}' and run_id = '{runId}'");

    /// <summary>The one cancel request row of one run, as the table stores it.</summary>
    internal static Task<CancelRow> RowAsync(
        PostgresTenancyFixture fixture,
        RunCreationTests.World world,
        Guid runId) =>
        RowAsync(fixture, world.Database, world.Org, runId);

    /// <summary>The one cancel request row of one run, by database and tenant: a restart test keeps both after its host.</summary>
    internal static async Task<CancelRow> RowAsync(
        PostgresTenancyFixture fixture,
        string database,
        Guid orgId,
        Guid runId)
    {
        await using var connection = await fixture.OpenSuperuserAsync(database);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select requested_by, reason, state, requested_at, soft_stop_deadline_at, kill_at
              from public.run_cancel_request
             where org_id = @org_id and run_id = @run_id
            """;
        command.Parameters.AddWithValue("org_id", orgId);
        command.Parameters.AddWithValue("run_id", runId);

        var token = TestContext.Current.CancellationToken;
        await using var reader = await command.ExecuteReaderAsync(token);
        (await reader.ReadAsync(token)).ShouldBeTrue($"no run_cancel_request row for run {runId:D}");

        var row = new CancelRow(
            await reader.GetFieldValueAsync<Guid>(0, token),
            await reader.IsDBNullAsync(1, token) ? null : await reader.GetFieldValueAsync<string>(1, token),
            await reader.GetFieldValueAsync<string>(2, token),
            await reader.GetFieldValueAsync<DateTimeOffset>(3, token),
            await reader.GetFieldValueAsync<DateTimeOffset>(4, token),
            await reader.GetFieldValueAsync<DateTimeOffset>(5, token));

        (await reader.ReadAsync(token)).ShouldBeFalse("one run has at most one cancel request row");

        return row;
    }

    /// <summary>The <c>to</c> of every committed <c>run.state_changed</c> outbox row of one run, in order.</summary>
    internal static Task<IReadOnlyList<string>> OutboxTargetsAsync(
        PostgresTenancyFixture fixture,
        RunCreationTests.World world,
        Guid runId) =>
        fixture.ColumnAsync<string>(
            world.Database,
            $"select payload ->> 'to' from public.outbox where aggregate_id = '{runId}' and task_type = '{StateChangedTask}' "
            + "order by created_at, id");
}
