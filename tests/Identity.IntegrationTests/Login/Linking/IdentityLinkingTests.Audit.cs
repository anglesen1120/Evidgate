using System.Net;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Login.Audit;
using Evidgate.Modules.Identity.Login.Linking;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Linking;

/// <summary>
/// The audit half of the link confirm: the rows belong to the same transaction as the identity row, so a row
/// either exists with the link or the link did not happen, and only a first-time <c>linked</c>/<c>moved</c>
/// writes them.
/// </summary>
public sealed partial class IdentityLinkingTests
{
    /// <summary>
    /// One <c>auth.link</c> row in the chain of every ACTIVE org of the confirming user, and none in a suspended
    /// one: the audit follows the orgs the link actually affects. Every touched chain still verifies.
    /// </summary>
    [Fact]
    public async Task Confirm_WritesOneLinkRowInEveryActiveOrgAndNoneInASuspendedOne()
    {
        using var host = LinkTestHost.Start(
            Fixture,
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, Email));
        using var client = host.CreateLinkClient();

        var (userId, session, firstOrg) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);

        var secondOrg = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await host.SeedMembershipAsync(secondOrg, userId);

        var suspendedOrg = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await host.SeedMembershipAsync(suspendedOrg, userId);
        await host.ArrangeAsync(
            "update public.organization set status = 'suspended' where id = @id",
            ("id", suspendedOrg));

        var linkCookie = await StageAsync(host, client, LinkTestHost.GitHub, session);
        using var confirm = await ConfirmAsync(client, session, linkCookie);
        confirm.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LinkTestHost.MemberAsync(confirm, "outcome")).ShouldBe("linked");

        foreach (var orgId in new[] { firstOrg, secondOrg })
        {
            var links = (await host.AuditRowsAsync(orgId))
                .Where(row => row.Action == IdentityAuditActions.Link)
                .ToArray();

            links.Length.ShouldBe(1);
            links[0].ObjectId.ShouldBe(userId.ToString("D"));

            var metadata = links[0].MetadataJson.ShouldNotBeNull();
            metadata.ShouldContain(LinkTestHost.GitHub, Case.Sensitive);
            metadata.ShouldNotContain(Email, Case.Insensitive);

            (await host.VerifyChainAsync(orgId)).ShouldBeNull("the appended row is a valid chain entry");
        }

        (await host.AuditRowsAsync(suspendedOrg))
            .Where(row => row.Action is IdentityAuditActions.Link or IdentityAuditActions.LinkMove)
            .ShouldBeEmpty();
    }

    /// <summary>
    /// A repeat confirm answers <c>already_linked</c> and writes nothing: the rows of a link belong to the call
    /// that created the identity row, so a retry of a committed link appends no second <c>auth.link</c>.
    /// </summary>
    [Fact]
    public async Task Confirm_AlreadyLinked_WritesNoFurtherAuditRows()
    {
        using var host = LinkTestHost.Start(
            Fixture,
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, Email));
        using var client = host.CreateLinkClient();

        var (userId, session, orgId) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);

        var secondOrg = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await host.SeedMembershipAsync(secondOrg, userId);

        var linkCookie = await StageAsync(host, client, LinkTestHost.GitHub, session);
        using (var first = await ConfirmAsync(client, session, linkCookie))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await LinkTestHost.MemberAsync(first, "outcome")).ShouldBe("linked");
        }

        var before = await AuditLinkRowsAsync(host, orgId);
        var beforeSecond = await AuditLinkRowsAsync(host, secondOrg);

        before.ShouldBe(1);
        beforeSecond.ShouldBe(1);

        // The staged value the client kept, presented again: the database answers idempotently.
        using var again = await ConfirmAsync(client, session, linkCookie);

        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LinkTestHost.MemberAsync(again, "outcome")).ShouldBe("already_linked");

        (await AuditLinkRowsAsync(host, orgId)).ShouldBe(before, "already_linked is a link whose rows committed with it");
        (await AuditLinkRowsAsync(host, secondOrg)).ShouldBe(beforeSecond);

        // The suite's own count of the rows a confirm may write, so the two reads above share one definition.
        static async Task<long> AuditLinkRowsAsync(LinkTestHost host, Guid org) =>
            (await host.AuditRowsAsync(org))
                .Count(row => row.Action is IdentityAuditActions.Link or IdentityAuditActions.LinkMove);
    }

    /// <summary>
    /// The chain append is part of the link's transaction: when it fails, the confirm answers 503 and the
    /// identity row is ABSENT — nothing was half-written — and the retry of the same staging then links once.
    /// </summary>
    [Fact]
    public async Task Confirm_WhenTheAuditAppendFails_Answers503AndRollsBackTheLink()
    {
        using var host = LinkTestHost.Start(
            Fixture,
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, Email));
        using var client = host.CreateLinkClient();

        var (userId, session, orgId) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);

        var secondOrg = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await host.SeedMembershipAsync(secondOrg, userId);

        var linkCookie = await StageAsync(host, client, LinkTestHost.GitHub, session);

        try
        {
            await host.InstallAuditFaultAsync();

            using var failed = await ConfirmAsync(client, session, linkCookie);

            failed.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            (await LinkTestHost.ErrorCodeAsync(failed)).ShouldBe(ApiErrorCode.Unavailable);
            failed.Headers.RetryAfter.ShouldNotBeNull();
            LinkTestHost.CookieValue(failed, LinkConfirmCookie.Name).ShouldBeNull(
                "the staging is kept, so the verified identity survives the failed attempt");

            (await host.UserIdAsync(LinkTestHost.GitHub, GitHubSubject))
                .ShouldBeNull("the failed append aborted the call, so the identity row rolled back with it");

            foreach (var org in new[] { orgId, secondOrg })
            {
                (await host.AuditRowsAsync(org))
                    .Where(row => row.Action is IdentityAuditActions.Link or IdentityAuditActions.LinkMove)
                    .ShouldBeEmpty();
                (await host.VerifyChainAsync(org)).ShouldBeNull("the aborted append left the chain untouched");
            }
        }
        finally
        {
            await host.DropAuditFaultAsync();
        }

        using var retry = await ConfirmAsync(client, session, linkCookie);

        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LinkTestHost.MemberAsync(retry, "outcome")).ShouldBe("linked");
        (await host.UserIdAsync(LinkTestHost.GitHub, GitHubSubject)).ShouldBe(userId);

        foreach (var org in new[] { orgId, secondOrg })
        {
            var links = (await host.AuditRowsAsync(org))
                .Where(row => row.Action == IdentityAuditActions.Link)
                .ToArray();

            links.Length.ShouldBe(1);
            links[0].ObjectId.ShouldBe(userId.ToString("D"));
            links[0].MetadataJson.ShouldNotBeNull().ShouldContain(LinkTestHost.GitHub, Case.Sensitive);
            (await host.VerifyChainAsync(org)).ShouldBeNull();
        }
    }

    /// <summary>
    /// A confirm that MOVES the identity and then cannot audit it answers 503 with the move rolled back: the
    /// identity is still the source account's, no row exists anywhere, and the retry performs the move and
    /// audits it once.
    /// </summary>
    [Fact]
    public async Task Confirm_WhenTheAuditAppendFailsAfterAMove_RollsBackTheMove()
    {
        using var host = LinkTestHost.Start(
            Fixture,
            LinkTestHost.Provider(LinkTestHost.Google, GoogleSubject, Email),
            LinkTestHost.Provider(LinkTestHost.GitHub, GitHubSubject, MovableEmail));
        using var client = host.CreateLinkClient();

        var (userId, session, orgId) = await MemberAsync(host, client, LinkTestHost.Google, GoogleSubject);

        var secondOrg = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await host.SeedMembershipAsync(secondOrg, userId);

        // The source account: its first sign-in created the identity and was refused for want of a membership,
        // so it holds no membership and no live session and the identity may be moved.
        using (var source = await host.SignInAsync(client, LinkTestHost.GitHub))
        {
            source.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var movedFrom = (await host.UserIdAsync(LinkTestHost.GitHub, GitHubSubject))
            ?? throw new InvalidOperationException("the source sign-in created no identity");

        var linkCookie = await StageAsync(host, client, LinkTestHost.GitHub, session);

        try
        {
            await host.InstallAuditFaultAsync();

            using var failed = await ConfirmAsync(client, session, linkCookie);

            failed.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            (await LinkTestHost.ErrorCodeAsync(failed)).ShouldBe(ApiErrorCode.Unavailable);
            LinkTestHost.CookieValue(failed, LinkConfirmCookie.Name).ShouldBeNull();

            (await host.UserIdAsync(LinkTestHost.GitHub, GitHubSubject)).ShouldBe(
                movedFrom,
                "the move is part of the same transaction, so the failed append took it back");
            (await host.CountAsync(
                "select count(*) from public.user_identity where user_id = @id",
                ("id", movedFrom))).ShouldBe(1L);
            (await host.CountAsync(
                "select count(*) from public.user_identity where user_id = @id",
                ("id", userId))).ShouldBe(1L);

            foreach (var org in new[] { orgId, secondOrg })
            {
                (await host.AuditRowsAsync(org))
                    .Where(row => row.Action is IdentityAuditActions.Link or IdentityAuditActions.LinkMove)
                    .ShouldBeEmpty();
                (await host.VerifyChainAsync(org)).ShouldBeNull();
            }
        }
        finally
        {
            await host.DropAuditFaultAsync();
        }

        using var retry = await ConfirmAsync(client, session, linkCookie);

        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LinkTestHost.MemberAsync(retry, "outcome")).ShouldBe("moved");
        (await host.UserIdAsync(LinkTestHost.GitHub, GitHubSubject)).ShouldBe(userId);

        foreach (var org in new[] { orgId, secondOrg })
        {
            var rows = await host.AuditRowsAsync(org);
            var links = rows.Where(row => row.Action == IdentityAuditActions.Link).ToArray();
            var moves = rows.Where(row => row.Action == IdentityAuditActions.LinkMove).ToArray();

            links.Length.ShouldBe(1);
            links[0].ObjectId.ShouldBe(userId.ToString("D"));
            links[0].MetadataJson.ShouldNotBeNull().ShouldContain(LinkTestHost.GitHub, Case.Sensitive);

            moves.Length.ShouldBe(1);
            moves[0].ObjectId.ShouldBe(userId.ToString("D"));
            var metadata = moves[0].MetadataJson.ShouldNotBeNull();
            metadata.ShouldContain(LinkTestHost.GitHub, Case.Sensitive);
            metadata.ShouldContain("\"previous_id\"", Case.Sensitive);
            metadata.ShouldContain(movedFrom.ToString("D"), Case.Insensitive);

            (await host.VerifyChainAsync(org)).ShouldBeNull();
        }
    }
}
