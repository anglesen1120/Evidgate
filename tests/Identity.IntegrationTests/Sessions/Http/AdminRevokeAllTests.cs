using System.Net;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Http;

/// <summary>
/// <c>POST /v1/auth/users/{u}/sessions/revoke-all</c> over the real host: one org admin ends every live session
/// of one member of that org in one call, audit row included.
/// </summary>
/// <remarks>
/// <para>
/// One row per call that revoked something, written in the revoke's transaction by the definer: the target as
/// its object, <c>target_user_id</c> and <c>revoked_count</c> as metadata strings, so the record says who ended
/// whose sessions and how many.
/// </para>
/// <para>
/// Every refusal is the single revoke's one 404, asserted byte for byte: an envelope that differs by a byte is an
/// oracle about other accounts. A faulted audit append aborts the whole call, so a revoked session always has a
/// record.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class AdminRevokeAllTests : TenantTest
{
    /// <summary>The addressed-user half of the route; the id follows it.</summary>
    private const string AdminRevokePrefix = "/v1/auth/users/";

    /// <summary>The route's tail.</summary>
    private const string AdminRevokeSuffix = "/sessions/revoke-all";

    /// <summary>The single-session route, whose 404 this route must reproduce exactly.</summary>
    private const string SessionsPath = "/v1/auth/sessions";

    /// <summary>The audited action the definer writes; it lives in SQL, so the literal is pinned here.</summary>
    private const string AdminRevokeAction = "auth.session_revoke_admin";

    /// <summary>The three cookies a 204 of this route must never set or expire: the caller stays signed in.</summary>
    private static readonly string[] CallerCookies =
    [
        SessionTestHost.SessionCookieName,
        SessionTestHost.XsrfCookieName,
        SessionTestHost.AntiforgeryCookieName,
    ];

    /// <summary>Own database: the rollback case installs a trigger on <c>public.audit_log</c>.</summary>
    public AdminRevokeAllTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

    /// <summary>
    /// The administered case: an admin of the member's org ends BOTH live sessions of that member in one call —
    /// 204, no cookie of its own touched — while another member's session and the admin's own session stay live,
    /// the member's next request is 401 <c>session_revoked</c>, and exactly one audit row names the target and the
    /// count.
    /// </summary>
    [Fact]
    public async Task An_Admin_Ends_Every_Live_Session_Of_A_Member_And_One_Audit_Row_Names_The_Target()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var firstId = await host.SessionIdOfAsync(member.SessionCookie);

        // A second device of the same account: the call ends every live row, not only the one it could name.
        host.Clock.Advance(TimeSpan.FromSeconds(5));
        var second = await host.CreateSessionAsync(member.UserId);
        var secondId = await host.SessionIdOfAsync(second.CookieValue);

        // The control rows the call must not touch: a colleague of the same org and the admin itself.
        var colleague = await host.SignInMemberInOrgAsync(client, member.OrgId, roleCode: null);
        var colleagueId = await host.SessionIdOfAsync(colleague.SessionCookie);
        var admin = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");
        var adminId = await host.SessionIdOfAsync(admin.SessionCookie);

        using (var revoked = await RevokeAllAsync(client, admin, member.UserId, member.OrgId))
        {
            revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            revoked.Headers.CacheControl.ShouldNotBeNull("a session answer is never reusable by a shared cache");
            revoked.Headers.CacheControl!.NoStore.ShouldBeTrue();

            foreach (var cookieName in CallerCookies)
            {
                SessionTestHost.Cookies(revoked, cookieName).ShouldBeEmpty(
                    cookieName + ": the admin's own credential is not the target");
            }
        }

        (await host.SessionRowAsync(firstId)).RevokedAt.ShouldNotBeNull("the first live row of the target is ended");
        (await host.SessionRowAsync(secondId)).RevokedAt.ShouldNotBeNull("and so is the second device");
        (await host.SessionRowAsync(colleagueId)).RevokedAt.ShouldBeNull("another member's session is not the target");
        (await host.SessionRowAsync(adminId)).RevokedAt.ShouldBeNull("the caller's own session is never the target");

        (await LiveCountAsync(host, member.UserId)).ShouldBe(0L, "no live session of the target survives the call");

        // One row, and it describes the act: the admin as actor, the TARGET user as object, the count it revoked.
        (await AdminRowCountAsync(host, member.OrgId)).ShouldBe(1L, "exactly one row per call, not one per session");
        using (var row = JsonDocument.Parse(await NewestAdminRowJsonAsync(host, member.OrgId)))
        {
            var stored = row.RootElement;
            stored.GetProperty("actor_type").GetString().ShouldBe("user");
            stored.GetProperty("actor_id").GetString().ShouldBe(admin.UserId.ToString("D"));
            stored.GetProperty("action").GetString().ShouldBe(AdminRevokeAction);
            stored.GetProperty("object_type").GetString().ShouldBe("user");
            stored.GetProperty("object_id").GetString().ShouldBe(member.UserId.ToString("D"));

            var metadata = stored.GetProperty("metadata");
            string.Join(",", metadata.EnumerateObject().Select(property => property.Name)).ShouldBe(
                "revoked_count,target_user_id",
                "the canonical string sorts the two keys by UTF-16 code unit, which the hash depends on");
            metadata.GetProperty("revoked_count").GetString().ShouldBe("2", "both live rows were revoked");
            metadata.GetProperty("target_user_id").GetString().ShouldBe(member.UserId.ToString("D"));
        }

        // And the revocation is a row state: the member's very next request is refused on the dead cookie.
        using var next = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            SessionsPath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie));

        next.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(next)).ShouldBe(ApiErrorCode.SessionRevoked);
    }

    /// <summary>
    /// A membership is not the permission: a member of the same org holding no role answers 404 <c>not_found</c>
    /// and nothing — not one row of the target, not one audit row — changes.
    /// </summary>
    [Fact]
    public async Task A_Caller_Without_Membership_Write_Answers_404_And_Changes_Nothing()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var memberSessionId = await host.SessionIdOfAsync(member.SessionCookie);
        var plain = await host.SignInMemberInOrgAsync(client, member.OrgId, roleCode: null);

        using (var refused = await RevokeAllAsync(client, plain, member.UserId, member.OrgId))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound, "an unauthorised caller learns nothing about the target");
            (await SessionTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.NotFound);
        }

        (await host.SessionRowAsync(memberSessionId)).RevokedAt.ShouldBeNull("a refused call revokes nothing");
        (await LiveCountAsync(host, member.UserId)).ShouldBe(1L);
        (await AdminRowCountAsync(host, member.OrgId)).ShouldBe(0L);
    }

    /// <summary>
    /// The caller must hold <c>membership:write</c> in the org the header NAMES: an admin of another org naming
    /// this one answers 404, and an admin of this org naming another answers 404 too — the header cannot widen
    /// what the definer authorises.
    /// </summary>
    [Fact]
    public async Task An_Admin_Of_Another_Org_Answers_404_And_Changes_Nothing()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var memberSessionId = await host.SessionIdOfAsync(member.SessionCookie);

        var otherOrg = await host.SeedOrganizationAsync();
        var foreignAdmin = await host.SignInMemberInOrgAsync(client, otherOrg, "tenant_admin");

        using (var refused = await RevokeAllAsync(client, foreignAdmin, member.UserId, member.OrgId))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the caller administers no membership in this org");
            (await SessionTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.NotFound);
        }

        var localAdmin = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");

        using (var refused = await RevokeAllAsync(client, localAdmin, member.UserId, otherOrg))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the org named is not one the caller administers");
            (await SessionTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.NotFound);
        }

        (await host.SessionRowAsync(memberSessionId)).RevokedAt.ShouldBeNull();
        (await AdminRowCountAsync(host, member.OrgId)).ShouldBe(0L);
    }

    /// <summary>
    /// An id that never existed answers the route's one 404, exactly as an unknown session id does on the single
    /// revoke, and writes nothing.
    /// </summary>
    [Fact]
    public async Task An_Unknown_User_Answers_404_And_Changes_Nothing()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var admin = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");

        using (var refused = await RevokeAllAsync(client, admin, Guid.CreateVersion7(), member.OrgId))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await SessionTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.NotFound);
        }

        (await AdminRowCountAsync(host, member.OrgId)).ShouldBe(0L);
    }

    /// <summary>
    /// The id's shape and the header's presence are never an oracle: a non-uuid <c>{u}</c>, a missing org header
    /// and a non-uuid org header all answer the same 404 <c>not_found</c> — never the undocumented 400 parameter
    /// binding would raise before the credential was judged.
    /// </summary>
    [Fact]
    public async Task A_Malformed_User_Or_Org_Header_Answers_The_Routes_One_404()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var admin = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");

        var probes = new (string Path, string? OrgHeader, string Because)[]
        {
            (AdminRevokePrefix + "not-a-uuid" + AdminRevokeSuffix, member.OrgId.ToString("D"), "an id that is not a uuid"),
            (AdminRevokePrefix + member.UserId.ToString("D") + AdminRevokeSuffix, null, "no org named at all"),
            (AdminRevokePrefix + member.UserId.ToString("D") + AdminRevokeSuffix, "not-a-uuid", "an org that is not a uuid"),
        };

        var bodies = new List<string>();

        foreach (var (path, orgHeader, because) in probes)
        {
            using var refused = await SessionTestHost.SendAsync(
                client,
                HttpMethod.Post,
                path,
                SessionTestHost.CookieHeaderFor(admin.SessionCookie, admin.Xsrf),
                xsrfToken: admin.Xsrf.RequestToken,
                orgHeader: orgHeader);

            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound, because + " is the route's one 404");
            (await SessionTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.NotFound);
            bodies.Add(await NormalizedBodyAsync(refused));
        }

        bodies.Distinct(StringComparer.Ordinal).Count().ShouldBe(
            1,
            "every refusal of this route carries the same bytes, so their shape distinguishes nothing");

        (await LiveCountAsync(host, member.UserId)).ShouldBe(1L);
        (await AdminRowCountAsync(host, member.OrgId)).ShouldBe(0L);
    }

    /// <summary>
    /// The target must be an ACTIVE member of the named org: an account of another org, and an account whose
    /// membership of this org was removed, both answer 404 and keep their live sessions.
    /// </summary>
    [Fact]
    public async Task A_Target_Who_Is_Not_An_Active_Member_Of_The_Org_Answers_404()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var admin = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");

        // A real account of ANOTHER org, signed in: its row is live, but this org administers no membership of it.
        var stranger = await host.SignInMemberAsync(client);
        var strangerSessionId = await host.SessionIdOfAsync(stranger.SessionCookie);

        using (var refused = await RevokeAllAsync(client, admin, stranger.UserId, member.OrgId))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await SessionTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.NotFound);
        }

        (await host.SessionRowAsync(strangerSessionId)).RevokedAt.ShouldBeNull("a refused call revokes nothing");

        // The removal path: the account keeps a live session while its membership of this org is no longer active.
        var removed = await host.SeedUserAsync();
        await host.SeedMembershipAsync(member.OrgId, removed, status: "removed");
        var removedTicket = await host.CreateSessionAsync(removed);
        var removedSessionId = await host.SessionIdOfAsync(removedTicket.CookieValue);

        using (var refused = await RevokeAllAsync(client, admin, removed, member.OrgId))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a removed membership is not an administered member");
            (await SessionTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.NotFound);
        }

        (await host.SessionRowAsync(removedSessionId)).RevokedAt.ShouldBeNull();
        (await AdminRowCountAsync(host, member.OrgId)).ShouldBe(0L);
    }

    /// <summary>
    /// The caller cannot end its OWN sessions through the administrative route: addressing itself answers 404,
    /// because the self route is <c>POST /v1/auth/sessions/revoke-all</c> and this one only ever acts on another
    /// member. Nothing of the caller's is revoked.
    /// </summary>
    [Fact]
    public async Task An_Admin_Addressing_Itself_Answers_404()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var admin = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");
        var adminSessionId = await host.SessionIdOfAsync(admin.SessionCookie);

        using (var refused = await RevokeAllAsync(client, admin, admin.UserId, member.OrgId))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the self route is the only way to end your own sessions");
            (await SessionTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.NotFound);
        }

        (await host.SessionRowAsync(adminSessionId)).RevokedAt.ShouldBeNull();
        (await AdminRowCountAsync(host, member.OrgId)).ShouldBe(0L);
    }

    /// <summary>
    /// Every refusal of this route is the single revoke's <c>SessionNotFound</c> — the same status, code, message,
    /// details and field order, byte for byte apart from the per-request id 00 §9.2 makes every body echo — so a
    /// client cannot tell an unknown user, a foreign org or a self-addressed call from an unknown session id.
    /// </summary>
    [Fact]
    public async Task Every_Refusal_Is_Byte_Identical_To_The_Single_Revokes_404()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var admin = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");
        var otherOrg = await host.SeedOrganizationAsync();

        using var baseline = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            SessionsPath + "/" + Guid.CreateVersion7(),
            SessionTestHost.CookieHeaderFor(admin.SessionCookie, admin.Xsrf),
            xsrfToken: admin.Xsrf.RequestToken,
            orgHeader: member.OrgId.ToString("D"));

        baseline.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the single revoke's 404 is the reference envelope");
        var expected = await NormalizedBodyAsync(baseline);
        expected.ShouldNotBeNullOrWhiteSpace();

        var refusals = new (Guid Target, Guid Org)[]
        {
            (Guid.CreateVersion7(), member.OrgId),   // unknown user
            (member.UserId, otherOrg),               // org the caller does not administer
            (admin.UserId, member.OrgId),            // the caller itself
        };

        foreach (var (target, org) in refusals)
        {
            using var refused = await RevokeAllAsync(client, admin, target, org);

            refused.StatusCode.ShouldBe(baseline.StatusCode);
            (await NormalizedBodyAsync(refused)).ShouldBe(
                expected,
                "a refusal of this route must be indistinguishable from the single revoke's not_found");
        }
    }

    /// <summary>
    /// A target with no live session answers 204 and writes NO audit row: the call is idempotent and revoked
    /// nothing, so a row would assert a state change that never happened — the record must stay honest, which is
    /// why the definer inserts only when its update touched a row.
    /// </summary>
    [Fact]
    public async Task A_Target_With_Only_Dead_Sessions_Answers_204_And_Writes_No_Row()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var memberSessionId = await host.SessionIdOfAsync(member.SessionCookie);
        var admin = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");

        // The member's only row is already dead, so the call has nothing to end.
        await host.RevokeSessionAsync(memberSessionId);

        using (var response = await RevokeAllAsync(client, admin, member.UserId, member.OrgId))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent, "an authorised call on a target with nothing live succeeds");
        }

        (await LiveCountAsync(host, member.UserId)).ShouldBe(0L);
        (await AdminRowCountAsync(host, member.OrgId)).ShouldBe(
            0L,
            "the call revoked nothing, so no row may claim it did");
    }

    /// <summary>
    /// The definer's own fault: a throwaway trigger on <c>audit_log</c> that refuses this action's insert makes the
    /// whole call abort — 503 <c>unavailable</c>, every session of the target STILL live, no row, no cookie
    /// touched — and the retry, with the fault gone, records the revoke exactly once.
    /// </summary>
    [Fact]
    public async Task A_Faulted_Audit_Rolls_Every_Revoke_Back_And_Answers_503()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var firstId = await host.SessionIdOfAsync(member.SessionCookie);
        host.Clock.Advance(TimeSpan.FromSeconds(5));
        var second = await host.CreateSessionAsync(member.UserId);
        var secondId = await host.SessionIdOfAsync(second.CookieValue);
        var admin = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");

        try
        {
            await InstallAuditFaultAsync(host, member.OrgId);

            using var failed = await RevokeAllAsync(client, admin, member.UserId, member.OrgId);

            failed.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            (await SessionTestHost.ErrorCodeAsync(failed)).ShouldBe(ApiErrorCode.Unavailable);

            foreach (var cookieName in CallerCookies)
            {
                SessionTestHost.Cookies(failed, cookieName).ShouldBeEmpty(
                    cookieName + ": a refused call never ends the caller's own credential");
            }
        }
        finally
        {
            await DropAuditFaultAsync(host);
        }

        (await host.SessionRowAsync(firstId)).RevokedAt.ShouldBeNull(
            "the revokes share the definer's transaction, so a failed append leaves every session live");
        (await host.SessionRowAsync(secondId)).RevokedAt.ShouldBeNull();
        (await LiveCountAsync(host, member.UserId)).ShouldBe(2L);
        (await AdminRowCountAsync(host, member.OrgId)).ShouldBe(0L, "the faulted call committed nothing");

        // The retry, with the fault gone, is an ordinary successful call: nothing was left half-done.
        using (var retried = await RevokeAllAsync(client, admin, member.UserId, member.OrgId))
        {
            retried.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await LiveCountAsync(host, member.UserId)).ShouldBe(0L);
        (await AdminRowCountAsync(host, member.OrgId)).ShouldBe(1L, "the retry records the call once");
    }

    /// <summary>
    /// The mutation is cookie-authenticated, so the host-wide gate refuses it before any handler runs: a request
    /// carrying the session and the antiforgery COOKIE but no <c>X-XSRF-TOKEN</c> header is 403
    /// <c>csrf_failed</c>, and the target's sessions stay live.
    /// </summary>
    [Fact]
    public async Task A_Call_Without_The_Xsrf_Header_Is_403_CsrfFailed_And_Changes_Nothing()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var memberSessionId = await host.SessionIdOfAsync(member.SessionCookie);
        var admin = await host.SignInMemberInOrgAsync(client, member.OrgId, "tenant_admin");

        using (var refused = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Post,
            AdminRevokePrefix + member.UserId.ToString("D") + AdminRevokeSuffix,
            SessionTestHost.CookieHeaderFor(admin.SessionCookie, admin.Xsrf),
            orgHeader: member.OrgId.ToString("D")))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await SessionTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.CsrfFailed);
        }

        (await host.SessionRowAsync(memberSessionId)).RevokedAt.ShouldBeNull("the gate runs before any handler");
        (await AdminRowCountAsync(host, member.OrgId)).ShouldBe(0L);
    }

    /// <summary>
    /// The route needs a caller: without a session cookie it answers 401 <c>unauthenticated</c> through the
    /// authenticator, before any id, header or target is judged.
    /// </summary>
    [Fact]
    public async Task A_Call_Without_A_Session_Is_401_Unauthenticated()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        host.Recorder.Clear();

        var path = AdminRevokePrefix + member.UserId.ToString("D") + AdminRevokeSuffix;

        using (var refused = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Post,
            path,
            orgHeader: member.OrgId.ToString("D")))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the route cannot be driven anonymously");
            (await SessionTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.Unauthenticated);
            host.Recorder.PatternFor(HttpMethod.Post.Method, path).ShouldNotBeNull(
                "the refusal must come from the route, not from an unmatched path");
        }

        (await LiveCountAsync(host, member.UserId)).ShouldBe(1L);
    }

    /// <summary>
    /// The failure body's bytes with the per-request <c>request_id</c> echo replaced.
    /// </summary>
    /// <remarks>
    /// 00 §9.2 requires the envelope to echo the request's own id, so two requests cannot agree on that one value;
    /// everything else — code, message, retryable, details and the field order — is compared byte for byte.
    /// </remarks>
    /// <param name="response">The refused response.</param>
    private static async Task<string> NormalizedBodyAsync(HttpResponseMessage response)
    {
        var body = await SessionTestHost.BodyAsync(response);
        using var document = JsonDocument.Parse(body);
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            foreach (var property in document.RootElement.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);

                if (!string.Equals(property.Name, "error", StringComparison.Ordinal))
                {
                    property.Value.WriteTo(writer);
                    continue;
                }

                writer.WriteStartObject();

                foreach (var field in property.Value.EnumerateObject())
                {
                    if (string.Equals(field.Name, "request_id", StringComparison.Ordinal))
                    {
                        writer.WriteString(field.Name, "<request id>");
                        continue;
                    }

                    writer.WritePropertyName(field.Name);
                    field.Value.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>One administrative revoke-all as <paramref name="caller"/>'s browser sends it.</summary>
    /// <param name="client">A client of the host.</param>
    /// <param name="caller">The signed-in account.</param>
    /// <param name="targetUserId">The user id the route addresses.</param>
    /// <param name="orgId">The <c>X-Evidgate-Org</c> value, or null for none.</param>
    private static Task<HttpResponseMessage> RevokeAllAsync(
        HttpClient client,
        SignedInMember caller,
        Guid targetUserId,
        Guid? orgId) =>
        SessionTestHost.SendAsync(
            client,
            HttpMethod.Post,
            AdminRevokePrefix + targetUserId.ToString("D") + AdminRevokeSuffix,
            SessionTestHost.CookieHeaderFor(caller.SessionCookie, caller.Xsrf),
            xsrfToken: caller.Xsrf.RequestToken,
            orgHeader: orgId?.ToString("D"));

    /// <summary>How many live rows the account holds, read as the owner (the app role cannot read the table).</summary>
    /// <param name="host">The host whose owner connection reads the row.</param>
    /// <param name="userId">The account whose live sessions are counted.</param>
    private static Task<long> LiveCountAsync(SessionTestHost host, Guid userId) =>
        host.CountAsync(
            "select count(*) from public.session where user_id = @user and revoked_at is null",
            ("user", userId));

    /// <summary>How many administrative-revoke rows the org's chain holds.</summary>
    /// <param name="host">The host whose owner connection reads the chain.</param>
    /// <param name="orgId">The org whose chain is counted.</param>
    private static Task<long> AdminRowCountAsync(SessionTestHost host, Guid orgId) =>
        host.CountAsync(
            "select count(*) from public.audit_log where org_id = @org and action = @action",
            ("org", orgId),
            ("action", AdminRevokeAction));

    /// <summary>The newest administrative-revoke row of the org, as stored JSON text.</summary>
    /// <param name="host">The host whose owner connection reads the chain.</param>
    /// <param name="orgId">The org whose chain is read.</param>
    private static async Task<string> NewestAdminRowJsonAsync(SessionTestHost host, Guid orgId)
    {
        var payload = await host.ScalarAsync(
            "select to_jsonb(a)::text from public.audit_log a "
            + "where a.org_id = @org and a.action = @action order by a.seq desc limit 1",
            new (string Name, object Value)[] { ("org", orgId), ("action", AdminRevokeAction) });

        payload.ShouldNotBeNull("the call must have appended its one audit row");

        return (string)payload;
    }

    /// <summary>
    /// Installs the throwaway fault: a trigger on <c>audit_log</c> that refuses this action's insert for this org
    /// only, so no other write of the harness is disturbed. Created as the owner under a name no production
    /// object uses.
    /// </summary>
    /// <param name="host">The host whose owner seam arranges the trigger.</param>
    /// <param name="orgId">The org whose row must be refused.</param>
    private static async Task InstallAuditFaultAsync(SessionTestHost host, Guid orgId)
    {
        await host.ArrangeAsync(
            """
            create or replace function public.s302_admin_revoke_all_audit_fault() returns trigger
              language plpgsql
              set search_path = ''
              as $$
              begin
                raise exception 's302 admin revoke-all audit fault' using errcode = '58000';
              end
              $$;
            """);

        // A utility statement takes no bind parameters, so the org travels as a literal uuid (a Guid's "D" text
        // carries no SQL) and the action is the pinned constant.
        await host.ArrangeAsync(
            "create trigger s302_admin_revoke_all_audit_fault before insert on public.audit_log "
            + "for each row when (new.action = '" + AdminRevokeAction + "' and new.org_id = '"
            + orgId.ToString("D") + "'::uuid) execute function public.s302_admin_revoke_all_audit_fault()");
    }

    /// <summary>Drops the throwaway trigger and its function, whatever the test's outcome was.</summary>
    /// <param name="host">The host whose owner seam removes them.</param>
    private static async Task DropAuditFaultAsync(SessionTestHost host)
    {
        await host.ArrangeAsync("drop trigger if exists s302_admin_revoke_all_audit_fault on public.audit_log");
        await host.ArrangeAsync("drop function if exists public.s302_admin_revoke_all_audit_fault()");
    }
}
