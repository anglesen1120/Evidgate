using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Evidgate.Api.OperatorIngress;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// The platform-wide healing switch route on the operator ingress. The registered gate denies a request that carries
/// no operator credential (401 <c>unauthenticated</c>); a permitted operator reaches the handler, whose definer call
/// the database refuses because the API role holds no EXECUTE on it — 403 <c>forbidden</c>, never a 500 and never a
/// stored row. The V1 clamp still answers 422 before any connection is opened.
/// </summary>
/// <remarks>
/// The gate is a test double, exactly as the dead-letter suite does it: the real <c>platform_operator</c> check lands
/// with 02.8.1. The permitting double also stamps the principal, because an operator route that cannot attribute a
/// change to a person is refused (401 unauthenticated / 403 forbidden) by the handler itself.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class OperatorHealingSwitchEndpointTests
{
    private static readonly Guid OperatorId = Guid.CreateVersion7();

    private readonly PostgresFixture _postgres;

    public OperatorHealingSwitchEndpointTests(PostgresFixture postgres) => _postgres = postgres;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A gate that permits and, when asked to, stamps the authenticated operator principal.</summary>
    private sealed class PermitGate(Guid? userId) : IOperatorGate
    {
        public ValueTask<bool> IsOperatorAsync(HttpContext httpContext)
        {
            if (userId is { } id)
            {
                httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, id.ToString("D"))],
                    authenticationType: "operator-test"));
            }

            return ValueTask.FromResult(true);
        }
    }

    /// <summary>
    /// AC1/T72: the default (fail-closed) gate answers 401 <c>unauthenticated</c> and writes no row.
    /// </summary>
    [Fact]
    public async Task The_default_gate_denies_the_system_switch()
    {
        await using var connectionString = await _postgres.CreateMigratedDatabaseAsync();

        await using var factory = new EvidgateApiFactory(connectionString);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/internal/operator/healing/switch", new { mode = "shadow" }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(response)).ShouldBe("unauthenticated");
    }

    /// <summary>
    /// The body is read inside the handler, after the gate and the attribution checks: an anonymous caller sending a
    /// body that is not even JSON still gets 401, so the route never parses untrusted input before it is admitted
    /// (SR-F6, decision (d)).
    /// </summary>
    [Fact]
    public async Task The_gate_decides_before_the_body_is_parsed()
    {
        await using var connectionString = await _postgres.CreateMigratedDatabaseAsync();

        await using var factory = new EvidgateApiFactory(connectionString);
        using var client = factory.CreateClient();

        using var content = new StringContent("{not json at all", System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/internal/operator/healing/switch", content, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(response)).ShouldBe("unauthenticated");
    }

    /// <summary>
    /// The API role holds no EXECUTE on the definer, so a permitted operator's valid request is a 403-class refusal
    /// and writes nothing at all: no system row and no platform operator audit row.
    /// </summary>
    [Fact]
    public async Task The_permitted_operator_is_refused_and_writes_no_row()
    {
        await using var connectionString = await _postgres.CreateMigratedDatabaseAsync();
        var admin = _postgres.AdminConnectionString(connectionString.Name);

        await using var factory = new EvidgateApiFactory(
            connectionString,
            configureServices: services => services.AddSingleton<IOperatorGate>(new PermitGate(OperatorId)));
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/internal/operator/healing/switch", new { mode = "shadow", reason = "V1 posture" }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).ShouldBe("forbidden");
        await AssertNothingStoredAsync(admin);
    }

    /// <summary>
    /// The mode vocabulary and the V1 clamp are judged before any connection is opened: <c>unavailable</c> is 422
    /// <c>invalid_switch_mode</c> and <c>enabled</c> is 422 <c>mode_not_available</c> while the clamp is off, so
    /// neither refusal depends on the definer's grant.
    /// </summary>
    [Fact]
    public async Task The_mode_vocabulary_and_the_clamp_are_judged_before_the_database()
    {
        await using var connectionString = await _postgres.CreateMigratedDatabaseAsync();
        var admin = _postgres.AdminConnectionString(connectionString.Name);

        await using var factory = new EvidgateApiFactory(
            connectionString,
            configureServices: services => services.AddSingleton<IOperatorGate>(new PermitGate(OperatorId)));
        using var client = factory.CreateClient();

        using var unavailable = await client.PostAsJsonAsync(
            "/internal/operator/healing/switch", new { mode = "unavailable" }, Token);
        unavailable.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeAsync(unavailable)).ShouldBe("invalid_switch_mode");

        // The clamp, on the operator path: the test host sets no Healing:Apply, the V1 posture.
        using var enabled = await client.PostAsJsonAsync(
            "/internal/operator/healing/switch", new { mode = "enabled" }, Token);
        enabled.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeAsync(enabled)).ShouldBe("mode_not_available");

        await AssertNothingStoredAsync(admin);
    }

    /// <summary>
    /// With the clamp on, <c>enabled</c> passes the gate and the handler really runs, so this case proves the database
    /// refusal is what answers the route — 403 rather than a 500 — and that it stores nothing.
    /// </summary>
    [Fact]
    public async Task A_permitted_operator_reaching_the_handler_with_the_clamp_on_is_refused()
    {
        await using var connectionString = await _postgres.CreateMigratedDatabaseAsync();
        var admin = _postgres.AdminConnectionString(connectionString.Name);

        await using var factory = new EvidgateApiFactory(
            connectionString,
            configureServices: services => services.AddSingleton<IOperatorGate>(new PermitGate(OperatorId)));
        await using var host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["EVIDGATE:Healing:Apply"] = "true",
                })));
        using var client = host.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/internal/operator/healing/switch", new { mode = "enabled", reason = "clamp on" }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).ShouldBe("forbidden");
        await AssertNothingStoredAsync(admin);
    }

    /// <summary>
    /// A permitting gate that stamps no principal cannot attribute a system change: the handler answers 401 rather
    /// than writing a row with an invented <c>set_by</c>.
    /// </summary>
    [Fact]
    public async Task An_unattributable_operator_is_refused()
    {
        await using var connectionString = await _postgres.CreateMigratedDatabaseAsync();

        await using var factory = new EvidgateApiFactory(
            connectionString,
            configureServices: services => services.AddSingleton<IOperatorGate>(new PermitGate(userId: null)));
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/internal/operator/healing/switch", new { mode = "shadow" }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(response)).ShouldBe("unauthenticated");
    }

    /// <summary>No switch write may leave a trace while the API role lacks the definer's EXECUTE.</summary>
    private static async Task AssertNothingStoredAsync(string adminConnectionString)
    {
        (await CountAsync(adminConnectionString,
            "select count(*) from public.healing_switch where scope = 'system'")).ShouldBe(0L);
        (await CountAsync(adminConnectionString, "select count(*) from public.platform_operator_audit")).ShouldBe(0L);
    }

    private static async Task<long> CountAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return (long)(await command.ExecuteScalarAsync(Token))!;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(Token), cancellationToken: Token);

        return document.RootElement.TryGetProperty("error", out var error)
            ? error.GetProperty("code").GetString()
            : null;
    }
}
