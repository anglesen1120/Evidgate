using Evidgate.Modules.Identity.Contracts;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Invites;

/// <summary>
/// D-S81-Q5: a bind is audited inside its own org transaction — one <c>membership.bind</c> entry
/// in that org's chain for a commit, none at all for a refusal, and metadata that names the path
/// (<c>via</c>) without the address or the invite secret.
/// </summary>
/// <remarks>
/// The rows are read as the owner (00 §5.5 C3): the assertion is about what the committed
/// transaction left behind, not about what the app role may see. A fresh org per test means
/// <c>seq = 1</c> is the whole chain, so "exactly one entry" is also "the first entry".
/// </remarks>
[Trait("Category", "Docker")]
public sealed class BindAuditTests : BindTestBase
{
    public BindAuditTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// The secret path: one entry, actor <c>user</c> with the bound user's id, object
    /// <c>membership</c> with the activated row's id, and <c>via = invite_secret</c> — and
    /// nothing in the row's text that names the invitee or the token.
    /// </summary>
    [Fact]
    public async Task SuccessfulBind_AppendsExactlyOneMembershipBindEntry()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var invite = await SeedInviteAsync(org, "invitee@example.com");
        var userId = await SeedUserAsync();

        var result = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(userId, "github", invite.Email, EmailVerified: true, invite.Token, HostedDomain: null),
            Token);

        result.Value.Bound.Count.ShouldBe(1);

        (await ScalarOwnerAsync<long>(
            "select count(*) from public.audit_log where org_id = @org_id and action = 'membership.bind'",
            ("org_id", org))).ShouldBe(1, "one bind, one entry");

        (await ScalarOwnerAsync<long>(
            "select seq from public.audit_log where org_id = @org_id and action = 'membership.bind'",
            ("org_id", org))).ShouldBe(1, "the bind is the org's first audited action");

        (await ScalarOwnerAsync<string>(
            "select actor_type from public.audit_log where org_id = @org_id and action = 'membership.bind'",
            ("org_id", org))).ShouldBe("user");

        (await ScalarOwnerAsync<string>(
            "select actor_id from public.audit_log where org_id = @org_id and action = 'membership.bind'",
            ("org_id", org))).ShouldBe(userId.ToString("D"));

        (await ScalarOwnerAsync<string>(
            "select object_type from public.audit_log where org_id = @org_id and action = 'membership.bind'",
            ("org_id", org))).ShouldBe("membership");

        (await ScalarOwnerAsync<string>(
            "select object_id from public.audit_log where org_id = @org_id and action = 'membership.bind'",
            ("org_id", org))).ShouldBe(invite.MembershipId.ToString("D"));

        var metadata = await TextOwnerAsync(
            "select metadata::text from public.audit_log where org_id = @org_id and action = 'membership.bind'",
            ("org_id", org));

        metadata.ShouldContain("invite_secret");

        // No address, no token half, no prefix anywhere in the stored entry — the row is scanned
        // as text, so a nested or renamed field cannot hide one.
        var row = await TextOwnerAsync(
            "select to_jsonb(a)::text from public.audit_log a where a.org_id = @org_id",
            ("org_id", org));

        row.ShouldNotContain(invite.Email);
        row.ShouldNotContain(invite.Prefix);
        row.ShouldNotContain(invite.Token);
    }

    /// <summary>
    /// The auto-bind path records the same entry with <c>via = verified_domain</c>, so an auditor
    /// can tell the two ways a seat was taken without reading the session log.
    /// </summary>
    [Fact]
    public async Task VerifiedDomainBind_RecordsItsOwnVia()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: true);
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

        result.Value.Bound.Count.ShouldBe(1);

        var metadata = await TextOwnerAsync(
            "select metadata::text from public.audit_log where org_id = @org_id and action = 'membership.bind'",
            ("org_id", org));

        metadata.ShouldContain("verified_domain");
    }

    /// <summary>
    /// A refused bind commits nothing, so it appends nothing: the org that saw the attempt has no
    /// audit row at all and its invite is still <c>pending</c>. The audit trail is never asked
    /// outside a successful bind, which is what keeps the refusal's shape identical.
    /// </summary>
    [Fact]
    public async Task RefusedBind_AppendsNothing()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var invite = await SeedInviteAsync(org, "invitee@example.com");

        var result = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(
                await SeedUserAsync(),
                "github",
                "someone.else@example.com",
                EmailVerified: true,
                invite.Token,
                HostedDomain: null),
            Token);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Bound.ShouldBeEmpty();

        (await ScalarOwnerAsync<long>(
            "select count(*) from public.audit_log where org_id = @org_id",
            ("org_id", org))).ShouldBe(0, "a rolled-back bind leaves no audit row");

        (await MembershipStateAsync(org, invite.MembershipId)).Status.ShouldBe(MembershipStatus.Pending);
    }

    /// <summary>
    /// Two orgs binding in one call are two chains: one entry each, both at <c>seq = 1</c>, and
    /// neither row lands in the other org.
    /// </summary>
    [Fact]
    public async Task TwoOrgsInOneCall_AuditIntoTheirOwnChains()
    {
        var first = await SeedOrgAsync(autobindVerifiedDomain: true);
        var second = await SeedOrgAsync(autobindVerifiedDomain: true);
        var address = Unique.Email("invitee");
        var firstInvite = await SeedInviteAsync(first, address);
        var secondInvite = await SeedInviteAsync(second, address);

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

        foreach (var (orgId, invite) in new[] { (first, firstInvite), (second, secondInvite) })
        {
            (await ScalarOwnerAsync<long>(
                "select count(*) from public.audit_log where org_id = @org_id and action = 'membership.bind' and seq = 1",
                ("org_id", orgId))).ShouldBe(1);

            (await ScalarOwnerAsync<string>(
                "select object_id from public.audit_log where org_id = @org_id and action = 'membership.bind'",
                ("org_id", orgId))).ShouldBe(invite.MembershipId.ToString("D"));
        }
    }

    /// <summary>The same owner seam for a text column or an aggregate, where NULL reads as the empty string.</summary>
    private async Task<string> TextOwnerAsync(string sql, params (string Name, object Value)[] parameters) =>
        await ScalarOwnerAsync<string?>(sql, parameters) ?? string.Empty;
}
