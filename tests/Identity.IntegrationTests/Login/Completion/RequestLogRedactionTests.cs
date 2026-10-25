using System.Net;
using Evidgate.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Completion;

/// <summary>
/// 02.3.1-T1.4 / hand-over (viii) of #160 (<c>RequestLogRedactionTests</c>): the callback's <c>code</c> and
/// <c>state</c> never reach the request log at Information.
/// </summary>
/// <remarks>
/// <para>
/// The framework's own category <c>Microsoft.AspNetCore.Hosting.Diagnostics</c> logs "Request starting … {url}"
/// at Information, and that url carries the whole query string — the one-time <c>state</c> and the provider's
/// authorization <c>code</c>, exactly the pair the login must not keep. The host's logging defaults therefore
/// floor that category above Information (<c>ApiHost</c>), and this suite proves it on a REAL callback under the
/// host's own filters: the capturing provider is added to the same container, so the framework's filters apply
/// to it exactly as they do to the deployment's sinks. Remove the filter and the assertions below fail.
/// </para>
/// <para>
/// Two guards keep the case honest: the host's own <see cref="ILoggerFactory"/> is asked whether that category is
/// enabled at Information (a filter regression fails here directly), and the capture itself is exercised through
/// the same provider before the request, so "nothing was recorded" can never be an artefact of a dead provider.
/// </para>
/// </remarks>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class RequestLogRedactionTests : CatalogueTestBase
{
    private const string RequestLogCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";

    /// <summary>
    /// The non-vacuity probe: one Warning line written through the provider under test. A
    /// <see cref="LoggerMessage"/> delegate, because a bare <c>LogWarning</c> call is CA1848 at error severity.
    /// </summary>
    private static readonly Action<ILogger, string, Exception?> ProbeCapture =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, nameof(ProbeCapture)), "probe {Text}");

    /// <summary>The constructor the harness's assembly fixture flows into.</summary>
    /// <param name="fixture">The shared container, migrated database and reset seam.</param>
    public RequestLogRedactionTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>The callback request log keeps neither <c>code=</c> nor <c>state=</c> at Information.</summary>
    [Fact]
    public async Task The_Callback_Request_Log_Carries_No_Code_Or_State_At_Information()
    {
        using var logs = new CapturingLoggerProvider();
        using var host = LoginCompletionTestHost.StartWithLogs(
            Fixture,
            logs,
            LoginCompletionTestHost.GoogleProvider());
        using var client = host.CreateLoginClient();

        await LoginCompletionTestHost.EstablishMemberAsync(
            Fixture,
            host,
            client,
            LoginCompletionTestHost.Google,
            LoginCompletionTestHost.GoogleSubject);

        // The framework's own request-log category must not be enabled at Information, or the url would be
        // written by the deployment's sinks no matter what this provider captured.
        host.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(RequestLogCategory)
            .IsEnabled(LogLevel.Information)
            .ShouldBeFalse($"{RequestLogCategory} must be floored above Information (ApiHost's logging defaults)");

        // Non-vacuity: the very provider under test records a line written through it.
        ProbeCapture(
            logs.CreateLogger("capture-probe"),
            "code=probe-value state=probe-value",
            null);
        logs.Entries.ShouldContain(entry => entry.Message.Contains("code=probe-value", StringComparison.Ordinal));
        logs.Clear();

        using var completed = await host.SignInAsync(client, returnUrl: "/runs/42");
        completed.StatusCode.ShouldBe(HttpStatusCode.Found, "the request really went through the pipeline");

        var informational = logs.Entries.Where(entry => entry.Level <= LogLevel.Information).ToArray();

        informational.ShouldNotContain(
            entry => entry.Message.Contains("code=", StringComparison.Ordinal),
            "the provider's authorization code query must never be logged");
        informational.ShouldNotContain(
            entry => entry.Message.Contains("state=", StringComparison.Ordinal),
            "the one-time state query must never be logged");
        logs.Entries.ShouldNotContain(
            entry => entry.Message.Contains("authorization-code", StringComparison.Ordinal),
            "the code value itself must never appear at any level");
    }
}
