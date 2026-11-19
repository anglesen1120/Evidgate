using System.Net;
using System.Text.Json;
using Evidgate.Modules.Identity.Login;
using Evidgate.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Identity.IntegrationTests.Login.Linking;

/// <summary>
/// The composition half of <see cref="LinkTestHost"/>: the configuration the deployment needs, the owner-seam
/// plumbing the row helpers read through, and the request builders a case sends with.
/// </summary>
internal sealed partial class LinkTestHost
{
    // ------------------------------------------------------------------ wiring

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Production");

        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Login:PublicBaseUrl"] = PublicBaseUrl,
                ["EVIDGATE:Login:StateLifetimeSeconds"] =
                    StateLifetimeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["EVIDGATE:Login:RetryAfterSeconds"] =
                    RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["EVIDGATE:Database:ConnectionString"] = _connectionString,
                // The linking cases are not about throttling and share the assembly's database: a wide budget
                // keeps one case's attempts from answering another's request with a 429 in the same window
                // (the shape IsolationApiFactory uses).
                ["EVIDGATE:Identity:RateLimits:LoginLimit"] = "1000",
                ["EVIDGATE:Identity:RateLimits:LinkLimit"] = "1000",
                ["EVIDGATE:Identity:RateLimits:InviteLimit"] = "1000",
                ["EVIDGATE:Runs:Cancel:SoftStopDelay"] = "00:00:30",
                ["EVIDGATE:Runs:Cancel:KillDelay"] = "00:02:00",
                ["EVIDGATE:Isolation:SelfCheck"] = "Disabled",
                ["EVIDGATE:Session:Pepper"] = Convert.ToBase64String(Pepper),
            }));

        builder.ConfigureTestServices(services =>
        {
            ArgumentNullException.ThrowIfNull(services);

            // Exactly the case's providers: the host's real ones are unconfigured anyway, and a fake named
            // `google` would shadow the real registration for the same route value.
            services.RemoveAll<ILoginProvider>();
            foreach (var provider in _providers)
            {
                services.AddSingleton<ILoginProvider>(provider);
            }

            if (_logProvider is { } logProvider)
            {
                services.AddSingleton(logProvider);
            }

            _overrides?.Invoke(services);
        });
    }

    /// <summary>The harness the host runs against (needed by the row helpers).</summary>
    internal required DatabaseFixture Fixture { get; init; }

    /// <summary>One scalar read as the owner connection (the only role that may read the identity tables).</summary>
    /// <param name="sql">The query.</param>
    /// <param name="values">Its parameters.</param>
    private async Task<object?> OwnerScalarAsync(string sql, IReadOnlyList<(string Name, object Value)> values)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var scalar = await command.ExecuteScalarAsync(Token).ConfigureAwait(false);

        return scalar is null or DBNull ? null : scalar;
    }

    private static LinkTestHost Start(
        DatabaseFixture fixture,
        ILoggerProvider? logProvider,
        Action<IServiceCollection>? overrides,
        params FakeLoginProvider[] providers)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        return new LinkTestHost(fixture.AppConnectionString, providers, logProvider, overrides) { Fixture = fixture };
    }

    private static HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("User-Agent", UserAgent);

        return request;
    }

    private static HttpRequestMessage CallbackRequest(string provider, string? cookieValue, string? state)
    {
        var query = new List<string>();
        if (state is not null)
        {
            query.Add("state=" + Uri.EscapeDataString(state));
        }

        query.Add("code=authorization-code");

        var request = Request(
            HttpMethod.Get,
            "/v1/auth/oidc/" + provider + "/callback?" + string.Join("&", query));

        if (cookieValue is not null)
        {
            request.Headers.Add("Cookie", LoginStateCookie.Name + "=" + cookieValue);
        }

        return request;
    }

    private static StringContent JsonContent(object body) =>
        new StringContent(
            JsonSerializer.Serialize(body),
            System.Text.Encoding.UTF8,
            "application/json");
}
