using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Store;

/// <summary>
/// Addendum (a) of the 02.3.2-T1.1 review: rotation is ATOMIC AND SINGLE-WINNER (0045:415-451). Two rotations
/// of one predecessor run here against the real server at the same time — genuinely concurrent, held on the
/// row lock by a third transaction — and exactly one of them may mint a successor. The loser's UPDATE matches
/// no row (the row it saw live is revoked once the lock is released) and it answers 401
/// <c>session_revoked</c>, which is the truth about the old cookie rather than an internal error.
/// </summary>
/// <remarks>
/// The invariant asserted here is timing-independent: whatever the interleaving, the old row can be revoked
/// once, so exactly one successor exists and exactly one caller succeeds. The row lock is taken by the test
/// so both statements are known to be queued on it — that is what makes the two rotations overlap instead of
/// happening to run one after the other.
/// </remarks>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class RotationConcurrencyTests : SessionHardeningTestBase
{
    public RotationConcurrencyTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>Two rotations of one session: one successor, one winner, one <c>session_revoked</c>.</summary>
    [Fact]
    public async Task Two_Concurrent_Rotations_Of_One_Session_Produce_Exactly_One_Successor()
    {
        var userId = await SeedUserAsync("rotate-race@example.com");
        var clock = ClockAt();
        var predecessor = await CreateSessionAsync(userId, clock);
        var store = CreateStore(clock);

        await using var holder = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await holder.OpenAsync(Token);
        await using var held = await holder.BeginTransactionAsync(Token);

        await using (var lockRow = new NpgsqlCommand(
            "select id from public.session where id = @id for update",
            holder,
            held))
        {
            lockRow.Parameters.AddWithValue("id", predecessor.SessionId);
            (await lockRow.ExecuteScalarAsync(Token)).ShouldBe(predecessor.SessionId);
        }

        var first = store.RotateAsync(new SessionRotation(predecessor.SessionId, null, null), Token).AsTask();
        var second = store.RotateAsync(new SessionRotation(predecessor.SessionId, null, null), Token).AsTask();

        // Both statements are waiting on the row lock: neither may have produced an answer yet.
        await Task.Delay(TimeSpan.FromMilliseconds(200), Token);
        first.IsCompleted.ShouldBeFalse("a rotation waits on the predecessor's row lock");
        second.IsCompleted.ShouldBeFalse("a rotation waits on the predecessor's row lock");

        await held.CommitAsync(Token);

        var results = await Task.WhenAll(first, second);

        results.Count(result => result.IsSuccess).ShouldBe(1, "exactly one rotation wins");

        var loser = results.Single(result => !result.IsSuccess);
        loser.Error!.Code.ShouldBe(ApiErrorCode.SessionRevoked);
        loser.Error.Status.ShouldBe(401);
        loser.Error.Retryable.ShouldBeFalse();

        (await OwnerScalarAsync<long>(
            "select count(*) from public.session where rotated_from = @id",
            ("id", predecessor.SessionId))).ShouldBe(1, "the insert is gated by the update, so one successor exists");

        (await OwnerScalarAsync<bool>(
            "select revoked_at is not null from public.session where id = @id",
            ("id", predecessor.SessionId))).ShouldBeTrue("the predecessor is revoked exactly once");

        // Both live sessions a browser would hold: the winner's successor only — the old cookie is dead.
        (await OwnerScalarAsync<long>(
            "select count(*) from public.session where user_id = @user and revoked_at is null",
            ("user", userId))).ShouldBe(1);
    }

    /// <summary>
    /// Without the artificial lock the same invariant must hold: two rotations issued back to back still leave
    /// one successor and one 401, never two live sessions for one browser.
    /// </summary>
    [Fact]
    public async Task Back_To_Back_Rotations_Leave_One_Successor()
    {
        var userId = await SeedUserAsync("rotate-back-to-back@example.com");
        var clock = ClockAt();
        var predecessor = await CreateSessionAsync(userId, clock);
        var store = CreateStore(clock);

        var first = store.RotateAsync(new SessionRotation(predecessor.SessionId, null, null), Token).AsTask();
        var second = store.RotateAsync(new SessionRotation(predecessor.SessionId, null, null), Token).AsTask();
        var results = await Task.WhenAll(first, second);

        results.Count(result => result.IsSuccess).ShouldBe(1);
        results.Single(result => !result.IsSuccess).Error!.Code.ShouldBe(ApiErrorCode.SessionRevoked);

        (await OwnerScalarAsync<long>(
            "select count(*) from public.session where rotated_from = @id",
            ("id", predecessor.SessionId))).ShouldBe(1);
    }
}
