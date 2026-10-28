using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Evidgate.Api.OperatorIngress;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// 08.6.3-T1 (coordinator decision, ruling T72): the platform-wide healing switch lives only on the operator ingress.
/// The registered gate denies a request that carries no operator credential (401 <c>unauthenticated</c>), a permitted
/// operator sets the one <c>system</c> row through migration 0052's definer with its own user id as <c>set_by</c>, and
/// the V1 clamp holds there too — <c>enabled</c> is 422 <c>mode_not_available</c> while <c>Healing:Apply</c> is off.
/// </summary>
/// <remarks>
/// The gate is a test double, exactly as the dead-letter suite does it: the real <c>platform_operator</c> check lands
/// with 02.8.1. The permitting double also stamps the principal, because an operator route that cannot attribute a
/// change to a person is refused (401 unauthenticated / 403 forbidden) by the handler itself.
/// </remarks>
[Collection(PostgresSuite.Name)]
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

    /// <summary>AC1/T72: the default (fail-closed) gate answers 401 <c>unauthenticated</c> and writes no row.</summary>
    [Fact]
    public async Task The_default_gate_denies_the_system_switch()
    {
        var connectionString = await _postgres.CreateMigratedDatabaseAsync();

        await using var factory = new EvidgateApiFactory(connectionString);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/internal/operator/healing/switch", new { mode = "shadow" }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(response)).ShouldBe("unauthenticated");
    }

    /// <summary>
    /// AC1: a permitted operator sets the system row and a second call bumps its version; AC2/AC7: the vocabulary
    /// and the clamp are the same gate the tenant route uses, and an unattributable principal is refused.
    /// </summary>
    [Fact]
    public async Task The_permitted_operator_sets_the_system_switch_through_the_definer()
    {
        var connectionString = await _postgres.CreateMigratedDatabaseAsync();

        await using var factory = new EvidgateApiFactory(
            connectionString,
            configureServices: services => services.AddSingleton<IOperatorGate>(new PermitGate(OperatorId)));
        using var client = factory.CreateClient();

        using var first = await client.PostAsJsonAsync(
            "/internal/operator/healing/switch", new { mode = "shadow", reason = "V1 posture" }, Token);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var firstBody = await JsonDocument.ParseAsync(await first.Content.ReadAsStreamAsync(Token), cancellationToken: Token);
        var root = firstBody.RootElement;
        root.GetProperty("scope").GetString().ShouldBe("system");
        root.GetProperty("mode").GetString().ShouldBe("shadow");
        root.GetProperty("project_id").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("set_by").GetGuid().ShouldBe(OperatorId);
        root.GetProperty("reason").GetString().ShouldBe("V1 posture");
        var firstVersion = root.GetProperty("version").GetInt32();

        using var second = await client.PostAsJsonAsync(
            "/internal/operator/healing/switch", new { mode = "disabled" }, Token);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var secondBody = await JsonDocument.ParseAsync(await second.Content.ReadAsStreamAsync(Token), cancellationToken: Token);
        secondBody.RootElement.GetProperty("version").GetInt32().ShouldBeGreaterThan(firstVersion);
        secondBody.RootElement.GetProperty("mode").GetString().ShouldBe("disabled");

        using var unavailable = await client.PostAsJsonAsync(
            "/internal/operator/healing/switch", new { mode = "unavailable" }, Token);
        unavailable.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeAsync(unavailable)).ShouldBe("invalid_switch_mode");

        // AC7, on the operator path: the clamp refuses `enabled` while Healing:Apply is off (the test host sets none).
        using var enabled = await client.PostAsJsonAsync(
            "/internal/operator/healing/switch", new { mode = "enabled" }, Token);
        enabled.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeAsync(enabled)).ShouldBe("mode_not_available");
    }

    /// <summary>
    /// A permitting gate that stamps no principal cannot attribute a system change: the handler answers 401 rather
    /// than writing a row with an invented <c>set_by</c>.
    /// </summary>
    [Fact]
    public async Task An_unattributable_operator_is_refused()
    {
        var connectionString = await _postgres.CreateMigratedDatabaseAsync();

        await using var factory = new EvidgateApiFactory(
            connectionString,
            configureServices: services => services.AddSingleton<IOperatorGate>(new PermitGate(userId: null)));
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/internal/operator/healing/switch", new { mode = "shadow" }, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ErrorCodeAsync(response)).ShouldBe("unauthenticated");
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(Token), cancellationToken: Token);

        return document.RootElement.TryGetProperty("error", out var error)
            ? error.GetProperty("code").GetString()
            : null;
    }
}
