using System.Diagnostics;
using Evidgate.Modules.Identity.Login;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login;

/// <summary>
/// 02.3.1-T1.1 (F2/S3): the authorization <c>code</c> and <c>state</c> never reach a trace. The ASP.NET Core
/// instrumentation copies the callback's query string into the span, so
/// <see cref="LoginTraceRedaction"/> takes it off again on those two routes only.
/// </summary>
// The assembly Fixture of this project (DatabaseFixture) needs Docker, so the smoke build, which has none, skips Category=Docker.
[Trait("Category", "Docker")]
public sealed class LoginTraceRedactionTests(DatabaseFixture fixture)
    : CatalogueTestBase(fixture, DatabaseIsolation.Dedicated)
{
    private const string CallbackPath = "/v1/auth/oidc/google/callback";

    private const string Code = "4/authorization-code-value";

    private const string State = "state-value-that-must-not-be-exported";

    [Fact]
    public void A_Login_Callback_Activity_Keeps_No_Query_And_No_Url_Query()
    {
        using var activity = CallbackActivity();

        LoginTraceRedaction.Redact(activity, CallbackPath).ShouldBeTrue();

        activity.GetTagItem(LoginTraceRedaction.QueryTag).ShouldBeNull();

        foreach (var tag in LoginTraceRedaction.UrlTags)
        {
            activity.GetTagItem(tag).ShouldBe("https://app.evidgate.test" + CallbackPath);
        }
    }

    [Fact]
    public void No_Remaining_Tag_Of_A_Login_Activity_Carries_The_Code_Or_The_State()
    {
        using var activity = CallbackActivity();

        LoginTraceRedaction.Redact(activity, CallbackPath).ShouldBeTrue();

        foreach (var tag in activity.TagObjects)
        {
            (tag.Value?.ToString() ?? string.Empty).ShouldNotContain(Code, Case.Sensitive);
            (tag.Value?.ToString() ?? string.Empty).ShouldNotContain(State, Case.Sensitive);
        }
    }

    [Fact]
    public void Both_Login_Routes_Are_Redacted_Case_Insensitively()
    {
        using var activity = CallbackActivity();

        LoginTraceRedaction.Redact(activity, "/v1/auth/oidc/google/START").ShouldBeTrue();

        activity.GetTagItem(LoginTraceRedaction.QueryTag).ShouldBeNull();
    }

    [Fact]
    public void Another_Route_Is_Left_Untouched()
    {
        using var activity = CallbackActivity();

        LoginTraceRedaction.Redact(activity, "/v1/ci/token").ShouldBeFalse();

        activity.GetTagItem(LoginTraceRedaction.QueryTag).ShouldNotBeNull();
        activity.GetTagItem("url.full").ShouldBe(CallbackUrl());
    }

    [Fact]
    public void A_Request_Without_An_Activity_Is_A_No_Op()
    {
        LoginTraceRedaction.Redact(activity: null, CallbackPath).ShouldBeFalse();
    }

    [Theory]
    [InlineData("https://app.evidgate.test/v1/auth/oidc/google/callback?code=1&state=2", "https://app.evidgate.test/v1/auth/oidc/google/callback")]
    [InlineData("https://app.evidgate.test//callback#fragment", "https://app.evidgate.test//callback")]
    [InlineData("https://app.evidgate.test/v1/ci/token", "https://app.evidgate.test/v1/ci/token")]
    public void A_Url_Loses_Its_Query_And_Fragment(string url, string expected)
    {
        LoginTraceRedaction.WithoutQuery(url).ShouldBe(expected);
    }

    /// <summary>An activity shaped like the instrumentation's callback span: query tag plus both URL tags.</summary>
    private static Activity CallbackActivity()
    {
        var activity = new Activity("HTTP GET " + CallbackPath);
        activity.SetTag(LoginTraceRedaction.QueryTag, "?code=" + Code + "&state=" + State);
        activity.SetTag("url.full", CallbackUrl());
        activity.SetTag("http.url", CallbackUrl());
        activity.SetTag("http.request.method", "GET");

        return activity;
    }

    [Fact]
    public async Task The_Span_Of_A_Real_Callback_Carries_No_Query_And_No_Code_Or_State()
    {
        // The helper test above calls the redaction with the tag names of the helper, so it cannot notice a
        // wrong OTel tag name or a missing ApiHost hook. This probe reads a real server span instead and
        // names the instrumentation's own tags literally.
        using var spans = new SpanCollector();
        using var host = LoginTestHost.Start(
            Fixture,
            new MutableClock(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero)),
            new FakeLoginProvider("fake"));
        using var client = host.CreateLoginClient();

        using var response = await LoginTestHost.SendAsync(
            client,
            LoginTestHost.CallbackRequest("fake", cookieValue: null, state: WireState, code: WireCode));

        var login = await spans.WaitForSpansUnderAsync(OidcPath, TestContext.Current.CancellationToken);
        login.ShouldNotBeEmpty("the ASP.NET Core instrumentation must produce a server span for the callback");

        foreach (var span in login)
        {
            span.HasQueryTag.ShouldBeFalse($"no login span may keep url.query ({span.DisplayName})");

            foreach (var tag in span.Tags)
            {
                var value = tag.Value?.ToString() ?? string.Empty;
                value.ShouldNotContain(WireCode, Case.Sensitive);
                value.ShouldNotContain(WireState, Case.Sensitive);
            }
        }
    }

    /// <summary>The path prefix the login routes live under.</summary>
    private const string OidcPath = "/v1/auth/oidc/";

    /// <summary>
    /// Markers with no character a URL escapes, so a leaked query string keeps them literally (the
    /// constants above are compared against tags the test sets itself).
    /// </summary>
    private const string WireCode = "authorizationcodemarker";

    private const string WireState = "statecodemarker";

    /// <summary>The server spans of this process, snapshotted when each one stops.</summary>
    private sealed class SpanCollector : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly List<RecordedSpan> _stopped = [];

        public SpanCollector()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    var tags = activity.TagObjects.ToList();
                    lock (_stopped)
                    {
                        _stopped.Add(new RecordedSpan(
                            activity.DisplayName,
                            activity.GetTagItem("url.path") as string,
                            tags.Any(tag => tag.Key == "url.query"),
                            tags));
                    }
                },
            };

            ActivitySource.AddActivityListener(_listener);
        }

        public List<RecordedSpan> SpansUnder(string path)
        {
            lock (_stopped)
            {
                return _stopped
                    .Where(span => (span.UrlPath?.StartsWith(path, StringComparison.Ordinal) ?? false)
                        || span.DisplayName.Contains(path, StringComparison.Ordinal))
                    .ToList();
            }
        }

        // The server span stops after the response is flushed, so the client can see the response first.
        // Poll for the span with a bounded wait instead of reading the list once.
        public async Task<List<RecordedSpan>> WaitForSpansUnderAsync(string path, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (true)
            {
                var found = SpansUnder(path);
                if (found.Count > 0 || DateTime.UtcNow >= deadline)
                {
                    return found;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>One stopped span: what the probe asserts on, copied before the span is disposed.</summary>
    private sealed record RecordedSpan(
        string DisplayName,
        string? UrlPath,
        bool HasQueryTag,
        IReadOnlyList<KeyValuePair<string, object?>> Tags);

    private static string CallbackUrl() =>
        "https://app.evidgate.test" + CallbackPath + "?code=" + Code + "&state=" + State;
}
