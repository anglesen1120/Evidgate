using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Login;
using Evidgate.Modules.Identity.Login.Linking;
using Evidgate.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Linking;

/// <summary>What a staging carries, and what it must never carry or leak.</summary>
public sealed partial class IdentityLinkingTests : TenantTest
{

    /// <summary>
    /// A provider profile name longer than the staging carries is DROPPED, never a refusal: the name is the one
    /// field no column of the write path owns, and refusing the identity over it would lock a real person out of
    /// linking (F2; Google's validator accepts names up to 256 characters, this cookie stages 200).
    /// </summary>
    [Fact]
    public async Task Callback_OfAnOverLongProfileName_StagesTheIdentityWithoutTheName()
    {
        using var host = LinkTestHost.Start(
            Fixture,
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, MovableEmail));
        using var client = host.CreateLinkClient();

        var (userId, session, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);

        host.Use(
            LinkTestHost.GitHub,
            LinkTestHost.Identity(LinkTestHost.GitHub, GitHubSubject, MovableEmail)
                with
            { DisplayName = new string('n', 256) });

        var staged = await StageAsync(host, client, LinkTestHost.GitHub, session);

        var payload = host.ReadStagedLink(staged);
        payload.DisplayName.ShouldBeNull("an over-long name is dropped, never truncated into a different name");
        payload.Subject.ShouldBe(GitHubSubject, "the identity itself is staged");

        using (var pending = await PendingAsync(client, session, staged))
        {
            pending.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await LinkTestHost.MemberAsync(pending, "display_name")).ShouldBeNullOrEmpty();
        }

        using var confirm = await ConfirmAsync(client, session, staged);

        confirm.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LinkTestHost.MemberAsync(confirm, "outcome")).ShouldBe("linked");
        (await host.UserIdAsync(LinkTestHost.GitHub, GitHubSubject)).ShouldBe(userId);
        (await host.IdentityEmailAsync(LinkTestHost.GitHub, GitHubSubject)).ShouldBe(MovableEmail);
    }

    // ------------------------------------------------------------------ the staged value

    /// <summary>
    /// The staged value is confidential: it carries no plaintext address, subject or display name, only the
    /// protected payload this host's ring can read back.
    /// </summary>
    [Fact]
    public async Task StagedCookie_CarriesNoPlaintext()
    {
        using var host = LinkTestHost.Start(
            Fixture,
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, "staged@example.test"));
        using var client = host.CreateLinkClient();

        var (userId, session, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);
        var linkCookie = await StageAsync(host, client, LinkTestHost.GitHub, session);

        const string StagedEmail = "staged@example.test";

        linkCookie.ShouldNotContain(StagedEmail, Case.Insensitive);
        linkCookie.ShouldNotContain("staged", Case.Insensitive);
        linkCookie.ShouldNotContain(GitHubSubject, Case.Insensitive);
        linkCookie.ShouldNotContain("Test Member", Case.Insensitive);
        linkCookie.ShouldNotContain(userId.ToString("D"), Case.Insensitive);

        host.ReadStagedLink(linkCookie).UserId.ShouldBe(userId);
    }

    // ------------------------------------------------------------------ nothing sensitive on the wire

    /// <summary>
    /// No cookie value of the linking flow reaches a log line or a <c>Location</c> header: the staged link and the
    /// session stay in cookies and are never echoed into a redirect or the diagnostics.
    /// </summary>
    /// <remarks>
    /// Two things the login suites already pin, restated here because the linking routes share the pipeline: the
    /// framework's own request-log category is floored above Information, and the start route's provider URL — which
    /// carries the one-time state the browser is being sent to anyway — is logged legibly. So the capture is cleared
    /// after the start and the state is asserted against the callback, <c>pending</c> and <c>confirm</c> only.
    /// </remarks>
    [Fact]
    public async Task CookieValues_NeverReachLogsOrLocationHeaders()
    {
        var logs = new LinkTestHost.CapturingLoggerProvider();

        using var host = LinkTestHost.StartWithLogs(
            Fixture,
            logs,
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, Email));
        using var client = host.CreateLinkClient();

        var (_, session, _) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);

        // The deployment's logging floor: the request log cannot write the callback's query at Information.
        host.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics")
            .IsEnabled(LogLevel.Information)
            .ShouldBeFalse("ApiHost floors the request log above Information");

        // Non-vacuity: the provider under test really records a line written through it (CA1848: a LoggerMessage
        // delegate, not a bare LogWarning).
        CaptureProbe(logs.CreateLogger("capture-probe"), "code=probe state=probe", null);
        logs.Entries.ShouldContain(entry => entry.Message.Contains("code=probe", StringComparison.Ordinal));
        logs.Clear();

        var start = await StartLinkAsync(host, client, LinkTestHost.GitHub, session);
        using var startResponse = start.Response;
        var stateCookie = start.StateCookie;
        var state = start.Payload.State;

        // The start legitimately logs the provider URL; nothing after it may log any of these values.
        logs.Clear();

        using var callback = await LinkTestHost.LinkCallbackAsync(
            client,
            LinkTestHost.GitHub,
            stateCookie,
            state,
            session);
        var linkCookie = LinkTestHost.CookieValue(callback, LinkConfirmCookie.Name)
            ?? throw new InvalidOperationException("the callback staged no link cookie");

        using var pending = await PendingAsync(client, session, linkCookie);
        using var confirm = await ConfirmAsync(client, session, linkCookie);

        confirm.StatusCode.ShouldBe(HttpStatusCode.OK);

        var sensitive = new[]
        {
            session.Session,
            session.Antiforgery,
            session.RequestToken,
            stateCookie,
            state,
            linkCookie,
        };

        foreach (var response in new[] { startResponse, callback, pending, confirm })
        {
            var location = response.Headers.Location?.ToString();
            if (location is null)
            {
                continue;
            }

            foreach (var value in sensitive)
            {
                location.ShouldNotContain(value, Case.Insensitive);
            }
        }

        // The callback's query is the real leak vector: neither the code nor the state parameter may be logged, and
        // the authorization code value this host sends must not appear at any level either.
        logs.Entries.ShouldNotContain(
            entry => entry.Message.Contains("code=", StringComparison.Ordinal),
            "the provider's authorization code must never be logged");
        logs.Entries.ShouldNotContain(
            entry => entry.Message.Contains("state=", StringComparison.Ordinal),
            "the one-time state query must never be logged");
        logs.Entries.ShouldNotContain(
            entry => entry.Message.Contains("authorization-code", StringComparison.Ordinal),
            "the code value itself must never appear at any level");

        foreach (var entry in logs.Entries)
        {
            foreach (var value in sensitive)
            {
                entry.Message.ShouldNotContain(value, Case.Insensitive);
            }
        }
    }
}
