using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Sessions;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Store;

/// <summary>
/// 02.3.2-T1.1 D4 at the store level: the pepper rotation window. While the previous pepper is configured a
/// session signed with it still authenticates and is re-hashed to the current pepper in the same statement;
/// once the window closes the same cookie is unknown, which is the retirement step's whole point.
/// </summary>
[Trait("Category", "Docker")]
public sealed class PepperRotationTests : SessionHardeningTestBase
{
    /// <summary>The pepper that becomes current when the suite's pepper is retired.</summary>
    private static readonly byte[] NextPepper =
        Enumerable.Range(50, 32).Select(index => (byte)index).ToArray();

    private const int NextVersion = 8;

    private static string NextPepperBase64 => Convert.ToBase64String(NextPepper);

    /// <summary>Shares the run's database: every read is the row the case minted.</summary>
    public PepperRotationTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// D4: inside the window the session authenticates through the previous pepper's hash and the row is
    /// re-hashed to the current pair — one request, no forced re-login, no sweep.
    /// </summary>
    [Fact]
    public async Task A_Previous_Pepper_Session_Verifies_And_Is_Rehashed_To_The_Current_Pepper()
    {
        var userId = await SeedUserAsync(Unique.Email("rotation-window"));
        var clock = ClockAt();
        var session = await CreateSessionAsync(userId, clock);

        clock.Advance(TimeSpan.FromMinutes(10));

        var result = await AuthenticateAsync(
            session.CookieValue,
            clock,
            pepper: NextPepperBase64,
            version: NextVersion,
            previous: PepperBase64,
            previousVersion: CurrentVersion);

        result.IsSuccess.ShouldBeTrue(result.Error?.Code ?? "the retired pepper still verifies its sessions");
        result.Value.Status.ShouldBe(SessionAuthStatus.Active);
        result.Value.SessionId.ShouldBe(session.SessionId);

        var row = await OwnerRowAsync(
            "select cookie_hmac = @hash, pepper_version = @version from public.session where id = @id",
            ("id", session.SessionId),
            ("hash", HmacOf(NextPepper, session.CookieValue)),
            ("version", NextVersion));

        row[0].ShouldBe(true, "the row is re-hashed to the current pepper");
        row[1].ShouldBe(true, "the version moves with the hash");
    }

    /// <summary>
    /// D4, retirement: with the previous pepper gone the same cookie is unknown — the old value is dead and
    /// nothing can resurrect it.
    /// </summary>
    [Fact]
    public async Task Removing_The_Previous_Pepper_Makes_The_Old_Cookie_Unknown()
    {
        var userId = await SeedUserAsync(Unique.Email("rotation-retired"));
        var clock = ClockAt();
        var session = await CreateSessionAsync(userId, clock);

        var retired = await AuthenticateAsync(
            session.CookieValue,
            clock,
            pepper: NextPepperBase64,
            version: NextVersion);

        retired.IsSuccess.ShouldBeTrue();
        retired.Value.Status.ShouldBe(SessionAuthStatus.Unknown);
        retired.Value.SessionId.ShouldBe(Guid.Empty);

        // The row still exists and is still live — only the credential no longer matches.
        (await OwnerScalarAsync<bool>(
            "select revoked_at is null and pepper_version = @version from public.session where id = @id",
            ("id", session.SessionId),
            ("version", CurrentVersion)))
            .ShouldBeTrue();
    }

    /// <summary>D4: a dead row is never re-hashed — a revoked session stays on the pepper it was signed with.</summary>
    [Fact]
    public async Task A_Revoked_Session_Is_Not_Rehashed()
    {
        var userId = await SeedUserAsync(Unique.Email("rotation-revoked"));
        var clock = ClockAt();
        var session = await CreateSessionAsync(userId, clock);
        await OwnerExecuteAsync(
            "update public.session set revoked_at = @now where id = @id",
            ("id", session.SessionId),
            ("now", clock.UtcNow));

        var result = await AuthenticateAsync(
            session.CookieValue,
            clock,
            pepper: NextPepperBase64,
            version: NextVersion,
            previous: PepperBase64,
            previousVersion: CurrentVersion);

        result.Value.Status.ShouldBe(SessionAuthStatus.Revoked);

        var row = await OwnerRowAsync(
            "select cookie_hmac = @hash, pepper_version = @version from public.session where id = @id",
            ("id", session.SessionId),
            ("hash", HmacOf(Pepper, session.CookieValue)),
            ("version", CurrentVersion));

        row[0].ShouldBe(true, "the stored hash is still the retired pepper's");
        row[1].ShouldBe(true);
    }

    /// <summary>
    /// The candidate list itself (no database): current hash first, previous second, so the statement's index
    /// probe sees the current pepper's hash first and the caller's ordering is deterministic.
    /// </summary>
    [Fact]
    public void Candidates_Are_Current_Then_Previous()
    {
        var options = OptionsFor(
            NextPepperBase64,
            NextVersion,
            previous: PepperBase64,
            previousVersion: CurrentVersion);
        var secrets = new SessionSecrets(options);
        var value = "a-cookie-value-we-never-minted";

        secrets.CanHash.ShouldBeTrue();

        var candidates = secrets.Candidates(System.Text.Encoding.ASCII.GetBytes(value));

        candidates.Count.ShouldBe(2);
        candidates[0].Version.ShouldBe(NextVersion);
        candidates[0].Hash.ShouldBe(HmacOf(NextPepper, value));
        candidates[1].Version.ShouldBe(CurrentVersion);
        candidates[1].Hash.ShouldBe(HmacOf(Pepper, value));

        new SessionSecrets(OptionsFor(NextPepperBase64, NextVersion))
            .Candidates(System.Text.Encoding.ASCII.GetBytes(value))
            .Count.ShouldBe(1, "with no rotation window there is exactly one candidate");

        var unprovisioned = new SessionSecrets(OptionsFor(null));
        unprovisioned.CanHash.ShouldBeFalse();
        unprovisioned.Candidates(System.Text.Encoding.ASCII.GetBytes(value)).ShouldBeEmpty();
    }
}
