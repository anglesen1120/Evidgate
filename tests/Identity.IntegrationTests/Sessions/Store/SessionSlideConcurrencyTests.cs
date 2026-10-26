using Evidgate.Modules.Identity.Contracts;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Store;

/// <summary>
/// P1 of the 02.3.2-T1.1 review: the 60 s throttle of <c>session_authenticate</c> is re-tested in the
/// <c>update</c>'s own predicate (0045:412-434), so of N requests that all read "slide is due" on an idle
/// session exactly ONE writes the row and the losers answer <c>slid = false</c>. The statements are
/// genuinely concurrent: every request is held on the session's row lock by a third transaction, so they
/// all reach the update with the same pre-slide snapshot instead of happening to run one after the other.
/// </summary>
/// <remarks>
/// Two independent witnesses of "one write". <see cref="Eight_Concurrent_Requests_On_One_Idle_Session_Slide_It_Once"/>
/// runs the real store and asserts the row a single slide leaves behind (one instant, one idle deadline,
/// an untouched absolute deadline) plus the control that the next request inside the window changes
/// nothing on the row at all. <see cref="Exactly_One_Concurrent_Statement_Reports_The_Slide"/> reads the
/// statement's own <c>slid</c> column — the outcome the fix made report rows ACTUALLY updated — and
/// counts one true among the eight answers. Neither witness uses table statistics, a trigger or a
/// timing threshold; the eight requests share one clock instant, so no interleaving can produce a
/// different row or a different count.
/// </remarks>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class SessionSlideConcurrencyTests : SessionHardeningTestBase
{
    /// <summary>The requests of one batch — more than the page a slide can write could ever hide.</summary>
    private const int ConcurrentRequests = 8;

    /// <summary>The default idle lifetime (<c>SessionOptions.IdleSeconds</c>, 28 800 s).</summary>
    private static readonly TimeSpan Idle = TimeSpan.FromHours(8);

    public SessionSlideConcurrencyTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// Eight store authenticates on a session idle past the throttle: all Active, and the row carries
    /// exactly one slide — <c>last_seen_at</c> at the shared instant, the idle deadline at that instant
    /// plus one lifetime, the absolute deadline and the revocation untouched.
    /// </summary>
    [Fact]
    public async Task Eight_Concurrent_Requests_On_One_Idle_Session_Slide_It_Once()
    {
        var userId = await SeedUserAsync("slide-race@example.com");
        var clock = ClockAt();
        var session = await CreateSessionAsync(userId, clock);
        clock.Advance(TimeSpan.FromSeconds(90));

        var before = await OwnerScalarAsync<string>(
            "select absolute_expires_at::text || '/' || coalesce(revoked_at::text, 'live')"
            + " from public.session where id = @id",
            ("id", session.SessionId));

        var store = CreateAuthenticator(clock);
        await using var holder = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await holder.OpenAsync(Token);
        await using var held = await holder.BeginTransactionAsync(Token);
        await HoldSessionRowAsync(holder, held, session.SessionId, Token);

        var calls = Enumerable.Range(0, ConcurrentRequests)
            .Select(_ => store.AuthenticateAsync(session.CookieValue, Token).AsTask())
            .ToArray();

        await Task.Delay(TimeSpan.FromMilliseconds(200), Token);
        calls.Count(call => !call.IsCompleted)
            .ShouldBe(ConcurrentRequests, "every request waits on the session's row lock");

        await held.CommitAsync(Token);

        var results = await Task.WhenAll(calls);
        foreach (var result in results)
        {
            result.IsSuccess.ShouldBeTrue(result.Error?.Code ?? "a live session authenticates");
            result.Value.Status.ShouldBe(SessionAuthStatus.Active);
            result.Value.SessionId.ShouldBe(session.SessionId);
            result.Value.UserId.ShouldBe(userId);
        }

        // One slide, at the one instant all eight requests carried. A row cannot count the writes — the
        // window slides TO the instant, so eight writes and one write leave the same two values — which is
        // why the count is read from the statement's own `slid` in the second test. What the row does prove
        // is that the slide landed on the shared instant, stayed inside the hard deadline and touched
        // nothing else.
        (await OwnerScalarAsync<bool>(
            "select last_seen_at = @now and expires_at = @expires from public.session where id = @id",
            ("id", session.SessionId),
            ("now", clock.UtcNow),
            ("expires", clock.UtcNow.Add(Idle))))
            .ShouldBeTrue("the idle window slid once, to the instant the eight requests shared");

        (await OwnerScalarAsync<string>(
            "select absolute_expires_at::text || '/' || coalesce(revoked_at::text, 'live')"
            + " from public.session where id = @id",
            ("id", session.SessionId)))
            .ShouldBe(before, "a slide touches neither the absolute deadline nor the revocation");

        // Control: the batch's write is the last one. A request inside the window that follows it writes
        // nothing, which the row's own version proves (no new tuple, no moved line pointer).
        var version = await OwnerScalarAsync<string>(
            "select xmin::text || '/' || ctid::text from public.session where id = @id",
            ("id", session.SessionId));

        var throttled = await store.AuthenticateAsync(session.CookieValue, Token);
        throttled.Value.Status.ShouldBe(SessionAuthStatus.Active);

        (await OwnerScalarAsync<string>(
            "select xmin::text || '/' || ctid::text from public.session where id = @id",
            ("id", session.SessionId)))
            .ShouldBe(version, "a throttled request inside the window writes no row version at all");
    }

    /// <summary>
    /// The statement's own <c>slid</c> outcome, eight times over: exactly one request reports that it
    /// wrote the row, the other seven report the throttle. This is the count the concurrency fix exists
    /// for, and it is read from the result rather than from cumulative server statistics.
    /// </summary>
    [Fact]
    public async Task Exactly_One_Concurrent_Statement_Reports_The_Slide()
    {
        var userId = await SeedUserAsync("slide-race-slid@example.com");
        var clock = ClockAt();
        var session = await CreateSessionAsync(userId, clock);
        clock.Advance(TimeSpan.FromSeconds(90));

        var hmac = ExpectedHmac(session.CookieValue);
        var now = clock.UtcNow.ToUniversalTime();

        await using var holder = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await holder.OpenAsync(Token);
        await using var held = await holder.BeginTransactionAsync(Token);
        await HoldSessionRowAsync(holder, held, session.SessionId, Token);

        var calls = Enumerable.Range(0, ConcurrentRequests)
            .Select(_ => AuthenticateAndReadSlideAsync(hmac, now))
            .ToArray();

        await Task.Delay(TimeSpan.FromMilliseconds(200), Token);
        calls.Count(call => !call.IsCompleted)
            .ShouldBe(ConcurrentRequests, "every statement waits on the session's row lock");

        await held.CommitAsync(Token);

        var outcomes = await Task.WhenAll(calls);

        outcomes.Count(outcome => string.Equals(outcome.Status, "active", StringComparison.Ordinal))
            .ShouldBe(ConcurrentRequests, "the throttle decides a write, never the status");
        outcomes.Count(outcome => outcome.Slid)
            .ShouldBe(1, "of eight requests on one idle session exactly one slides the row");

        (await OwnerScalarAsync<bool>(
            "select last_seen_at = @now from public.session where id = @id",
            ("id", session.SessionId),
            ("now", clock.UtcNow)))
            .ShouldBeTrue("the one winner slid the row to the instant every request carried");
    }

    /// <summary>One concurrent statement: the resolver the store calls, with its <c>slid</c> outcome.</summary>
    /// <param name="hmac">The presented cookie value's hash under the current pepper.</param>
    /// <param name="now">The instant the request carries.</param>
    private async Task<(string Status, bool Slid)> AuthenticateAndReadSlideAsync(byte[] hmac, DateTimeOffset now)
    {
        await using var command = AppDataSource.CreateCommand(
            "select status, slid from evidgate_resolve.session_authenticate("
            + "@cookie_hmacs, @current_hmac, @current_version, @now, @idle_seconds)");

        command.Parameters.AddWithValue("cookie_hmacs", new[] { hmac });
        command.Parameters.AddWithValue("current_hmac", hmac);
        command.Parameters.AddWithValue("current_version", CurrentVersion);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("idle_seconds", (int)Idle.TotalSeconds);

        await using var reader = await command.ExecuteReaderAsync(Token);
        (await reader.ReadAsync(Token)).ShouldBeTrue("the statement always answers one row");

        return (reader.GetString(0), reader.GetBoolean(1));
    }

    /// <summary>Holds one session's row lock so every request under test queues behind it.</summary>
    /// <param name="connection">The connection the lock is taken on.</param>
    /// <param name="transaction">The transaction the lock lives in.</param>
    /// <param name="sessionId">The session to lock.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    private static async Task HoldSessionRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select id from public.session where id = @id for update",
            connection,
            transaction);

        command.Parameters.AddWithValue("id", sessionId);
        (await command.ExecuteScalarAsync(cancellationToken)).ShouldBe(sessionId, "the session row is the lock");
    }
}
