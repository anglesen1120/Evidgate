using System.Net;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Invites;
using Evidgate.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Invites;

/// <summary>
/// The lifecycle of one invite (02.1.2 §5/§6, AC6/AC7) through the real Api composition
/// (<c>Program</c> → <c>ApiHost</c>) over the harness's migrated database: <c>resend</c> rotates the
/// token of the SAME pending row, <c>cancel-invite</c> takes <c>pending → revoked</c>, and every
/// state that is not <c>pending</c> answers 409 <c>invite_not_pending</c>. Rows are arranged through
/// the harness's owner seam (00 §5.5 C3) and read back the same way, so a refused command cannot hide
/// a row it wrote anyway.
/// </summary>
[Trait("Category", "Docker")]
public sealed class InviteLifecycleTests : CatalogueTestBase
{
    private static readonly string[] NotifyKinds = ["invite", "resend"];
    private static readonly string[] PayloadMembers = ["kind", "membership_id", "org_id"];

    public InviteLifecycleTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// AC6: a resend re-mints the invite of the same membership — one row, rotated prefix, hash and
    /// expiry, a fresh <c>invite_token</c>, and the previous token no longer resolves anywhere.
    /// </summary>
    [Fact]
    public async Task Resend_RotatesTheTokenOfTheSamePendingRowAndAnswers202()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        var created = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships",
            """{"email":"invitee@example.com"}""",
            "create-1");

        created.Status.ShouldBe(HttpStatusCode.Created);
        var membershipId = created.Body.GetProperty("id").GetGuid();
        var firstToken = created.Body.GetProperty("invite_token").GetString();
        string.IsNullOrWhiteSpace(firstToken).ShouldBeFalse();
        InviteTokenFormat.TryParse(firstToken, out var firstPrefix, out var firstSecret).ShouldBeTrue();

        var firstHash = await ScalarAsync<byte[]>(
            "select invite_token_hash from public.membership where id = @id",
            ("id", membershipId));
        var firstExpiry = await ScalarAsync<double>(
            "select extract(epoch from invite_expires_at)::double precision from public.membership where id = @id",
            ("id", membershipId));

        var resent = await PostAsync(client, $"/v1/orgs/{org}/memberships/{membershipId}/resend", null, "resend-1");

        resent.Status.ShouldBe(HttpStatusCode.Accepted);
        resent.Body.GetProperty("id").GetGuid().ShouldBe(membershipId, "AC6: a resend keeps the membership row");
        resent.Body.GetProperty("status").GetString().ShouldBe(MembershipStatus.Pending);

        var secondToken = resent.Body.GetProperty("invite_token").GetString();
        string.IsNullOrWhiteSpace(secondToken).ShouldBeFalse("the resend answers a fresh token exactly once");
        secondToken.ShouldNotBe(firstToken);
        InviteTokenFormat.TryParse(secondToken, out var secondPrefix, out var secondSecret).ShouldBeTrue();
        secondPrefix.ShouldNotBe(firstPrefix, "the public lookup key rotates with the secret");

        (await ScalarAsync<long>("select count(*) from public.membership where org_id = @org", ("org", org)))
            .ShouldBe(1, "AC6: a resend creates no second row");
        (await ScalarAsync<long>(
            "select count(*) from public.membership where org_id = @org and status in ('active', 'pending')",
            ("org", org))).ShouldBe(1, "a resend consumes no seat: the row it re-mints is itself");

        (await TextAsync("select invite_secret_prefix from public.membership where id = @id", ("id", membershipId)))
            .ShouldBe(secondPrefix);

        var secondHash = await ScalarAsync<byte[]>(
            "select invite_token_hash from public.membership where id = @id",
            ("id", membershipId));
        secondHash.Length.ShouldBe(32);
        secondHash.ShouldNotBe(firstHash, "the stored digest is replaced, so a leaked pair cannot be replayed");

        var secondExpiry = await ScalarAsync<double>(
            "select extract(epoch from invite_expires_at)::double precision from public.membership where id = @id",
            ("id", membershipId));
        secondExpiry.ShouldBeGreaterThan(firstExpiry, "the resend re-arms the lifetime");
        secondExpiry.ShouldBeLessThanOrEqualTo(
            DateTimeOffset.UtcNow.Add(InviteLifetime.Default).AddMinutes(5).ToUnixTimeSeconds());

        // The old token stopped resolving; the new one resolves to this very row.
        (await ResolverHitsAsync(firstPrefix)).ShouldBe(0, "the overwritten prefix is gone from every live row");
        var resolved = await ResolvedMembershipAsync(secondPrefix);
        resolved.HasValue.ShouldBeTrue("the fresh prefix resolves to its membership");
        resolved!.Value.ShouldBe(membershipId);

        // …and the stored hash only verifies the secret the resend minted.
        var secrets = new InviteSecrets(factory.Services.GetRequiredService<IOptions<InviteOptions>>());
        var version = await ScalarAsync<int>("select invite_pepper_version from public.membership where id = @id", ("id", membershipId));

        secrets.Verify(version, secondHash, secondSecret).ShouldBeTrue();
        secrets.Verify(version, secondHash, firstSecret).ShouldBeFalse("the superseded secret can no longer bind");
    }

    /// <summary>
    /// §6: <c>cancel-invite</c> answers 204 and takes the row to <c>revoked</c>; the resolver only
    /// returns <c>pending</c> rows, so the invite can never bind afterwards — and the prefix and hash
    /// stay on the row for audit.
    /// </summary>
    [Fact]
    public async Task Cancel_RevokesTheInviteAndAnswers204()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        var created = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships",
            """{"email":"invitee@example.com"}""",
            "create-1");

        created.Status.ShouldBe(HttpStatusCode.Created);
        var membershipId = created.Body.GetProperty("id").GetGuid();
        InviteTokenFormat.TryParse(created.Body.GetProperty("invite_token").GetString(), out var prefix, out _)
            .ShouldBeTrue();

        var cancelled = await PostAsync(client, $"/v1/orgs/{org}/memberships/{membershipId}/cancel-invite", null, "cancel-1");

        cancelled.Status.ShouldBe(HttpStatusCode.NoContent);
        cancelled.Body.ValueKind.ShouldBe(JsonValueKind.Undefined, "a 204 carries no body");

        (await TextAsync("select status from public.membership where id = @id", ("id", membershipId)))
            .ShouldBe(MembershipStatus.Revoked);
        (await ResolverHitsAsync(prefix)).ShouldBe(0, "the resolver returns pending rows only, so a revoked invite cannot bind");

        (await ScalarAsync<long>("select count(*) from public.membership where org_id = @org", ("org", org))).ShouldBe(1);
        (await ScalarAsync<int>(
            "select octet_length(invite_token_hash) from public.membership where id = @id",
            ("id", membershipId))).ShouldBe(32, "the issued token stays on the row for audit");
    }

    /// <summary>
    /// §5/§6: once cancelled, the invite is not pending any more — a second cancel and a resend both
    /// answer 409 <c>invite_not_pending</c>, the status does not move, and no notification is enqueued.
    /// </summary>
    [Fact]
    public async Task SecondCancelAndResendAfterCancel_Are409InviteNotPending()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        var created = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships",
            """{"email":"invitee@example.com"}""",
            "create-1");

        created.Status.ShouldBe(HttpStatusCode.Created);
        var membershipId = created.Body.GetProperty("id").GetGuid();

        var cancelled = await PostAsync(client, $"/v1/orgs/{org}/memberships/{membershipId}/cancel-invite", null, "cancel-1");
        cancelled.Status.ShouldBe(HttpStatusCode.NoContent);

        var again = await PostAsync(client, $"/v1/orgs/{org}/memberships/{membershipId}/cancel-invite", null, "cancel-2");
        again.Status.ShouldBe(HttpStatusCode.Conflict);
        Code(again.Body).ShouldBe("invite_not_pending");

        var resend = await PostAsync(client, $"/v1/orgs/{org}/memberships/{membershipId}/resend", null, "resend-after-cancel");
        resend.Status.ShouldBe(HttpStatusCode.Conflict, "a cancelled invite has no token to rotate");
        Code(resend.Body).ShouldBe("invite_not_pending");

        (await TextAsync("select status from public.membership where id = @id", ("id", membershipId)))
            .ShouldBe(MembershipStatus.Revoked);
        (await ScalarAsync<long>(
            "select count(*) from public.outbox where org_id = @org and task_type = 'identity.invite.notify'",
            ("org", org))).ShouldBe(1, "only the create notified: neither the cancel nor the refused resend enqueues a row");
    }

    /// <summary>
    /// §5: <c>resend</c> and <c>cancel-invite</c> only exist for a <c>pending</c> row — an already bound
    /// (active) or swept (expired) membership answers 409 <c>invite_not_pending</c>, with no write.
    /// </summary>
    [Fact]
    public async Task ResendAndCancel_OnActiveOrExpiredMembership_Are409InviteNotPending()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        var activeId = await SeedMembershipAsync(org, "seated@example.com", MembershipStatus.Active);
        var expiredId = await SeedMembershipAsync(org, "lapsed@example.com", MembershipStatus.Expired);

        foreach (var (membershipId, status) in new[] { (activeId, MembershipStatus.Active), (expiredId, MembershipStatus.Expired) })
        {
            var resend = await PostAsync(
                client,
                $"/v1/orgs/{org}/memberships/{membershipId}/resend",
                null,
                $"resend-{membershipId:N}");

            resend.Status.ShouldBe(HttpStatusCode.Conflict);
            Code(resend.Body).ShouldBe("invite_not_pending");

            var cancel = await PostAsync(
                client,
                $"/v1/orgs/{org}/memberships/{membershipId}/cancel-invite",
                null,
                $"cancel-{membershipId:N}");

            cancel.Status.ShouldBe(HttpStatusCode.Conflict);
            Code(cancel.Body).ShouldBe("invite_not_pending");

            (await TextAsync("select status from public.membership where id = @id", ("id", membershipId)))
                .ShouldBe(status, "a refused lifecycle command leaves the row where it was");
        }

        (await ScalarAsync<long>("select count(*) from public.outbox where org_id = @org", ("org", org)))
            .ShouldBe(0, "neither refusal enqueued anything");
    }

    /// <summary>
    /// AC7: a membership id this tenant cannot see — another org's pending invite, or an id that
    /// exists nowhere — is 404 on both routes, and the foreign row is untouched.
    /// </summary>
    [Fact]
    public async Task UnknownOrForeignMembershipId_Is404()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var foreignOrg = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var foreignId = await SeedMembershipAsync(foreignOrg, "foreign@example.com", MembershipStatus.Pending);

        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        foreach (var membershipId in new[] { Guid.CreateVersion7(), foreignId })
        {
            var resend = await PostAsync(
                client,
                $"/v1/orgs/{org}/memberships/{membershipId}/resend",
                null,
                $"resend-{membershipId:N}");

            resend.Status.ShouldBe(HttpStatusCode.NotFound);
            Code(resend.Body).ShouldBe("not_found");

            var cancel = await PostAsync(
                client,
                $"/v1/orgs/{org}/memberships/{membershipId}/cancel-invite",
                null,
                $"cancel-{membershipId:N}");

            cancel.Status.ShouldBe(HttpStatusCode.NotFound);
            Code(cancel.Body).ShouldBe("not_found");
        }

        (await TextAsync("select status from public.membership where id = @id", ("id", foreignId)))
            .ShouldBe(MembershipStatus.Pending, "the other org's invite is not cancelled by a 404");
        (await ScalarAsync<long>("select count(*) from public.outbox where org_id = @org", ("org", org))).ShouldBe(0);
    }

    /// <summary>
    /// A <c>viewer</c> holds no <c>membership:write</c>, so both lifecycle routes are refused by the
    /// production evaluator with 403 <c>forbidden</c> — before any row is read, so nothing changes.
    /// </summary>
    [Fact]
    public async Task Viewer_CannotResendOrCancel_Is403()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var viewerUserId = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);
        var inviteId = await SeedMembershipAsync(org, "invitee@example.com", MembershipStatus.Pending);

        await using var factory = IdentityApiFactory.Start(
            Fixture,
            new IdentityHostSetup(org, Principal: RequestPrincipal.Member(viewerUserId)));
        using var client = factory.CreateClient();

        var resend = await PostAsync(client, $"/v1/orgs/{org}/memberships/{inviteId}/resend", null, "viewer-resend-1");
        resend.Status.ShouldBe(HttpStatusCode.Forbidden);
        Code(resend.Body).ShouldBe("forbidden");

        var cancel = await PostAsync(client, $"/v1/orgs/{org}/memberships/{inviteId}/cancel-invite", null, "viewer-cancel-1");
        cancel.Status.ShouldBe(HttpStatusCode.Forbidden);
        Code(cancel.Body).ShouldBe("forbidden");

        (await TextAsync("select status from public.membership where id = @id", ("id", inviteId)))
            .ShouldBe(MembershipStatus.Pending);
        (await ScalarAsync<long>("select count(*) from public.outbox where org_id = @org", ("org", org))).ShouldBe(0);
    }

    /// <summary>
    /// §7 / PDPL: one <c>identity.invite.notify</c> row per mint — create and resend — carrying the
    /// membership, the org and the kind, and never the address, the prefix or the token. The dedupe
    /// key names the row version, so the resend gets its own row.
    /// </summary>
    [Fact]
    public async Task Outbox_OneNotifyRowPerMintWithIdsOnly()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        await using var factory = IdentityApiFactory.Start(Fixture, org);
        using var client = factory.CreateClient();

        var created = await PostAsync(
            client,
            $"/v1/orgs/{org}/memberships",
            """{"email":"invitee@example.com"}""",
            "create-1");

        created.Status.ShouldBe(HttpStatusCode.Created);
        var membershipId = created.Body.GetProperty("id").GetGuid();
        var token = created.Body.GetProperty("invite_token").GetString()!;
        InviteTokenFormat.TryParse(token, out var prefix, out var secret).ShouldBeTrue();

        var resent = await PostAsync(client, $"/v1/orgs/{org}/memberships/{membershipId}/resend", null, "resend-1");
        resent.Status.ShouldBe(HttpStatusCode.Accepted);

        var rows = await OutboxRowsAsync(org);

        rows.Length.ShouldBe(2, "one row per mint: the create and the resend");
        rows.All(row => row.GetProperty("task_type").GetString() == "identity.invite.notify").ShouldBeTrue();
        rows.All(row => row.GetProperty("aggregate_type").GetString() == "membership").ShouldBeTrue();
        rows.All(row => row.GetProperty("aggregate_id").GetGuid() == membershipId).ShouldBeTrue();

        rows.Select(row => row.GetProperty("payload").GetProperty("kind").GetString())
            .ToArray()
            .ShouldBe(NotifyKinds);

        foreach (var row in rows)
        {
            var payload = row.GetProperty("payload");

            // Exactly the three documented members — a jsonb object normalises the order, so compare as a set.
            payload.EnumerateObject()
                .Select(property => property.Name)
                .Where(name => name != "_evidgate_correlation") // trace carrier (10.3.1-T1), no business data
                .Order(StringComparer.Ordinal)
                .ToArray()
                .ShouldBe(PayloadMembers);

            payload.GetProperty("membership_id").GetGuid().ShouldBe(membershipId);
            payload.GetProperty("org_id").GetGuid().ShouldBe(org);

            var text = payload.GetRawText();
            text.Contains('@').ShouldBeFalse("PDPL: the payload carries no address");
            text.Contains(token, StringComparison.Ordinal).ShouldBeFalse();
            text.Contains(prefix, StringComparison.Ordinal).ShouldBeFalse();
            text.Contains(secret, StringComparison.Ordinal).ShouldBeFalse();
        }

        rows.Select(row => row.GetProperty("idempotency_key").GetString()).ToArray().ShouldBe(
            new[]
            {
                InviteNotifications.IdempotencyKey(membershipId, 1),
                InviteNotifications.IdempotencyKey(membershipId, 2),
            },
            "the key names the row version, so the resend cannot reuse the create's row");
    }

    /// <summary>The <c>identity.invite.notify</c> rows of one org, oldest first, as jsonb objects.</summary>
    private async Task<JsonElement[]> OutboxRowsAsync(Guid org)
    {
        var json = await TextAsync(
            """
            select coalesce(jsonb_agg(to_jsonb(o) order by o.created_at), '[]'::jsonb)::text
              from public.outbox o
             where o.org_id = @org
               and o.task_type = 'identity.invite.notify'
            """,
            ("org", org));

        using var document = JsonDocument.Parse(json);

        return document.RootElement.EnumerateArray().Select(row => row.Clone()).ToArray();
    }

    /// <summary>How many rows the pre-tenant resolver returns for a prefix (T75).</summary>
    private Task<long> ResolverHitsAsync(string prefix) =>
        ScalarAsync<long>("select count(*) from evidgate_resolve.invite_by_secret(@prefix)", ("prefix", prefix));

    /// <summary>The membership the resolver points at, or null when nothing resolves.</summary>
    private Task<Guid?> ResolvedMembershipAsync(string prefix) =>
        ScalarAsync<Guid?>("select membership_id from evidgate_resolve.invite_by_secret(@prefix)", ("prefix", prefix));

    /// <summary>Seeds one membership in the given state as the owner and returns its id.</summary>
    private async Task<Guid> SeedMembershipAsync(Guid orgId, string email, string status)
    {
        var membershipId = Guid.CreateVersion7();

        await Fixture.SeedAsync(
            "insert into public.membership (id, org_id, invited_email, status) values (@id, @org_id, @email, @status)",
            [("id", membershipId), ("org_id", orgId), ("email", email), ("status", status)],
            Token);

        return membershipId;
    }

    /// <summary>The 00 §9.2 <c>error.code</c> of a failure body.</summary>
    private static string Code(JsonElement body) =>
        body.GetProperty("error").GetProperty("code").GetString()!;

    /// <summary>One owner-connection statement: the harness's arranging/asserting seam (00 §5.5 C3).</summary>
    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(Token))!;
    }

    /// <summary>The same seam for a text column or an aggregate, where NULL reads as the empty string.</summary>
    private async Task<string> TextAsync(string sql, params (string Name, object Value)[] parameters) =>
        await ScalarAsync<string?>(sql, parameters) ?? string.Empty;

    /// <summary>One POST through the real Api composition, with its <c>Idempotency-Key</c> (and a body only where the route takes one).</summary>
    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(
        HttpClient client,
        string path,
        string? json,
        string idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        request.Headers.Add("Idempotency-Key", idempotencyKey);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var body = string.IsNullOrEmpty(text) ? default : JsonSerializer.Deserialize<JsonElement>(text);

        return (response.StatusCode, body);
    }
}
