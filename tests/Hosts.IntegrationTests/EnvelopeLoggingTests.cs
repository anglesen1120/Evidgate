using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// What the envelope middleware logs: a request-binding failure is a 400 that is not an error,
/// a <see cref="JsonException"/> raised by server code is a 500 like any other bug, an unhandled
/// failure is logged exactly once at Error, and a client that went away is not logged at all.
/// </summary>
public sealed class EnvelopeLoggingTests
{
    private const string DeadConnectionString =
        "Host=127.0.0.1;Port=1;Username=evidgate_app;Password=y;Database=evidgate;Timeout=2";

    private const string BodyCanary = "body-canary-9f2c";

    [Fact]
    public async Task Malformed_Json_Body_Is_A_400_Without_An_Error_Log()
    {
        var capture = new CaptureLoggerProvider();
        await using var factory = NewFactory(capture);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            Evidgate.Testing.TransportProbeStartupFilter.Path,
            new StringContent("{ not json " + BodyCanary, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(capture.Logs, log => log.Level >= LogLevel.Warning);
        Assert.DoesNotContain(capture.Logs, log => log.Text.Contains(BodyCanary, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Binding_Failure_Of_A_Minimal_Api_Route_Is_A_400_Without_An_Error_Log()
    {
        var capture = new CaptureLoggerProvider();
        await using var factory = NewFactory(capture);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            BoundRoute,
            new StringContent("{ not json " + BodyCanary, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(capture.Logs, log => log.Level >= LogLevel.Warning);
        Assert.DoesNotContain(capture.Logs, log => log.Text.Contains(BodyCanary, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(JsonBugRoute)]
    [InlineData(BoomRoute)]
    public async Task Unhandled_Server_Failure_Is_A_500_And_Exactly_One_Error_Log(string route)
    {
        var capture = new CaptureLoggerProvider();
        await using var factory = NewFactory(capture);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(route, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("internal_error", document.RootElement.GetProperty("error").GetProperty("code").GetString());

        var errors = capture.Logs.Where(log => log.Level >= LogLevel.Warning).ToArray();
        var only = Assert.Single(errors);
        Assert.Equal(LogLevel.Error, only.Level);
        Assert.Equal(1201, only.EventId.Id);
    }

    [Fact]
    public async Task A_Cancelled_Request_Is_Not_Logged_As_An_Error()
    {
        var capture = new CaptureLoggerProvider();
        await using var factory = NewFactory(capture);
        using var client = factory.CreateClient();

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync(HangRoute, cancel.Token));

        // Let the server side observe the abort and unwind before asserting an absence.
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(capture.Logs, log => log.Level >= LogLevel.Warning);
    }

    private const string JsonBugRoute = "/v1/test/json-bug";
    private const string BoomRoute = "/v1/test/boom";
    private const string HangRoute = "/v1/test/hang";
    private const string BoundRoute = "/v1/test/bound";

    private static EvidgateApiFactory NewFactory(CaptureLoggerProvider capture) =>
        new(DeadConnectionString, capture, services => services.AddSingleton<IStartupFilter, FailingRoutesStartupFilter>());

    private sealed record BoundBody(string? Name);

    private sealed class FailingRoutesStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            if (app is IEndpointRouteBuilder endpoints)
            {
                Map(endpoints);
                return;
            }

            app.UseEndpoints(Map);
        };

        private static void Map(IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet(JsonBugRoute, () => JsonSerializer.Deserialize<BoundBody>("{ broken") is null ? Results.Ok() : Results.Ok())
                .ExcludeFromDescription();
            endpoints.MapGet(BoomRoute, () => (IResult)throw new InvalidOperationException("boom"))
                .ExcludeFromDescription();
            endpoints.MapGet(HangRoute, async (HttpContext context) =>
            {
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
                return Results.Ok();
            }).ExcludeFromDescription();
            endpoints.MapPost(BoundRoute, (BoundBody body) => Results.Ok(body.Name)).ExcludeFromDescription();
        }
    }
}
