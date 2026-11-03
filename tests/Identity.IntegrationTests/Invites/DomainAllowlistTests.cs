using Evidgate.Modules.Identity.Contracts;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Invites;

/// <summary>
/// 02.3.1-T2 AC5 / D-S81-Q3: <c>organization.allowed_email_domains</c> is enforced per org
/// inside the bind, for <c>google</c> identities only. An off-list verified address leaves that
/// org's invite <c>pending</c> — the same empty answer every other refusal gives — while another
/// org of the same call, or the same org for an on-list address, still binds.
/// </summary>
/// <remarks>
/// The gate runs on both bind paths: <c>hd</c>-based auto-bind (the case AC5 names) and the
/// invite-secret path (an invite exists, the identity is off-list, the seat stays
/// <c>pending</c>). GitHub identities are never gated, so the same off-list org binds them.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class DomainAllowlistTests : BindTestBase
{
    public DomainAllowlistTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// The AC5 case in one call: org A restricts its domains and stays <c>pending</c>, org B
    /// restricts nothing and binds the same verified address. Neither refusal leaks into the
    /// other org, because every org is its own transaction.
    /// </summary>
    [Fact]
    public async Task OffDomainStaysPendingForTheRestrictedOrg_WhileTheUnrestrictedOrgBinds()
    {
        var restricted = await SeedOrgAsync(autobindVerifiedDomain: true);
        var unrestricted = await SeedOrgAsync(autobindVerifiedDomain: true);
        await SetAllowedDomainsAsync(restricted, ["partner.example"]);
        var address = Unique.Email("invitee");
        var restrictedInvite = await SeedInviteAsync(restricted, address);
        var unrestrictedInvite = await SeedInviteAsync(unrestricted, address);
        var userId = await SeedUserAsync();

        var result = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(
                userId,
                "google",
                address,
                EmailVerified: true,
                InviteToken: null,
                HostedDomain: "example.com"),
            Token);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Bound.Count.ShouldBe(1);
        result.Value.Bound[0].OrgId.ShouldBe(unrestricted);
        result.Value.Bound[0].MembershipId.ShouldBe(unrestrictedInvite.MembershipId);

        (await MembershipStateAsync(restricted, restrictedInvite.MembershipId)).Status
            .ShouldBe(MembershipStatus.Pending, "an off-list domain binds nothing in that org");
        var (status, boundUser) = await MembershipStateAsync(unrestricted, unrestrictedInvite.MembershipId);
        status.ShouldBe(MembershipStatus.Active);
        boundUser.ShouldBe(userId);
    }

    /// <summary>An address whose domain the list names binds normally through the secret path.</summary>
    [Fact]
    public async Task OnDomainAddress_BindsThroughTheInviteSecret()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        await SetAllowedDomainsAsync(org, ["example.com"]);
        var invite = await SeedInviteAsync(org, Unique.Email("invitee"));
        var userId = await SeedUserAsync();

        var result = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(userId, "google", invite.Email, EmailVerified: true, invite.Token, HostedDomain: null),
            Token);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Bound.Count.ShouldBe(1);
        result.Value.Bound[0].Via.ShouldBe("invite_secret");
        (await MembershipStateAsync(org, invite.MembershipId)).Status.ShouldBe(MembershipStatus.Active);
    }

    /// <summary>
    /// A listed domain admits only itself: <c>example.com</c> does not admit
    /// <c>sub.example.com</c>, so a Workspace account on a subdomain stays <c>pending</c> —
    /// and the same invite binds once the list names the subdomain exactly.
    /// </summary>
    [Fact]
    public async Task SubdomainIsNotAdmittedByItsParentDomain()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: true);
        await SetAllowedDomainsAsync(org, ["example.com"]);
        var invite = await SeedInviteAsync(org, Unique.Text("invitee") + "@sub.example.com");

        var refused = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(
                await SeedUserAsync(),
                "google",
                invite.Email,
                EmailVerified: true,
                InviteToken: null,
                HostedDomain: "sub.example.com"),
            Token);

        refused.IsSuccess.ShouldBeTrue();
        refused.Value.Bound.ShouldBeEmpty("the parent domain is not a suffix match");
        (await MembershipStateAsync(org, invite.MembershipId)).Status.ShouldBe(MembershipStatus.Pending);

        await SetAllowedDomainsAsync(org, ["sub.example.com"]);

        var admitted = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(
                await SeedUserAsync(),
                "google",
                invite.Email,
                EmailVerified: true,
                InviteToken: null,
                HostedDomain: "sub.example.com"),
            Token);

        admitted.Value.Bound.Count.ShouldBe(1, "the exact domain admits it");
        (await MembershipStateAsync(org, invite.MembershipId)).Status.ShouldBe(MembershipStatus.Active);
    }

    /// <summary>A NULL list and an empty list both mean "this org restricts nothing".</summary>
    [Fact]
    public async Task NullAndEmptyLists_RestrictNothing()
    {
        var nullList = await SeedOrgAsync(autobindVerifiedDomain: true);
        var emptyList = await SeedOrgAsync(autobindVerifiedDomain: true);
        await SetAllowedDomainsAsync(emptyList, []);
        var address = Unique.Email("invitee");
        var nullListInvite = await SeedInviteAsync(nullList, address);
        var emptyListInvite = await SeedInviteAsync(emptyList, address);

        var result = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(
                await SeedUserAsync(),
                "google",
                address,
                EmailVerified: true,
                InviteToken: null,
                HostedDomain: "example.com"),
            Token);

        result.Value.Bound.Count.ShouldBe(2);
        (await MembershipStateAsync(nullList, nullListInvite.MembershipId)).Status.ShouldBe(MembershipStatus.Active);
        (await MembershipStateAsync(emptyList, emptyListInvite.MembershipId)).Status.ShouldBe(MembershipStatus.Active);
    }

    /// <summary>
    /// The allowlist restricts Google logins only. The same off-list org binds a GitHub identity
    /// that presents the invite secret, and refuses the same invite presented by Google — so the
    /// difference is the provider and not the invite.
    /// </summary>
    [Fact]
    public async Task GitHubIsNotGated_GoogleIs()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: true);
        await SetAllowedDomainsAsync(org, ["partner.example"]);
        var githubInvite = await SeedInviteAsync(org, Unique.Email("invitee"));
        var googleInvite = await SeedInviteAsync(org, Unique.Email("other"));

        var github = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(await SeedUserAsync(), "github", githubInvite.Email, EmailVerified: true, githubInvite.Token, HostedDomain: null),
            Token);

        github.Value.Bound.Count.ShouldBe(1, "GitHub identities are never domain-gated");
        (await MembershipStateAsync(org, githubInvite.MembershipId)).Status.ShouldBe(MembershipStatus.Active);

        var google = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(await SeedUserAsync(), "google", googleInvite.Email, EmailVerified: true, googleInvite.Token, HostedDomain: null),
            Token);

        google.IsSuccess.ShouldBeTrue();
        google.Value.Bound.ShouldBeEmpty("an off-list Google identity binds nothing, token or not");
        (await MembershipStateAsync(org, googleInvite.MembershipId)).Status.ShouldBe(MembershipStatus.Pending);
    }

    /// <summary>
    /// The list is admin-written data: entries may carry mixed case and surrounding whitespace,
    /// and both are normalised away rather than silently disabling the gate.
    /// </summary>
    [Fact]
    public async Task MixedCaseAndWhitespaceEntriesAreNormalised()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: true);
        await SetAllowedDomainsAsync(org, ["  Example.COM  ", "Partner.Example"]);
        var invite = await SeedInviteAsync(org, Unique.Text("Invitee") + "@Example.com");

        var result = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(
                await SeedUserAsync(),
                "google",
                invite.Email,
                EmailVerified: true,
                InviteToken: null,
                HostedDomain: "Example.com"),
            Token);

        result.Value.Bound.Count.ShouldBe(1);
        (await MembershipStateAsync(org, invite.MembershipId)).Status.ShouldBe(MembershipStatus.Active);
    }

    /// <summary>
    /// F2: an entry written with the trailing DNS root dot (<c>example.com.</c>) is the same domain as the plain
    /// one, so it admits the address instead of locking the org out of every Google bind over one admin typo.
    /// </summary>
    [Fact]
    public async Task TrailingDotEntry_AdmitsTheSameDomainThroughTheAutoBind()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: true);
        await SetAllowedDomainsAsync(org, ["Example.com."]);
        var invite = await SeedInviteAsync(org, Unique.Email("invitee"));
        var userId = await SeedUserAsync();

        var result = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(
                userId,
                "google",
                invite.Email,
                EmailVerified: true,
                InviteToken: null,
                HostedDomain: "example.com"),
            Token);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Bound.Count.ShouldBe(1, "a fully qualified entry names the same domain");
        result.Value.Bound[0].OrgId.ShouldBe(org);
        var (status, boundUser) = await MembershipStateAsync(org, invite.MembershipId);
        status.ShouldBe(MembershipStatus.Active);
        boundUser.ShouldBe(userId);
    }

    /// <summary>
    /// F2's other half: an entry that normalises to nothing (<c>.</c> alone) is still a restriction the admin
    /// wrote. It admits no address — and it must never turn the list into "this org restricts nothing", which
    /// would open the org to every domain instead of closing it.
    /// </summary>
    [Fact]
    public async Task DotOnlyEntry_RestrictsWithoutAdmittingAnything()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: true);
        await SetAllowedDomainsAsync(org, ["."]);
        var invite = await SeedInviteAsync(org, Unique.Email("invitee"));

        var result = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(
                await SeedUserAsync(),
                "google",
                invite.Email,
                EmailVerified: true,
                InviteToken: null,
                HostedDomain: "example.com"),
            Token);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Bound.ShouldBeEmpty("a dot-only entry matches no domain");
        (await MembershipStateAsync(org, invite.MembershipId)).Status
            .ShouldBe(MembershipStatus.Pending, "and it does not open the org either");
    }

    /// <summary>Writes the org's allowlist as the owner: null clears the restriction (00 §5.5 C3).</summary>
    private Task<int> SetAllowedDomainsAsync(Guid orgId, string[]? domains) => Fixture.SeedAsync(
        "update public.organization set allowed_email_domains = @domains::text[] where id = @org_id",
        [("domains", (object?)domains ?? DBNull.Value), ("org_id", orgId)],
        Token);
}
