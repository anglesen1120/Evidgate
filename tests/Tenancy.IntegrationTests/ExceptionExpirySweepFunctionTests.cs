using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The two owner-side functions of the waiver/quarantine expiry sweep against a real PostgreSQL: the tenant-bound
/// transition (due rows only, boundary at the transaction clock, one limit across both kinds, refusals) and the
/// org-id projection the scheduled job pages. A raw app UPDATE to <c>expired</c> stays refused, so these functions are
/// the only path that can write it.
/// </summary>
[Trait("Category", "Docker")]
public sealed class ExceptionExpirySweepFunctionTests
{
    private const string Sweep = "evidgate.sweep_expired_exceptions(@org, @limit)";
    private const string CountSweep = "select count(*) from " + Sweep;
    private const string Projection = "evidgate.exception_expiry_org_ids";

    private readonly PostgresTenancyFixture _fixture;

    public ExceptionExpirySweepFunctionTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Sweep_ExpiresBothKindsAtTheTransactionClockOnce_LeavesFutureAndOtherStatesUntouched()
    {
        var db = await _fixture.SharedDatabaseAsync();
        var tenant = await FixtureSeed.TenantAsync(_fixture, db);
        await using var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(db), tenant.Org);
        // The caller's transaction clock is the boundary: rows seeded at exactly now() are due, one second later is not.
        var now = await app.ScalarAsync<DateTime>("select now()");
        var waiver = await WaiverAsync(db, tenant, "approved", now);
        var quarantine = await QuarantineAsync(db, tenant, "active", now);
        var untouched = new[]
        {
            (await WaiverAsync(db, tenant, "approved", now.AddSeconds(1)), "approved"),
            (await QuarantineAsync(db, tenant, "active", now.AddSeconds(1)), "active"),
            (await WaiverAsync(db, tenant, "requested", now), "requested"),
            (await WaiverAsync(db, tenant, "revoked", now), "revoked"),
            (await QuarantineAsync(db, tenant, "removed", now), "removed"),
            (await WaiverAsync(db, tenant, "expired", now), "expired"),
            (await QuarantineAsync(db, tenant, "expired", now), "expired"),
        };

        var exactlyTheTwoDueRows = await app.ScalarAsync<bool>(
            $"""
            select count(*) = 2
                   and count(*) filter (where kind = 'waiver' and id = @w) = 1
                   and count(*) filter (where kind = 'quarantine' and id = @q) = 1
                   and bool_and(project_id = @project and expires_at = now())
              from {Sweep}
            """,
            ("org", tenant.Org), ("limit", 10), ("w", waiver), ("q", quarantine), ("project", tenant.Project));

        exactlyTheTwoDueRows.ShouldBeTrue();
        (await app.ScalarAsync<long>(CountSweep, ("org", tenant.Org), ("limit", 10)))
            .ShouldBe(0, "a rerun finds nothing left to expire");
        (await StateVersionAsync(app, waiver)).ShouldBe("expired:2");
        (await StateVersionAsync(app, quarantine)).ShouldBe("expired:2");
        foreach (var (id, state) in untouched)
        {
            (await StateVersionAsync(app, id)).ShouldBe(state + ":1");
        }
    }

    [Fact]
    public async Task Sweep_AppliesOneClampedLimitAcrossBothKinds_OldestFirst()
    {
        var db = await _fixture.SharedDatabaseAsync();
        var tenant = await FixtureSeed.TenantAsync(_fixture, db);
        await using var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(db), tenant.Org);
        var now = await app.ScalarAsync<DateTime>("select now()");
        var oldest = await QuarantineAsync(db, tenant, "active", now.AddSeconds(-2));
        await WaiversAsync(db, tenant, "approved", now, Enumerable.Range(0, 1001).Select(_ => Guid.CreateVersion7()).ToArray());

        (await app.ScalarAsync<Guid[]>($"select array_agg(id) from {Sweep}", ("org", tenant.Org), ("limit", -10)))
            .ShouldBe([oldest], "a limit below 1 clamps to 1 and the oldest due row wins across kinds");
        (await app.ScalarAsync<long>(CountSweep, ("org", tenant.Org), ("limit", int.MaxValue)))
            .ShouldBe(1000, "a limit above 1000 clamps to 1000 across both kinds");
        (await app.ScalarAsync<long>(CountSweep, ("org", tenant.Org), ("limit", int.MaxValue)))
            .ShouldBe(1, "the rest follows on the next call");
    }

    [Fact]
    public async Task Sweep_SkipsRowsLockedByAnotherTransaction_AndTakesThemOnceReleased()
    {
        var db = await _fixture.SharedDatabaseAsync();
        var tenant = await FixtureSeed.TenantAsync(_fixture, db);
        await using var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(db), tenant.Org);
        var now = await app.ScalarAsync<DateTime>("select now()");
        var locked = await WaiverAsync(db, tenant, "approved", now.AddMinutes(-1));
        var available = await WaiverAsync(db, tenant, "approved", now);
        await using (var holder = await TenantScope.BeginAsync(_fixture.AppConnectionString(db), tenant.Org))
        {
            (await holder.ScalarAsync<Guid>("select id from public.waiver where id = @id for update", ("id", locked)))
                .ShouldBe(locked);
            (await app.ScalarAsync<Guid[]>($"select array_agg(id) from {Sweep}", ("org", tenant.Org), ("limit", 1)))
                .ShouldBe([available], "the older row is locked, so the sweep moves on instead of waiting");
        }

        (await app.ScalarAsync<Guid[]>($"select array_agg(id) from {Sweep}", ("org", tenant.Org), ("limit", 1)))
            .ShouldBe([locked]);
    }

    [Fact]
    public async Task Projection_PagesOnlyDistinctDueOrgsInKeyOrder_ClampsBothEnds()
    {
        await using var db = await _fixture.CreateDedicatedDatabaseAsync();
        var waiverTenant = await FixtureSeed.TenantAsync(_fixture, db.Name);
        var quarantineTenant = await FixtureSeed.TenantAsync(_fixture, db.Name);
        var futureTenant = await FixtureSeed.TenantAsync(_fixture, db.Name);
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(db.Name));
        var now = await owner.ScalarAsync<DateTime>("select now()");
        await WaiverAsync(db.Name, waiverTenant, "approved", now);
        await QuarantineAsync(db.Name, waiverTenant, "active", now);
        await QuarantineAsync(db.Name, quarantineTenant, "active", now);
        await WaiverAsync(db.Name, futureTenant, "approved", now.AddDays(1));
        await QuarantineAsync(db.Name, futureTenant, "removed", now);
        var expected = InDatabaseOrder([waiverTenant.Org, quarantineTenant.Org]);

        await using var sched = await _fixture.OpenSuperuserAsync(db.Name);
        await using (var role = new NpgsqlCommand("set role evidgate_worker_sched", sched))
        {
            await role.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        (await PageAsync(sched, null, 0)).ShouldBe([expected[0]], "a limit of 0 clamps to one id; two due rows of one org count once");
        (await PageAsync(sched, expected[0], 10)).ShouldBe([expected[1]]);
        (await PageAsync(sched, expected[1], 10)).ShouldBeEmpty();

        // One set-based seed: a thousand separate tenant transactions would dominate the run.
        await owner.ExecuteAsync(
            """
            with orgs as (
              insert into public.organization (id, name, slug)
              select uuidv7(), 'expiry probe', 'expiry-' || n from generate_series(1, 1000) n returning id
            ), projects as (
              insert into public.project (id, org_id, name, slug)
              select uuidv7(), id, 'expiry probe', 'expiry' from orgs returning id, org_id
            )
            insert into public.waiver (id, org_id, project_id, scope, target_id, reason, requested_by, approved_by, state, expires_at)
            select uuidv7(), org_id, id, 'case', uuidv7(), 'probe', uuidv7(), uuidv7(), 'approved', now() from projects
            """);
        await owner.CommitAsync();

        var page = await PageAsync(sched, null, int.MaxValue);
        page.Length.ShouldBe(1000, "a limit above 1000 clamps to 1000");
        page.ShouldBe(InDatabaseOrder(page), "ids come back in the database's uuid order, the keyset order");
        (await PageAsync(sched, page[^1], int.MaxValue)).Length.ShouldBe(2, "the keyset continues after the last id");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sweep_RefusesAnotherOrgOrAnUnsetTenant_AndLeavesTheTargetRowAlone(bool callerHoldsAnotherOrg)
    {
        var db = await _fixture.SharedDatabaseAsync();
        var target = await FixtureSeed.TenantAsync(_fixture, db);
        var other = await FixtureSeed.TenantAsync(_fixture, db);
        await using var positive = await TenantScope.BeginAsync(_fixture.AppConnectionString(db), target.Org);
        var now = await positive.ScalarAsync<DateTime>("select now()");
        var id = await WaiverAsync(db, target, "approved", now);

        await using (var denied = await TenantScope.BeginAsync(
            _fixture.AppConnectionString(db), callerHoldsAnotherOrg ? other.Org : null))
        {
            (await denied.CaptureErrorAsync(CountSweep, ("org", target.Org), ("limit", 1))).SqlState.ShouldBe("42501");
        }

        (await StateVersionAsync(positive, id)).ShouldBe("approved:1");
        (await positive.ScalarAsync<long>(CountSweep, ("org", target.Org), ("limit", 1)))
            .ShouldBe(1, "the same call under the target's own tenant works, so the refusal was the tenant check");
    }

    [Theory]
    [InlineData("waiver")]
    [InlineData("quarantine_entry")]
    public async Task RawAppUpdateToExpired_IsRefusedEvenForADueRow_WhileTheSweepStillExpiresIt(string table)
    {
        var db = await _fixture.SharedDatabaseAsync();
        var tenant = await FixtureSeed.TenantAsync(_fixture, db);
        await using var positive = await TenantScope.BeginAsync(_fixture.AppConnectionString(db), tenant.Org);
        var now = await positive.ScalarAsync<DateTime>("select now()");
        var id = table == "waiver"
            ? await WaiverAsync(db, tenant, "approved", now)
            : await QuarantineAsync(db, tenant, "active", now);

        await using (var denied = await TenantScope.BeginAsync(_fixture.AppConnectionString(db), tenant.Org))
        {
            (await denied.CaptureErrorAsync($"update public.{table} set state = 'expired' where id = @id", ("id", id)))
                .SqlState.ShouldBe("23514");
        }

        (await StateVersionAsync(positive, id)).ShouldBe(table == "waiver" ? "approved:1" : "active:1");
        (await positive.ScalarAsync<long>(CountSweep, ("org", tenant.Org), ("limit", 1))).ShouldBe(1);
        (await StateVersionAsync(positive, id)).ShouldBe("expired:2");
    }

    [Fact]
    public async Task Functions_HoldExecuteForOneDesignatedRoleEach_AndNeverForPublic()
    {
        var db = await _fixture.SharedDatabaseAsync();
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(db));

        foreach (var (function, grantee) in new[]
        {
            (Projection, "evidgate_worker_sched"),
            ("evidgate.sweep_expired_exceptions", "evidgate_app"),
        })
        {
            var holders = await owner.ScalarAsync<string[]>(
                """
                select array_agg(coalesce(r.rolname::text, 'PUBLIC') order by coalesce(r.rolname::text, 'PUBLIC'))
                  from pg_catalog.pg_proc p
                  cross join lateral pg_catalog.aclexplode(coalesce(p.proacl, pg_catalog.acldefault('f', p.proowner))) a
                  left join pg_catalog.pg_roles r on r.oid = a.grantee
                 where p.oid = pg_catalog.to_regprocedure(@signature)::oid
                   and a.privilege_type = 'EXECUTE'
                   and a.grantee <> p.proowner
                """,
                ("signature", function + "(uuid,integer)"));
            holders.ShouldBe([grantee], function);
        }
    }

    [Theory]
    [InlineData("evidgate_operator", true)]
    [InlineData("evidgate_operator", false)]
    [InlineData("evidgate_platform_operator", true)]
    [InlineData("evidgate_platform_operator", false)]
    [InlineData("evidgate_worker_sched", false)]
    [InlineData("evidgate_app", true)]
    public async Task Function_IsRefusedForARoleThatHoldsNoExecute(string role, bool projection)
    {
        var db = await _fixture.SharedDatabaseAsync();
        var org = Guid.CreateVersion7().ToString("D", CultureInfo.InvariantCulture);
        await using var connection = await _fixture.OpenSuperuserAsync(db);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);

        // The tenant is set to the very org the call names, so only a missing EXECUTE can refuse it.
        await RunAsync(connection, transaction, $"set local role {role}");
        await RunAsync(connection, transaction, $"select set_config('evidgate.org_id', '{org}', true)");
        var call = projection ? $"select * from {Projection}(null, 1)" : $"select * from evidgate.sweep_expired_exceptions('{org}', 1)";

        var error = await Should.ThrowAsync<PostgresException>(() => RunAsync(connection, transaction, call));
        error.SqlState.ShouldBe("42501");
    }

    private static async Task RunAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// PostgreSQL orders <c>uuid</c> by its bytes, which is the hex text; <see cref="Guid"/> compares its fields in
    /// memory order and would sort the same values differently.
    /// </summary>
    private static Guid[] InDatabaseOrder(IEnumerable<Guid> ids) =>
        [.. ids.OrderBy(id => id.ToString("D", CultureInfo.InvariantCulture), StringComparer.Ordinal)];

    private static async Task<Guid[]> PageAsync(NpgsqlConnection sched, Guid? after, int limit)
    {
        await using var command = new NpgsqlCommand($"select array(select id from {Projection}(@after, @limit) id)", sched);
        command.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Uuid) { Value = (object?)after ?? DBNull.Value });
        command.Parameters.AddWithValue("limit", limit);
        return (Guid[])(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static Task<string> StateVersionAsync(TenantScope scope, Guid id) => scope.ScalarAsync<string>(
        """
        select state || ':' || version from public.waiver where id = @id
        union all
        select state || ':' || version from public.quarantine_entry where id = @id
        """,
        ("id", id));

    private async Task<Guid> WaiverAsync(string db, FixtureTenant tenant, string state, DateTime expiry)
    {
        var id = Guid.CreateVersion7();
        await WaiversAsync(db, tenant, state, expiry, [id]);
        return id;
    }

    private async Task WaiversAsync(string db, FixtureTenant tenant, string state, DateTime expiry, Guid[] ids)
    {
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(db));
        await owner.ExecuteAsync(
            """
            insert into public.waiver (id, org_id, project_id, scope, target_id, reason, requested_by, approved_by, state, expires_at)
            select id, @org, @project, 'case', uuidv7(), 'probe', uuidv7(),
                   case when @state = 'approved' then uuidv7() end, @state, @expiry
              from unnest(@ids) id
            """,
            ("org", tenant.Org), ("project", tenant.Project), ("state", state), ("expiry", expiry), ("ids", ids));
        await owner.CommitAsync();
    }

    private async Task<Guid> QuarantineAsync(string db, FixtureTenant tenant, string state, DateTime expiry)
    {
        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(db));
        var id = await owner.ScalarAsync<Guid>(
            """
            with member as (
              insert into public.membership (id, org_id, invited_email, status)
              values (uuidv7(), @org, uuidv7()::text || '@example.test', 'active') returning id
            ), probe as (
              insert into public.test_case (id, org_id, project_id, annotation_id, name, framework, first_seen_commit)
              values (uuidv7(), @org, @project, uuidv7(), 'probe', 'playwright', 'abcdef0') returning id
            )
            insert into public.quarantine_entry (id, org_id, project_id, case_id, owner, reason, state, expires_at)
            select uuidv7(), @org, @project, probe.id, member.id, 'probe', @state, @expiry
              from probe cross join member
            returning id
            """,
            ("org", tenant.Org), ("project", tenant.Project), ("state", state), ("expiry", expiry));
        await owner.CommitAsync();
        return id;
    }
}
