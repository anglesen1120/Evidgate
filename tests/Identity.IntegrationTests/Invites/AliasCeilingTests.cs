using System.Net;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Domain;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Invites;

/// <summary>
/// D-S81-Q2 (the S78 hand-over precondition D-S78-M1): one mailbox addressed through a
/// <c>+tag</c> or a Gmail dot/<c>googlemail.com</c> spelling must never reach the inviter's own
/// seat. The rule has two halves and this suite pins both — the canonicalisation itself
/// (<see cref="EmailAlias"/>), the refused bind at the invite's address, and the invite-create
/// refusal that keeps the alias from being written in the first place.
/// </summary>
[Trait("Category", "Docker")]
public sealed class AliasCeilingTests : BindTestBase
{
    public AliasCeilingTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// The fold itself, as a table: <c>+tag</c> stripped for every domain, dots dropped only for
    /// the two Gmail spellings (and the legacy one mapped onto <c>gmail.com</c>), everything else
    /// left alone, and idempotent on every row.
    /// </summary>
    [Fact]
    public void Canonical_FoldsOnlyTheFormsThatAreOneMailbox()
    {
        var cases = new (string Address, string Expected)[]
        {
            ("user@example.com", "user@example.com"),
            ("User+tag@Example.com", "user@example.com"),
            ("a+b+news@example.com", "a@example.com"),
            ("a.b@example.com", "a.b@example.com"),
            ("j.doe+news@gmail.com", "jdoe@gmail.com"),
            ("J.Doe@GoogleMail.com", "jdoe@gmail.com"),
            ("jdoe@googlemail.com", "jdoe@gmail.com"),
            ("a+tag@sub.example.com", "a@sub.example.com"),
            ("  Spaced+tag@Example.com  ", "spaced@example.com"),
            // A local part that is nothing but a tag keeps its text: stripping it would leave an
            // address nobody owns.
            ("+tag@example.com", "+tag@example.com"),
            ("not-an-address", "not-an-address"),
            ("", ""),
        };

        foreach (var (address, expected) in cases)
        {
            EmailAlias.Canonical(address).ShouldBe(expected, $"canonical form of '{address}'");
            EmailAlias.Canonical(expected).ShouldBe(expected, $"the fold is idempotent for '{address}'");
        }
    }

    /// <summary>
    /// The bind side. An invite the inviter addressed to one of their own alias forms stays
    /// <c>pending</c> — the answer is the empty result every refusal gives, and the row is
    /// untouched — while the same invite with no inviter recorded binds, so the refusal is the
    /// inviter comparison and not a blanket rule.
    /// </summary>
    [Fact]
    public async Task InviteAddressedToAnAliasOfTheInvitersMailbox_StaysPending()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var inviterUserId = await SeedUserAsync();
        await SeedMemberWithEmailAsync(org, "boss@gmail.com", inviterUserId);

        var plusAlias = await SeedInviteAsync(org, "boss+team@gmail.com");
        await SetInviterAsync(plusAlias.MembershipId, inviterUserId);

        var plusResult = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(await SeedUserAsync(), "github", plusAlias.Email, EmailVerified: true, plusAlias.Token, HostedDomain: null),
            Token);

        plusResult.IsSuccess.ShouldBeTrue();
        plusResult.Value.Bound.ShouldBeEmpty("the plus alias is the inviter's own mailbox");
        (await MembershipStateAsync(org, plusAlias.MembershipId)).Status.ShouldBe(MembershipStatus.Pending);

        var dotAlias = await SeedInviteAsync(org, "b.oss@gmail.com");
        await SetInviterAsync(dotAlias.MembershipId, inviterUserId);

        var dotResult = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(await SeedUserAsync(), "github", dotAlias.Email, EmailVerified: true, dotAlias.Token, HostedDomain: null),
            Token);

        dotResult.IsSuccess.ShouldBeTrue();
        dotResult.Value.Bound.ShouldBeEmpty("the Gmail dot form is the same mailbox");
        (await MembershipStateAsync(org, dotAlias.MembershipId)).Status.ShouldBe(MembershipStatus.Pending);

        // Control: an invite that names no inviter has no mailbox to compare against, so the
        // refusal above cannot be "every invite of this org is refused".
        var unowned = await SeedInviteAsync(org, "colleague@gmail.com");

        var unownedResult = await CreateBinder().BindAsync(
            new BindPendingMembershipsRequest(await SeedUserAsync(), "github", unowned.Email, EmailVerified: true, unowned.Token, HostedDomain: null),
            Token);

        unownedResult.IsSuccess.ShouldBeTrue();
        unownedResult.Value.Bound.Count.ShouldBe(1);
        (await MembershipStateAsync(org, unowned.MembershipId)).Status.ShouldBe(MembershipStatus.Active);
    }

    /// <summary>
    /// The write side, through the real Api: a <c>tenant_admin</c> (who holds
    /// <c>membership:write</c> but not <c>policy:write</c>) inviting an alias of their own
    /// address with the <c>release_approver</c> role is the self-grant and is refused 403 —
    /// exactly like an invite to the plain address — while the same role invited to a colleague
    /// is accepted.
    /// </summary>
    [Fact]
    public async Task InviteToAnAliasOfTheCallersOwnAddressWithARoleTheCallerLacks_Is403()
    {
        var org = await SeedOrgAsync(autobindVerifiedDomain: false);
        var callerUserId = await SeedUserAsync();
        await SeedMemberWithEmailAsync(org, "clerk@gmail.com", callerUserId, roleCode: "tenant_admin");
        var releaseApprover = await IdentityTestData.RoleIdAsync(Fixture, org, "release_approver", Token);

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(org, Principal: RequestPrincipal.Member(callerUserId)));
        using var client = factory.CreateClient();

        var alias = await PostInviteAsync(client, org, "clerk+raise@gmail.com", releaseApprover, "alias-self-1");
        alias.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "an alias of the caller's own mailbox is the caller");

        var plain = await PostInviteAsync(client, org, "clerk@gmail.com", releaseApprover, "alias-self-2");
        plain.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "the plain address is the same self-grant (unchanged behaviour)");

        var colleague = await PostInviteAsync(client, org, "colleague@gmail.com", releaseApprover, "alias-self-3");
        colleague.StatusCode.ShouldBe(HttpStatusCode.Created, "the rule is about the caller's own mailbox, not a blanket refusal");

        (await CountPendingInvitesAsync(org)).ShouldBe(1, "only the colleague's invite was written");
    }

    /// <summary>
    /// An active membership of <paramref name="userId"/> whose stored address is
    /// <paramref name="email"/>, optionally holding a built-in role (the owner seam, 00 §5.5 C3).
    /// </summary>
    private async Task SeedMemberWithEmailAsync(Guid orgId, string email, Guid userId, string? roleCode = null)
    {
        var membershipId = Guid.CreateVersion7();

        await Fixture.SeedAsync(
            """
            insert into public.membership
                (id, org_id, user_id, invited_email, status, created_at, updated_at, version)
            values (@id, @org_id, @user_id, @email, 'active', now(), now(), 1)
            """,
            [("id", membershipId), ("org_id", orgId), ("user_id", userId), ("email", email)],
            Token);

        if (roleCode is not null)
        {
            await Fixture.SeedAsync(
                """
                insert into public.membership_role
                    (id, org_id, membership_id, role_id, granted_at, created_at, updated_at, version)
                values (@id, @org_id, @membership_id, @role_id, now(), now(), now(), 1)
                """,
                [
                    ("id", Guid.CreateVersion7()),
                    ("org_id", orgId),
                    ("membership_id", membershipId),
                    ("role_id", await IdentityTestData.RoleIdAsync(Fixture, orgId, roleCode, Token)),
                ],
                Token);
        }
    }

    /// <summary>Records who invited the row: the one column the alias ceiling compares against.</summary>
    private Task<int> SetInviterAsync(Guid membershipId, Guid inviterUserId) => Fixture.SeedAsync(
        "update public.membership set invited_by = @invited_by where id = @id",
        [("invited_by", inviterUserId), ("id", membershipId)],
        Token);

    /// <summary>One JSON POST through the real Api composition, with its <c>Idempotency-Key</c>.</summary>
    private static async Task<HttpResponseMessage> PostInviteAsync(
        HttpClient client,
        Guid orgId,
        string email,
        Guid roleId,
        string idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/orgs/{orgId}/memberships")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { email, roles = new[] { roleId } }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>How many <c>pending</c> invites exist in the org, read as the owner.</summary>
    private Task<long> CountPendingInvitesAsync(Guid orgId) => ScalarOwnerAsync<long>(
        "select count(*) from public.membership where org_id = @org_id and status = 'pending'",
        ("org_id", orgId));
}
