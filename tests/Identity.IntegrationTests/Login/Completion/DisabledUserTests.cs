using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Testing;
using Identity.IntegrationTests.Invites;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Completion;

/// <summary>
/// 02.3.1-T2.2 / 02.3.1 §6: <c>user.status = disabled</c> refuses a login before anything else happens — no
/// bind, no audit row, no session, no cookie — and re-enabling the account admits it again through its
/// pending invite.
/// </summary>
/// <remarks>
/// The first two cases substitute the binder and the audit writer so the ORDER is observable: a disabled
/// account must not reach either seam at all. The re-enable case runs the deployment's own seams, so it proves
/// the whole path admits the account again rather than only that a flag was read.
/// </remarks>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class DisabledUserTests : BindTestBase
{
    /// <summary>The constructor the harness's assembly fixture flows into.</summary>
    /// <param name="fixture">The shared container, migrated database and reset seam.</param>
    public DisabledUserTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// A disabled account answers 403 <c>account_disabled</c> and nothing else happens: the binder is never
    /// called (so the invite it presented stays <c>pending</c>), the audit writer is never called, and no
    /// session row or cookie exists.
    /// </summary>
    [Fact]
    public async Task DisabledUser_AnswersAccountDisabled_WithoutBindAuditOrSession()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var invite = await SeedInviteAsync(org, "invitee@example.test");
        var binder = new RecordingMembershipBinder();
        var audit = new RecordingAuditWriter();

        using var host = LoginCompletionTestHost.Start(
            Fixture,
            new LoginHostSettings
            {
                InvitePepper = Pepper,
                InvitePepperVersion = PepperVersion,
                Binder = binder,
                AuditWriter = audit,
                Providers =
                [
                    LoginCompletionTestHost.IdentityProvider(
                        LoginCompletionTestHost.Google,
                        "google-subject-disabled",
                        invite.Email),
                ],
            });
        using var client = host.CreateLoginClient();

        // The first attempt creates the user row (403 no_membership) and reaches both seams once.
        using (var first = await host.SignInAsync(client))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var userId = await LoginCompletionTestHost
            .UserIdAsync(Fixture, LoginCompletionTestHost.Google, "google-subject-disabled");

        var bindCallsBefore = binder.Calls.Count;
        var auditWritesBefore = audit.Writes.Count;
        bindCallsBefore.ShouldBe(1, "a live account offers its identity to the binder");
        auditWritesBefore.ShouldBe(0, "no org, so no chain to extend");

        // Disabling is the owner's act (02.3.1 §7 later stories); the harness owner connection is that operator.
        (await LoginCompletionTestHost.SetUserStatusAsync(Fixture, userId, "disabled"))
            .ShouldBe(1);

        using var callback = await host.SignInWithInviteAsync(client, invite.Token);

        callback.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await LoginCompletionTestHost.ErrorCodeAsync(callback))
            .ShouldBe(ApiErrorCode.AccountDisabled);
        binder.Calls.Count.ShouldBe(bindCallsBefore, "a disabled account is refused before the bind");
        audit.Writes.Count.ShouldBe(auditWritesBefore, "and before any audit row");
        LoginCompletionTestHost.SessionCookies(callback).ShouldBeEmpty();
        (await LoginCompletionTestHost.CountAsync(Fixture, "select count(*) from public.session"))
            .ShouldBe(0);
        (await LoginCompletionTestHost.MembershipStatusAsync(Fixture, invite.MembershipId))
            .ShouldBe(MembershipStatus.Pending, "the invitation survives the refused login");
    }

    /// <summary>
    /// Re-enabling the account admits it again: the next login with the invitation's token binds the pending
    /// membership and completes with a session and a cookie.
    /// </summary>
    [Fact]
    public async Task ReEnabledUser_IsAdmittedThroughTheInvite()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var invite = await SeedInviteAsync(org, "invitee@example.test");

        using var host = LoginCompletionTestHost.Start(
            Fixture,
            new LoginHostSettings
            {
                InvitePepper = Pepper,
                InvitePepperVersion = PepperVersion,
                Providers =
                [
                    LoginCompletionTestHost.IdentityProvider(
                        LoginCompletionTestHost.Google,
                        "google-subject-reenabled",
                        invite.Email),
                ],
            });
        using var client = host.CreateLoginClient();

        // No token: the user row is created and the account is refused for want of a membership.
        using (var first = await host.SignInAsync(client))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var userId = await LoginCompletionTestHost
            .UserIdAsync(Fixture, LoginCompletionTestHost.Google, "google-subject-reenabled");

        (await LoginCompletionTestHost.SetUserStatusAsync(Fixture, userId, "disabled"))
            .ShouldBe(1);

        using (var refused = await host.SignInWithInviteAsync(client, invite.Token))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await LoginCompletionTestHost.ErrorCodeAsync(refused))
                .ShouldBe(ApiErrorCode.AccountDisabled);
        }

        (await LoginCompletionTestHost.SetUserStatusAsync(Fixture, userId, "active"))
            .ShouldBe(1);

        using var admitted = await host.SignInWithInviteAsync(client, invite.Token);

        admitted.StatusCode.ShouldBe(HttpStatusCode.Found);
        LoginCompletionTestHost.SessionCookieValue(admitted).ShouldNotBeNull();
        (await LoginCompletionTestHost.MembershipStatusAsync(Fixture, invite.MembershipId))
            .ShouldBe(MembershipStatus.Active);
        (await LoginCompletionTestHost.MembershipUserIdAsync(Fixture, invite.MembershipId))
            .ShouldBe(userId);
    }
}
