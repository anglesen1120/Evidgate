using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Sessions;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Store;

/// <summary>
/// 02.3.2 AC6 and AC8 at the store level (<see cref="ISessionRoutesStore"/>, migration 0048): the caller
/// sees and ends only their own live sessions, an admin holding <c>membership:write</c> ends a member's,
/// every other id answers the same <c>not_found</c>, and a logout leaves the old cookie dead.
/// </summary>
/// <remarks>
/// The acting user is never passed: the proof is the HMAC of the cookie the real <c>SessionStore</c> minted,
/// resolved inside the definer, so these tests prove the same property the routes rely on. Rows are read
/// through the owner connection (<c>evidgate_app</c> holds no privilege on <c>public.session</c>, 0041)
/// while every write under test runs as the app role.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class SessionRoutesStoreTests : SessionHardeningTestBase
{
    /// <summary>Shares the run's database: the routes act on the accounts and session rows the case seeded.</summary>
    public SessionRoutesStoreTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>A store over the app-role data source; the route statements never open the sweep connection.</summary>
    /// <param name="clock">The clock the statements' <c>p_now</c> comes from.</param>
    private SessionRoutesStore CreateRoutes(TimeProvider clock) =>
        new(AppDataSource, new UnusedSweep(), clock);

    /// <summary>The proof a route passes: the HMAC of the live cookie value under the current pepper.</summary>
    /// <param name="ticket">The session the store minted.</param>
    private static byte[] ProofOf(SessionTicket ticket) => HmacOf(Pepper, ticket.CookieValue);

    /// <summary>
    /// AC6: the list holds the caller's LIVE sessions only — a revoked or expired sibling and another
    /// account's session are absent — and exactly one row is flagged current.
    /// </summary>
    [Fact]
    public async Task List_Shows_Only_The_Callers_Live_Sessions_And_Flags_The_Current_One()
    {
        var clock = ClockAt();
        var mine = await SeedUserAsync(Unique.Email("list-mine"));
        var other = await SeedUserAsync(Unique.Email("list-other"));

        var current = await CreateSessionAsync(mine, clock);
        clock.Advance(TimeSpan.FromMinutes(1));
        var second = await CreateSessionAsync(mine, clock);
        var revoked = await CreateSessionAsync(mine, clock);
        var expired = await CreateSessionAsync(mine, clock);
        await CreateSessionAsync(other, clock);

        await OwnerExecuteAsync(
            "update public.session set revoked_at = @stamp where id = @id",
            ("id", revoked.SessionId),
            ("stamp", clock.UtcNow.Subtract(TimeSpan.FromMinutes(5))));
        await OwnerExecuteAsync(
            "update public.session set expires_at = @past where id = @id",
            ("id", expired.SessionId),
            ("past", clock.UtcNow.Subtract(TimeSpan.FromMinutes(1))));

        var (status, items) = await CreateRoutes(clock).ListAsync(ProofOf(current), Token);

        status.ShouldBe(SessionRouteStatus.Done);
        items.Select(item => item.Id).ShouldBe(
            new[] { second.SessionId, current.SessionId },
            ignoreOrder: false,
            customMessage: "newest first, and a revoked, an expired and another account's session are absent");

        var mineNow = items.Single(item => item.Id == current.SessionId);
        mineNow.Current.ShouldBeTrue("the row the proof resolved is the current session");
        mineNow.Ip.ShouldBe("203.0.113.7");
        mineNow.UserAgent.ShouldBe("xunit");
        items.Single(item => item.Id == second.SessionId).Current.ShouldBeFalse();
    }

    /// <summary>
    /// AC6 boundary: an empty proof and a proof that matches no row are the same
    /// <c>unauthenticated</c> answer with no session, never an empty-but-authenticated list.
    /// </summary>
    [Fact]
    public async Task List_Without_A_Proof_Is_Unauthenticated()
    {
        var clock = ClockAt();
        var mine = await SeedUserAsync(Unique.Email("list-unknown"));
        await CreateSessionAsync(mine, clock);
        var routes = CreateRoutes(clock);

        var (absent, absentItems) = await routes.ListAsync([], Token);
        absent.ShouldBe(SessionRouteStatus.Unauthenticated);
        absentItems.ShouldBeEmpty();

        var (unknown, unknownItems) = await routes.ListAsync(HmacOf(Pepper, UnknownCookieValue()), Token);
        unknown.ShouldBe(SessionRouteStatus.Unauthenticated);
        unknownItems.ShouldBeEmpty();
    }

    /// <summary>
    /// AC8: a session past its idle deadline is not live, so the list answers <c>unauthenticated</c> for it
    /// rather than showing a row the user could not use.
    /// </summary>
    [Fact]
    public async Task List_Of_An_Expired_Cookie_Is_Unauthenticated()
    {
        var clock = ClockAt();
        var mine = await SeedUserAsync(Unique.Email("list-expired"));
        var ticket = await CreateSessionAsync(mine, clock);

        await OwnerExecuteAsync(
            "update public.session set expires_at = @past where id = @id",
            ("id", ticket.SessionId),
            ("past", clock.UtcNow.Subtract(TimeSpan.FromMinutes(1))));

        var (status, items) = await CreateRoutes(clock).ListAsync(ProofOf(ticket), Token);

        status.ShouldBe(SessionRouteStatus.Unauthenticated);
        items.ShouldBeEmpty();
    }

    /// <summary>
    /// AC8: logout stamps the revocation on the current row, and the cookie that was just used can no
    /// longer authenticate — the row state, not a key rotation, is what kills a replayed cookie. A second
    /// logout with the same proof resolves no live session, which is the same 401 the route answers.
    /// </summary>
    [Fact]
    public async Task Logout_Revokes_The_Current_Row_And_The_Old_Cookie_Stops_Resolving()
    {
        var clock = ClockAt();
        var user = await SeedUserAsync(Unique.Email("logout"));
        var ticket = await CreateSessionAsync(user, clock);
        var routes = CreateRoutes(clock);

        (await routes.LogoutAsync(ProofOf(ticket), Token)).ShouldBe(SessionRouteStatus.Done);

        (await OwnerScalarAsync<bool>(
            "select revoked_at = @now and rotation_required_at is null from public.session where id = @id",
            ("id", ticket.SessionId),
            ("now", clock.UtcNow)))
            .ShouldBeTrue("logout stamps the revocation instant and clears a pending rotation");

        var replay = await AuthenticateAsync(ticket.CookieValue, clock);
        replay.IsSuccess.ShouldBeTrue();
        replay.Value.Status.ShouldBe(SessionAuthStatus.Revoked, "AC8: the replayed cookie is 401 session_revoked");

        (await routes.LogoutAsync(ProofOf(ticket), Token)).ShouldBe(SessionRouteStatus.Unauthenticated,
            "the row is dead, so the proof resolves no live session any more");
    }

    /// <summary>AC6: revoking one's own session ends exactly that row.</summary>
    [Fact]
    public async Task Revoking_Your_Own_Session_Ends_It()
    {
        var clock = ClockAt();
        var user = await SeedUserAsync(Unique.Email("self-revoke"));
        var ticket = await CreateSessionAsync(user, clock);
        var keep = await CreateSessionAsync(user, clock);

        (await CreateRoutes(clock).RevokeOneAsync(ProofOf(ticket), ticket.SessionId, orgId: null, requestId: null, traceId: null, Token))
            .ShouldBe(SessionRouteStatus.Done);

        (await OwnerScalarAsync<bool>(
            "select revoked_at is not null from public.session where id = @id",
            ("id", ticket.SessionId))).ShouldBeTrue();

        (await OwnerScalarAsync<bool>(
            "select revoked_at is null from public.session where id = @id",
            ("id", keep.SessionId))).ShouldBeTrue("the caller's other session is not touched");
    }

    /// <summary>
    /// AC6: another account's live session answers the same <c>not_found</c> with no org header and with an
    /// org the actor is not an admin of, and the row stays live — the route is not an oracle (plan §8).
    /// </summary>
    [Fact]
    public async Task Revoking_Another_Users_Session_Is_NotFound_With_Or_Without_An_Org()
    {
        var clock = ClockAt();
        var actor = await SeedUserAsync(Unique.Email("revoke-actor"));
        var victim = await SeedUserAsync(Unique.Email("revoke-victim"));
        var actorTicket = await CreateSessionAsync(actor, clock);
        var victimTicket = await CreateSessionAsync(victim, clock);
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var routes = CreateRoutes(clock);

        (await routes.RevokeOneAsync(ProofOf(actorTicket), victimTicket.SessionId, orgId: null, requestId: null, traceId: null, Token))
            .ShouldBe(SessionRouteStatus.NotFound);

        (await routes.RevokeOneAsync(ProofOf(actorTicket), victimTicket.SessionId, org, requestId: null, traceId: null, Token))
            .ShouldBe(SessionRouteStatus.NotFound, "the actor is not an admin of that org");

        (await OwnerScalarAsync<bool>(
            "select revoked_at is null from public.session where id = @id",
            ("id", victimTicket.SessionId))).ShouldBeTrue("a refused revoke writes nothing");
    }

    /// <summary>
    /// AC6: an admin (the built-in <c>tenant_admin</c>, which grants <c>membership:write</c>) revokes a
    /// member's session in their own org, immediately; a second call is <c>not_found</c> because the target
    /// is dead while the actor is still live.
    /// </summary>
    [Fact]
    public async Task An_Admin_With_Membership_Write_Revokes_A_Members_Session()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var memberTicket = await CreateSessionAsync(member, clock);
        var routes = CreateRoutes(clock);

        (await routes.RevokeOneAsync(ProofOf(adminTicket), memberTicket.SessionId, org, requestId: null, traceId: null, Token))
            .ShouldBe(SessionRouteStatus.Done);

        (await OwnerScalarAsync<bool>(
            "select revoked_at is not null from public.session where id = @id",
            ("id", memberTicket.SessionId))).ShouldBeTrue("the member's session is revoked on the next request");

        var replay = await AuthenticateAsync(memberTicket.CookieValue, clock);
        replay.Value.Status.ShouldBe(SessionAuthStatus.Revoked);

        (await routes.RevokeOneAsync(ProofOf(adminTicket), memberTicket.SessionId, org, requestId: null, traceId: null, Token))
            .ShouldBe(SessionRouteStatus.NotFound, "the target is dead; the actor is still live");
    }

    /// <summary>
    /// AC6 boundary: a member of the same org WITHOUT <c>membership:write</c> — here <c>viewer</c> — cannot
    /// revoke a colleague's session, and the refusal is the same <c>not_found</c>.
    /// </summary>
    [Fact]
    public async Task A_Member_Without_Membership_Write_Cannot_Revoke_A_Colleagues_Session()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var plain = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);
        var colleague = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var plainTicket = await CreateSessionAsync(plain, clock);
        var colleagueTicket = await CreateSessionAsync(colleague, clock);

        (await CreateRoutes(clock).RevokeOneAsync(ProofOf(plainTicket), colleagueTicket.SessionId, org, requestId: null, traceId: null, Token))
            .ShouldBe(SessionRouteStatus.NotFound);

        (await OwnerScalarAsync<bool>(
            "select revoked_at is null from public.session where id = @id",
            ("id", colleagueTicket.SessionId))).ShouldBeTrue("the colleague's session survives the refusal");
    }

    /// <summary>
    /// AC6 boundary: an admin is an admin of ONE org. A member of another org is <c>not_found</c> through the
    /// admin's own org, and through the other org the admin holds no membership at all.
    /// </summary>
    [Fact]
    public async Task An_Admin_Cannot_Revoke_A_Members_Session_Through_Another_Org()
    {
        var clock = ClockAt();
        var adminOrg = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var otherOrg = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, adminOrg, "tenant_admin", cancellationToken: Token);
        var foreignMember = await IdentityTestData.SeedActiveMemberAsync(Fixture, otherOrg, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var foreignTicket = await CreateSessionAsync(foreignMember, clock);
        var routes = CreateRoutes(clock);

        (await routes.RevokeOneAsync(ProofOf(adminTicket), foreignTicket.SessionId, adminOrg, requestId: null, traceId: null, Token))
            .ShouldBe(SessionRouteStatus.NotFound, "the target is not a member of the admin's org");

        (await routes.RevokeOneAsync(ProofOf(adminTicket), foreignTicket.SessionId, otherOrg, requestId: null, traceId: null, Token))
            .ShouldBe(SessionRouteStatus.NotFound, "the admin holds no membership in the target's org");

        (await OwnerScalarAsync<bool>(
            "select revoked_at is null from public.session where id = @id",
            ("id", foreignTicket.SessionId))).ShouldBeTrue();
    }

    /// <summary>
    /// A custom role is an authority only inside its own org: the same-org role holding <c>membership:write</c>
    /// authorizes the revoke (the control, so the only difference is the role's org), and the row that used to
    /// point an org-A membership at another org's role is refused by the database itself, so the definer never
    /// sees a foreign grant to ignore.
    /// </summary>
    [Fact]
    public async Task Only_A_Custom_Role_Of_The_Same_Org_Authorizes_A_Revoke()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var otherOrg = await Fixture.SeedOrgAsync(cancellationToken: Token);

        var localActor = await SeedMemberWithRoleAsync(
            org, await SeedCustomRoleAsync(org, "session_writer_local"), Unique.Email("custom-role-local"));
        var foreignRole = await SeedCustomRoleAsync(otherOrg, "session_writer_foreign");

        var localTarget = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);
        var localTicket = await CreateSessionAsync(localActor, clock);
        var localTargetTicket = await CreateSessionAsync(localTarget, clock);
        var routes = CreateRoutes(clock);

        (await routes.RevokeOneAsync(ProofOf(localTicket), localTargetTicket.SessionId, org, requestId: null, traceId: null, Token))
            .ShouldBe(SessionRouteStatus.Done,
                "a custom role of THIS org carrying membership:write authorizes the revoke");

        (await OwnerScalarAsync<bool>(
            "select revoked_at is not null from public.session where id = @id",
            ("id", localTargetTicket.SessionId))).ShouldBeTrue();

        // The role key refuses the foreign grant, so the definer's org scope is a second line of defence:
        // the row that used to make the foreign role an authority cannot be written at all.
        var membershipId = await OwnerScalarAsync<Guid>(
            "select id from public.membership where org_id = @org and user_id = @user",
            ("org", org),
            ("user", localActor));
        var refusal = await OwnerRefusalAsync(
            "insert into public.membership_role (id, org_id, membership_id, role_id) values (@id, @org, @membership, @role)",
            ("id", Guid.CreateVersion7()),
            ("org", org),
            ("membership", membershipId),
            ("role", foreignRole));
        refusal.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        refusal.ConstraintName.ShouldBe("membership_role_role_guard");
        (await OwnerScalarAsync<long>(
            "select count(*) from public.membership_role where org_id = @org and role_id = @role",
            ("org", org),
            ("role", foreignRole))).ShouldBe(0L, "no row points an org-A membership at another org's role");
    }

    /// <summary>
    /// QA G4 (admin half): the grant join needs the ACTOR's membership active. A membership that was
    /// removed, revoked, expired or never activated still holds its <c>membership_role</c> row, so only the
    /// <c>status = 'active'</c> predicate can refuse it — and the refusal is the same <c>not_found</c>.
    /// </summary>
    /// <param name="status">The status the admin's own membership was left in.</param>
    [Theory]
    [InlineData("removed")]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("pending")]
    public async Task An_Admin_Whose_Membership_Is_Not_Active_Cannot_Revoke(string status)
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(
            Fixture, org, "tenant_admin", status, Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var memberTicket = await CreateSessionAsync(member, clock);

        (await CreateRoutes(clock).RevokeOneAsync(ProofOf(adminTicket), memberTicket.SessionId, org, requestId: null, traceId: null, Token))
            .ShouldBe(SessionRouteStatus.NotFound, $"a '{status}' membership holds no membership:write");

        (await OwnerScalarAsync<bool>(
            "select revoked_at is null from public.session where id = @id",
            ("id", memberTicket.SessionId))).ShouldBeTrue("the member's session survives the refusal");
    }

    /// <summary>
    /// QA G4 (target half): the target must be an ACTIVE member of the org the admin acts in. A member
    /// whose membership was removed, revoked or is still pending keeps a live session (sessions are
    /// platform-scoped, not membership-scoped), and that session is exactly what the admin path must not
    /// reach.
    /// </summary>
    /// <param name="status">The status the target's membership was left in.</param>
    [Theory]
    [InlineData("removed")]
    [InlineData("revoked")]
    [InlineData("pending")]
    public async Task A_Target_That_Is_Not_Active_In_The_Org_Cannot_Be_Revoked(string status)
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(
            Fixture, org, "tenant_admin", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", status, Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var memberTicket = await CreateSessionAsync(member, clock);

        (await CreateRoutes(clock).RevokeOneAsync(ProofOf(adminTicket), memberTicket.SessionId, org, requestId: null, traceId: null, Token))
            .ShouldBe(SessionRouteStatus.NotFound, $"a '{status}' membership is not an active member of the org");

        (await OwnerScalarAsync<bool>(
            "select revoked_at is null from public.session where id = @id",
            ("id", memberTicket.SessionId))).ShouldBeTrue("the target's session is left exactly as it was");
    }

    /// <summary>
    /// AC6 boundary: an id nothing names, and a live-looking id that is already past its idle deadline, are
    /// both <c>not_found</c> — and the revoke does not stamp a dead row it was refused on.
    /// </summary>
    [Fact]
    public async Task An_Unknown_Or_Expired_Session_Id_Is_NotFound_And_Stays_Untouched()
    {
        var clock = ClockAt();
        var actor = await SeedUserAsync(Unique.Email("revoke-unknown"));
        var actorTicket = await CreateSessionAsync(actor, clock);
        var expired = await CreateSessionAsync(actor, clock);

        await OwnerExecuteAsync(
            "update public.session set expires_at = @past where id = @id",
            ("id", expired.SessionId),
            ("past", clock.UtcNow.Subtract(TimeSpan.FromMinutes(1))));

        var routes = CreateRoutes(clock);

        (await routes.RevokeOneAsync(ProofOf(actorTicket), Guid.CreateVersion7(), orgId: null, requestId: null, traceId: null, Token))
            .ShouldBe(SessionRouteStatus.NotFound);

        (await routes.RevokeOneAsync(ProofOf(actorTicket), expired.SessionId, orgId: null, requestId: null, traceId: null, Token))
            .ShouldBe(SessionRouteStatus.NotFound, "an expired session is dead to the route too");

        (await OwnerScalarAsync<bool>(
            "select revoked_at is null and expired_at is null from public.session where id = @id",
            ("id", expired.SessionId))).ShouldBeTrue("a refused revoke leaves expiry stamping to the sweep");
    }

    /// <summary>
    /// AC6: "all but this one" — every other live session of the caller is revoked and counted, the current
    /// one survives, and another account keeps its session.
    /// </summary>
    [Fact]
    public async Task Revoke_Others_Keeps_The_Current_Session_And_Counts_The_Rest()
    {
        var clock = ClockAt();
        var user = await SeedUserAsync(Unique.Email("revoke-others"));
        var bystander = await SeedUserAsync(Unique.Email("revoke-others-bystander"));

        var current = await CreateSessionAsync(user, clock);
        var second = await CreateSessionAsync(user, clock);
        var third = await CreateSessionAsync(user, clock);
        var other = await CreateSessionAsync(bystander, clock);

        var (status, revoked) = await CreateRoutes(clock).RevokeOthersAsync(ProofOf(current), keepCurrent: true, Token);

        status.ShouldBe(SessionRouteStatus.Done);
        revoked.ShouldBe(2);

        (await OwnerScalarAsync<bool>(
            "select revoked_at is null from public.session where id = @id",
            ("id", current.SessionId))).ShouldBeTrue("keep_current=true keeps the session in use");

        foreach (var ended in new[] { second.SessionId, third.SessionId })
        {
            (await OwnerScalarAsync<bool>(
                "select revoked_at is not null from public.session where id = @id",
                ("id", ended))).ShouldBeTrue("every other live session of the caller is revoked");
        }

        (await OwnerScalarAsync<bool>(
            "select revoked_at is null from public.session where id = @id",
            ("id", other.SessionId))).ShouldBeTrue("another account keeps its session");
    }

    /// <summary>AC6/AC8: <c>keep_current = false</c> ends the current session too, and the cookie dies with it.</summary>
    [Fact]
    public async Task Revoke_Others_Can_End_The_Current_Session_Too()
    {
        var clock = ClockAt();
        var user = await SeedUserAsync(Unique.Email("revoke-others-all"));
        var current = await CreateSessionAsync(user, clock);
        await CreateSessionAsync(user, clock);

        var (status, revoked) = await CreateRoutes(clock)
            .RevokeOthersAsync(ProofOf(current), keepCurrent: false, Token);

        status.ShouldBe(SessionRouteStatus.Done);
        revoked.ShouldBe(2, "the current session is one of the rows the caller asked to end");

        var replay = await AuthenticateAsync(current.CookieValue, clock);
        replay.Value.Status.ShouldBe(SessionAuthStatus.Revoked);
    }

    /// <summary>An absent proof revokes nothing and answers <c>unauthenticated</c>.</summary>
    [Fact]
    public async Task Revoke_Others_Without_A_Proof_Is_Unauthenticated_And_Revokes_Nothing()
    {
        var clock = ClockAt();
        var user = await SeedUserAsync(Unique.Email("revoke-others-anon"));
        var ticket = await CreateSessionAsync(user, clock);

        var (status, revoked) = await CreateRoutes(clock).RevokeOthersAsync([], keepCurrent: false, Token);

        status.ShouldBe(SessionRouteStatus.Unauthenticated);
        revoked.ShouldBe(0);

        (await OwnerScalarAsync<bool>(
            "select revoked_at is null from public.session where id = @id",
            ("id", ticket.SessionId))).ShouldBeTrue();
    }

    /// <summary>
    /// One custom (org-owned) role of <paramref name="org"/> carrying a single tenant-assignable
    /// permission code, written through the owner seam the way the custom-role commands would write it.
    /// </summary>
    /// <param name="org">The org the role belongs to — the scope the definer has to honour.</param>
    /// <param name="code">The role code (lowercase, as <c>role_code_format_check</c> requires).</param>
    /// <param name="permission">The permission code the role grants.</param>
    private async Task<Guid> SeedCustomRoleAsync(Guid org, string code, string permission = "membership:write")
    {
        var role = Guid.CreateVersion7();
        await OwnerExecuteAsync(
            """
            insert into public.role (id, org_id, code, name, is_builtin, kind)
            values (@id, @org_id, @code, @name, false, 'member')
            """,
            ("id", role),
            ("org_id", org),
            ("code", code),
            ("name", code));

        await OwnerExecuteAsync(
            """
            insert into public.role_permission (id, org_id, role_id, permission_code)
            values (@id, @org_id, @role_id, @code)
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", org),
            ("role_id", role),
            ("code", permission));

        return role;
    }

    /// <summary>
    /// An ACTIVE membership in <paramref name="org"/> whose single role is named by id — the arrangement that
    /// can point at a role of another org, which no command would ever write.
    /// </summary>
    /// <param name="org">The org the membership belongs to.</param>
    /// <param name="roleId">The role the membership holds.</param>
    /// <param name="email">The account's address.</param>
    private async Task<Guid> SeedMemberWithRoleAsync(Guid org, Guid roleId, string email)
    {
        var user = await SeedUserAsync(email);
        var membership = Guid.CreateVersion7();

        await OwnerExecuteAsync(
            """
            insert into public.membership
                (id, org_id, user_id, invited_email, status, created_at, updated_at, version)
            values (@id, @org_id, @user_id, @email, 'active', now(), now(), 1)
            """,
            ("id", membership),
            ("org_id", org),
            ("user_id", user),
            ("email", email));

        await OwnerExecuteAsync(
            """
            insert into public.membership_role
                (id, org_id, membership_id, role_id, granted_at, created_at, updated_at, version)
            values (@id, @org_id, @membership_id, @role_id, now(), now(), now(), 1)
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", org),
            ("membership_id", membership),
            ("role_id", roleId));

        return user;
    }

    /// <summary>
    /// The sweep connection the four route statements never open: reaching it means the store called the
    /// wrong role for a route, which is a defect and not a fallback.
    /// </summary>
    private sealed class UnusedSweep : IWorkerSchedDataSource
    {
        public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("a route statement must never open the sweep connection");
    }
}
