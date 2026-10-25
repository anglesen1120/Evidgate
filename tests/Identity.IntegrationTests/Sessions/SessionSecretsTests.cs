using System.Security.Cryptography;
using System.Text;
using Evidgate.Modules.Identity.Sessions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions;

/// <summary>
/// 02.3.2-T1 (D4) at the seam the cookie path depends on: <see cref="SessionSecrets.Candidates"/> answers
/// LOOKUP values — HMACs of the presented cookie value — never the pepper itself, with the current pepper
/// first and the previous one behind it while a rotation window is open.
/// </summary>
/// <remarks>
/// No database is touched, but the class carries the Docker trait like every other class of this project: the
/// assembly fixture needs the container whether or not one test of the class reaches it. The single-pepper case
/// is pinned here rather than only through <c>session_authenticate</c>: returning the key instead of its HMAC
/// made every lookup miss on a deployment that never rotated (so no live session authenticated and no forwarded
/// cookie was recognised as a predecessor) and passed key material into the statement's parameter.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class SessionSecretsTests
{
    private static readonly byte[] CurrentKey = Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();
    private static readonly byte[] PreviousKey = Enumerable.Range(0, 32).Select(i => (byte)(200 - i)).ToArray();

    /// <summary>The cookie value a browser would present; only its ASCII bytes reach the HMAC.</summary>
    private static readonly byte[] CookieValue = Encoding.ASCII.GetBytes("s85-cookie-value-under-test");

    /// <summary>
    /// The never-rotated deployment: exactly one candidate, the CURRENT version, and it is the HMAC of the value
    /// under the pepper — not the pepper (nor the cookie value) itself.
    /// </summary>
    [Fact]
    public void Without_A_Previous_Pepper_The_Single_Candidate_Is_The_Current_Hmac_And_Not_The_Key()
    {
        var secrets = Build(CurrentKey, keyVersion: 7, previous: null, previousVersion: null);

        var candidates = secrets.Candidates(CookieValue);

        var candidate = candidates.ShouldHaveSingleItem();
        candidate.Version.ShouldBe(7);
        candidate.Hash.ShouldBe(HMACSHA256.HashData(CurrentKey, CookieValue));
        candidate.Hash.ShouldNotBe(CurrentKey, "the pepper is key material, never a lookup value");
        candidate.Hash.ShouldNotBe(CookieValue);

        // One construction for both callers: the row's stored hash and the lookup candidate cannot drift.
        // (Compared member by member: a tuple of byte[] is compared by reference, never by content.)
        var stored = secrets.HashCurrent(CookieValue);
        stored.Version.ShouldBe(candidate.Version);
        stored.Hash.ShouldBe(candidate.Hash);
    }

    /// <summary>With a rotation window open the lookup tries the current pepper first and the previous one behind it.</summary>
    [Fact]
    public void During_A_Rotation_Window_The_Candidates_Are_Current_Then_Previous()
    {
        var secrets = Build(CurrentKey, keyVersion: 7, previous: PreviousKey, previousVersion: 6);

        var candidates = secrets.Candidates(CookieValue);

        candidates.Count.ShouldBe(2);
        candidates[0].Version.ShouldBe(7);
        candidates[0].Hash.ShouldBe(HMACSHA256.HashData(CurrentKey, CookieValue));
        candidates[1].Version.ShouldBe(6);
        candidates[1].Hash.ShouldBe(HMACSHA256.HashData(PreviousKey, CookieValue));
        candidates[0].Hash.ShouldNotBe(candidates[1].Hash);
        candidates.ShouldNotContain(c => c.Hash.SequenceEqual(CurrentKey) || c.Hash.SequenceEqual(PreviousKey));
    }

    /// <summary>An unprovisioned deployment offers no candidate at all; the request path answers 503 on <see cref="SessionSecrets.CanHash"/>.</summary>
    [Fact]
    public void Without_A_Pepper_There_Is_No_Candidate_And_Nothing_To_Hash_With()
    {
        var secrets = Build(current: null, keyVersion: 1, previous: null, previousVersion: null);

        secrets.CanHash.ShouldBeFalse();
        secrets.Candidates(CookieValue).ShouldBeEmpty();
        Should.Throw<InvalidOperationException>(() => secrets.HashCurrent(CookieValue));
    }

    private static SessionSecrets Build(byte[]? current, int keyVersion, byte[]? previous, int? previousVersion)
    {
        var options = new SessionOptions
        {
            Pepper = current is null ? null : Convert.ToBase64String(current),
            PepperVersion = keyVersion,
            PreviousPepper = previous is null ? null : Convert.ToBase64String(previous),
            PreviousPepperVersion = previousVersion,
        };

        return new SessionSecrets(Options.Create(options));
    }
}
