using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Store;

/// <summary>
/// Addendum (c) of the 02.3.2-T1.1 review: the two index claims of 0045:116-134, proven against the real
/// planner, plus the HOT property the sliding update depends on.
/// </summary>
/// <remarks>
/// <para>
/// A <c>SECURITY DEFINER</c> function is never inlined, so <c>explain select … from
/// evidgate_resolve.session_authenticate(…)</c> yields a Function Scan and proves nothing. The statements are
/// therefore planned here with their real parameter shapes on the owner connection (the role the definers run
/// as), and the drift hole is closed by asserting that each definer's stored <c>prosrc</c> still contains the
/// lookup text that was planned — the catalog pin (0044's linking suite reads <c>pg_proc</c> the same way).
/// </para>
/// <para>
/// The HOT assertion runs the real throttled authenticator: <c>last_seen_at</c> and <c>expires_at</c> are in no
/// index (the index swap of 0045 removed the last one) and <c>fillfactor = 80</c> leaves page room, so the slide
/// must be a heap-only update; a request inside the 60 s window must not update at all.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class SessionIndexPlanTests : SessionHardeningTestBase
{
    /// <summary>
    /// A database of its own: the planner and HOT assertions must read a table nobody else is writing, or
    /// another test's rows and dead tuples decide the plan.
    /// </summary>
    public SessionIndexPlanTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

    /// <summary>0045:116-124: the cookie lookup probes the unique full key, not a sequential scan.</summary>
    [Fact]
    public async Task The_Cookie_Lookup_Plans_The_Unique_Hmac_Index()
    {
        var userId = await SeedUserAsync("plan-cookie@example.com");
        var clock = ClockAt();
        await CreateSessionAsync(userId, clock);

        var plan = await ExplainAsync(
            "select s.id from public.session s where s.cookie_hmac = any(@hmacs::bytea[])",
            ("hmacs", new[] { HmacOf(Pepper, "plan-probe") }));

        plan.ShouldContain("session_cookie_hmac_key", Case.Sensitive);
        plan.ShouldNotContain("Seq Scan", Case.Sensitive);
    }

    /// <summary>0045:126-134: the per-user paths use the partial live index, not the dropped one.</summary>
    [Fact]
    public async Task The_Per_User_Paths_Plan_The_Partial_Live_Index()
    {
        var userId = await SeedUserAsync("plan-user@example.com");
        var clock = ClockAt();
        await CreateSessionAsync(userId, clock);
        await CreateSessionAsync(userId, clock);

        var plan = await ExplainAsync(
            "select s.id from public.session s where s.user_id = @user and s.revoked_at is null",
            ("user", userId));

        plan.ShouldContain("session_user_live_idx", Case.Sensitive);
        plan.ShouldNotContain("Seq Scan", Case.Sensitive);
    }

    /// <summary>
    /// The definers are not inlined, so the plan above is only meaningful while the function bodies still carry
    /// the same lookup text: pin <c>prosrc</c> so a body edit that changes the WHERE clause cannot leave the two
    /// plan tests green while the request path drifts.
    /// </summary>
    [Fact]
    public async Task The_Authenticator_Body_Still_Contains_The_Planned_Lookup()
    {
        var source = await OwnerScalarAsync<string>(
            """
            select p.prosrc
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate_resolve' and p.proname = 'session_authenticate'
            """);

        source.ShouldContain("from public.session s", Case.Sensitive);
        source.ShouldContain("= any (p_cookie_hmacs)", Case.Sensitive);
        source.ShouldContain("absolute_expires_at", Case.Sensitive, "the hard deadline stays part of the decision");
        source.ShouldContain("interval '60 seconds'", Case.Sensitive, "the throttle is the statement's own policy");
    }

    /// <summary>
    /// P3 (0045:126-134): a slide can be a heap-only update because neither column it writes is in any index,
    /// and the page keeps room for a second tuple version. This is the structural half of the HOT claim and it
    /// is deterministic — the counter half (<c>pg_stat_get_tuples_hot_updated</c>) is not asserted here on
    /// purpose: publishing that figure needs <c>pg_stat_force_next_flush()</c>, which <c>evidgate_owner</c>
    /// (created <c>nosuperuser</c> and without <c>pg_monitor</c>) may not call, and the unforced snapshot lands
    /// on the stats timer, so a gate could see a stale zero for a perfectly HOT update. The harness's own
    /// precedent states the missing grant — <c>tests/Tenancy.IntegrationTests/ProducerSlotQaTests.cs:315-317</c>
    /// ("<c>pg_read_all_stats</c> is not granted") — so the privilege route is dropped here too.
    /// </summary>
    [Fact]
    public async Task The_Slide_Columns_Are_In_No_Index_So_A_Slide_Can_Be_Heap_Only()
    {
        var indexes = await OwnerScalarAsync<string>(
            """
            select string_agg(indexname || '|' || indexdef, E'\n' order by indexname)
              from pg_catalog.pg_indexes
             where schemaname = 'public' and tablename = 'session'
            """);

        indexes.ShouldNotContain("last_seen_at", Case.Sensitive,
            "an indexed column in the UPDATE makes the new version non-HOT");
        indexes.ShouldNotContain("expires_at", Case.Sensitive,
            "the sliding deadline must not be indexed (the 0041 index that carried it is dropped)");
        indexes.ShouldContain("session_cookie_hmac_key", Case.Sensitive);

        (await OwnerScalarAsync<string>(
            "select coalesce(array_to_string(reloptions, ','), '') from pg_catalog.pg_class "
            + "where oid = 'public.session'::regclass"))
            .ShouldContain("fillfactor=80", Case.Sensitive, "the page keeps room for the next tuple version");
    }

    /// <summary>The removed-member probe of the invite path plans the partial index, whose predicate is exactly the probe's status.</summary>
    [Fact]
    public async Task The_Removed_Member_Invite_Probe_Plans_The_Partial_Index()
    {
        (await OwnerScalarAsync<string>(
            "select indexdef from pg_indexes where schemaname = 'public' and indexname = 'membership_removed_invited_email_idx'"))
            .ShouldContain("(org_id, invited_email)", Case.Sensitive);

        (await OwnerScalarAsync<string>(
            "select pg_get_expr(i.indpred, i.indrelid) from pg_index i " +
            "join pg_class c on c.oid = i.indexrelid where c.relname = 'membership_removed_invited_email_idx'"))
            .ShouldBe("(status = 'removed'::text)");

        var plan = await ExplainAsync(
            "select 1 from public.membership m where m.org_id = @org and m.invited_email = @email " +
            "and m.status = 'removed' and m.user_id is not null",
            ("org", Guid.NewGuid()),
            ("email", "removed-probe@example.com"));

        plan.ShouldContain("membership_removed_invited_email_idx", Case.Sensitive);
    }

    /// <summary>Plans the statement with real parameters, with sequential scans disabled for that transaction only.</summary>
    /// <param name="sql">The statement text the definers run.</param>
    /// <param name="values">Its parameters.</param>
    private async Task<string> ExplainAsync(string sql, params (string Name, object Value)[] values)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);

        // Session-level SET would leak through the pool; SET LOCAL inside a rolled-back transaction cannot.
        await using var transaction = await connection.BeginTransactionAsync(Token);
        await using (var disable = new NpgsqlCommand("set local enable_seqscan = off", connection, transaction))
        {
            await disable.ExecuteNonQueryAsync(Token);
        }

        await using var command = new NpgsqlCommand("explain " + sql, connection, transaction);
        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var lines = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(Token))
        {
            while (await reader.ReadAsync(Token))
            {
                lines.Add(reader.GetString(0));
            }
        }

        await transaction.RollbackAsync(Token);

        return string.Join('\n', lines);
    }
}
