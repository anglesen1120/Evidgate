using System.Diagnostics;
using Evidgate.Modules.Identity.Login;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// The install callback's query carries the OAuth <c>code</c> and the one-time <c>state</c>, so its span must carry
/// neither: the rule mirrors the login callback's and is asserted both on a hand-shaped span and on a real one.
/// </summary>
public sealed partial class GitHubInstallationTests
{
    private const string CallbackPath = "/v1/github/installations/callback";

    private const string SpanCode = "authorizationcodemarker";

    private const string SpanState = "statecodemarker";

    [Fact]
    public void The_Install_Callback_Span_Keeps_No_Query_And_No_Url_Query()
    {
        using var activity = CallbackActivity();

        LoginTraceRedaction.Redact(activity, CallbackPath).ShouldBeTrue();

        activity.GetTagItem(LoginTraceRedaction.QueryTag).ShouldBeNull();

        foreach (var tag in LoginTraceRedaction.UrlTags)
        {
            activity.GetTagItem(tag).ShouldBe("https://app.evidgate.test" + CallbackPath);
        }
    }

    [Theory]
    [InlineData(CallbackPath)]
    [InlineData(CallbackPath + "/")]
    public void The_Callback_Route_Is_Recognised_With_And_Without_A_Trailing_Slash(string path)
    {
        // Routing serves both forms, so a rule keyed on the exact path would leak the code and the state on one of
        // them while passing on the other.
        using var activity = CallbackActivity(path);

        LoginTraceRedaction.Redact(activity, path).ShouldBeTrue();

        activity.GetTagItem(LoginTraceRedaction.QueryTag).ShouldBeNull();

        foreach (var tag in LoginTraceRedaction.UrlTags)
        {
            activity.GetTagItem(tag).ShouldBe("https://app.evidgate.test" + path);
        }
    }

    [Fact]
    public void No_Remaining_Tag_Of_An_Install_Callback_Span_Carries_The_Code_Or_The_State()
    {
        using var activity = CallbackActivity();

        LoginTraceRedaction.Redact(activity, CallbackPath).ShouldBeTrue();

        foreach (var tag in activity.TagObjects)
        {
            (tag.Value?.ToString() ?? string.Empty).ShouldNotContain(SpanCode, Case.Sensitive);
            (tag.Value?.ToString() ?? string.Empty).ShouldNotContain(SpanState, Case.Sensitive);
        }
    }

    [Fact]
    public async Task The_Real_Callback_Span_Carries_No_Query()
    {
        // The helper test above names the tags itself; this one reads the span the host actually exported.
        using var spans = new SpanCollector();
        var (host, client, _, _, state, org) = await StartedAsync();
        await using var _ = host;

        using var callback = await CallbackAsync(client, state);
        callback.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        var served = await spans.WaitForAsync(CallbackPath, TestContext.Current.CancellationToken);
        served.ShouldNotBeEmpty("the ASP.NET Core instrumentation must produce a server span for the callback");

        foreach (var span in served)
        {
            span.HasQueryTag.ShouldBeFalse($"no install span may keep url.query ({span.DisplayName})");

            foreach (var tag in span.Tags)
            {
                var value = tag.Value?.ToString() ?? string.Empty;
                value.ShouldNotContain(SpanCode, Case.Sensitive);
                value.ShouldNotContain(NonceOf(state), Case.Sensitive);
            }
        }
    }

    /// <summary>An activity shaped like the instrumentation's callback span: query tag plus both URL tags.</summary>
    private static Activity CallbackActivity(string path = CallbackPath)
    {
        var url = "https://app.evidgate.test" + path + "?code=" + SpanCode + "&state=" + SpanState;
        var activity = new Activity("HTTP GET " + path);
        activity.SetTag(LoginTraceRedaction.QueryTag, "?code=" + SpanCode + "&state=" + SpanState);
        activity.SetTag("url.full", url);
        activity.SetTag("http.url", url);
        activity.SetTag("http.request.method", "GET");

        return activity;
    }

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

        // The server span stops after the response is flushed, so the client can see the response first.
        public async Task<List<RecordedSpan>> WaitForAsync(string path, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (true)
            {
                lock (_stopped)
                {
                    var found = _stopped
                        .Where(span => (span.UrlPath?.StartsWith(path, StringComparison.Ordinal) ?? false)
                            || span.DisplayName.Contains(path, StringComparison.Ordinal))
                        .ToList();

                    if (found.Count > 0 || DateTime.UtcNow >= deadline)
                    {
                        return found;
                    }
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
}
