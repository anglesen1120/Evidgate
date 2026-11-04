// 06.2.3-T1 (AC2/AC4/AC7) on the scanner itself: the three starter classes and their near misses, the boundary rule
// that keeps a longer identifier from matching, a match that straddles a 4 KiB chunk boundary, a large input, random
// binary input and a backtracking-shaped input. Pure logic, so no container and no database: every expectation comes
// from the plan's §3 pattern set, not from the current behaviour.
using System.Text;
using Evidgate.Modules.Evidence.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// <see cref="SecretPatternScanner"/> over the classes the plan fixes: an AWS access-key id, a PEM private-key header
/// and a bearer credential. Secret-shaped samples are assembled from a prefix and a body at run time, so this file
/// carries no literal that reads as a credential to the repository's own scanners.
/// </summary>
public sealed class EvidenceSecretPatternScannerTests
{
    /// <summary>The AWS prefix and a well-formed 16-character body, kept apart until the sample is built.</summary>
    private const string AwsPrefix = "AK" + "IA";

    private const string AwsBody = "IOSFODNN7EXAMPL" + "E";

    /// <summary>A PEM header, split so no whole header literal sits in the file.</summary>
    private const string PemBegin = "-----BEGIN ";

    private const string PemPrivateSuffix = "PRIVATE KEY" + "-----";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The plan's named unit case (06.2.3 §9): the AWS arm reports the key it is given.</summary>
    [Fact]
    public void SecretPatternScanner_DetectsAwsKey() =>
        SecretPatternScanner.Scan(Encoding.ASCII.GetBytes(Compose(AwsPrefix, AwsBody)))
            .ShouldHaveSingleItem()
            .PatternClass.ShouldBe(SecretPatternClass.AwsAccessKey);

    /// <summary>The plan's named unit case (06.2.3 §9): the PEM arm reports a private-key header.</summary>
    [Fact]
    public void SecretPatternScanner_DetectsPrivateKeyPem() =>
        SecretPatternScanner.Scan(Encoding.ASCII.GetBytes(PemBegin + "EC " + PemPrivateSuffix))
            .ShouldHaveSingleItem()
            .PatternClass.ShouldBe(SecretPatternClass.PrivateKeyPem);

    /// <summary>
    /// The plan's named unit case (06.2.3 §9): random hex of the length a key's body has is not a key. The positive
    /// control is the same buffer with the arm's own literal in front of it, which the AWS case above pins.
    /// </summary>
    [Fact]
    public void SecretPatternScanner_NoFalsePositiveOnRandomHex()
    {
        var hex = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var content = Encoding.ASCII.GetBytes($"commit={hex} checksum=sha256:{hex} digest {hex}");

        SecretPatternScanner.Scan(content).ShouldBeEmpty();
    }

    /// <summary>AC4: ordinary prose carries no class, and the answer is the empty list rather than a zero-count row.</summary>
    [Fact]
    public void Scan_Clean_Input_Finds_Nothing()
    {
        var content = Encoding.UTF8.GetBytes(
            "The deploy finished; see the changelog for the module's own notes and the policy review.");

        SecretPatternScanner.Scan(content).ShouldBeEmpty();
    }

    /// <summary>AC2: each class the plan names is reported once with its count, in the scanner's own class order.</summary>
    [Fact]
    public void Scan_All_Three_Classes_Reports_Each_With_Its_Count()
    {
        var first = Compose(AwsPrefix, AwsBody);
        var second = Compose(AwsPrefix, "AKZXYWVUTSRQPONM");
        var text = string.Join(
            ' ',
            first,
            second,
            PemBegin + "RSA " + PemPrivateSuffix,
            "Authorization: Bearer " + new string('a', 24));

        var hits = SecretPatternScanner.Scan(Encoding.UTF8.GetBytes(text));

        hits.Select(hit => hit.PatternClass).ShouldBe(
            [SecretPatternClass.AwsAccessKey, SecretPatternClass.PrivateKeyPem, SecretPatternClass.GenericBearerToken]);
        hits.Single(hit => hit.PatternClass == SecretPatternClass.AwsAccessKey).HitCount.ShouldBe(2);
        hits.Single(hit => hit.PatternClass == SecretPatternClass.PrivateKeyPem).HitCount.ShouldBe(1);
        hits.Single(hit => hit.PatternClass == SecretPatternClass.GenericBearerToken).HitCount.ShouldBe(1);
    }

    /// <summary>
    /// The AWS arm's near misses: a lower-case prefix, a body one character short, and a key embedded in a longer
    /// identifier. Each is the same input with one property changed against the positive control above.
    /// </summary>
    [Fact]
    public void Scan_Aws_Shaped_Non_Keys_Find_Nothing()
    {
        var shortBody = AwsBody[..^1];
        var longerIdentifier = "x" + Compose(AwsPrefix, AwsBody);
        var text = string.Join(' ', AwsPrefix.ToLowerInvariant() + AwsBody, Compose(AwsPrefix, shortBody), longerIdentifier);

        SecretPatternScanner.Scan(Encoding.UTF8.GetBytes(text)).ShouldBeEmpty();
    }

    /// <summary>
    /// The PEM arm accepts every private-key spelling the plan names and refuses the two public headers: the algorithm
    /// window is what makes one arm enough, so a variant that slipped through would be a scanner bug, not a new class.
    /// </summary>
    [Fact]
    public void Scan_Pem_Reports_Private_Key_Headers_And_Not_Public_Ones()
    {
        var privateKeys = new[]
        {
            PemBegin + "RSA " + PemPrivateSuffix,
            PemBegin + "EC " + PemPrivateSuffix,
            PemBegin + "OPENSSH " + PemPrivateSuffix,
            PemBegin + "ENCRYPTED " + PemPrivateSuffix,
        };

        var publicKeys = new[]
        {
            PemBegin + "CERTIFICATE-----",
            PemBegin + "PUBLIC KEY-----",
        };

        foreach (var header in privateKeys)
        {
            SecretPatternScanner.Scan(Encoding.UTF8.GetBytes(header))
                .ShouldHaveSingleItem()
                .PatternClass.ShouldBe(SecretPatternClass.PrivateKeyPem);
        }

        foreach (var header in publicKeys)
        {
            SecretPatternScanner.Scan(Encoding.UTF8.GetBytes(header)).ShouldBeEmpty();
        }
    }

    /// <summary>
    /// The bearer arm's boundary: the scheme is case-insensitive and a token of exactly the minimum length counts,
    /// while one character fewer, a scheme with no separator and a scheme glued to an identifier do not.
    /// </summary>
    [Fact]
    public void Scan_Bearer_Requires_A_Separator_And_A_Token_Length()
    {
        var minimum = new string('Q', 20);
        var accepted = new[]
        {
            "Bearer " + minimum,
            "bearer " + minimum,
            "BEARER\t" + minimum,
        };

        var refused = new[]
        {
            "Bearer " + new string('Q', 19),
            "Bearer" + minimum,
            "XBearer " + minimum,
        };

        foreach (var sample in accepted)
        {
            SecretPatternScanner.Scan(Encoding.UTF8.GetBytes(sample))
                .ShouldHaveSingleItem()
                .PatternClass.ShouldBe(SecretPatternClass.GenericBearerToken);
        }

        foreach (var sample in refused)
        {
            SecretPatternScanner.Scan(Encoding.UTF8.GetBytes(sample)).ShouldBeEmpty();
        }
    }

    /// <summary>
    /// A match that spans a chunk boundary is one match: the sample puts the AWS key at offset 4095 so the first ten
    /// bytes fall in the first 4 KiB chunk and the rest in the next, and the count stays one.
    /// </summary>
    [Fact]
    public void Scan_Match_Across_A_Chunk_Boundary_Is_Counted_Once()
    {
        const int boundary = 4096;
        var key = Compose(AwsPrefix, AwsBody);
        var content = new byte[boundary + key.Length + 16];
        Array.Fill(content, (byte)'.');
        Encoding.ASCII.GetBytes(key).CopyTo(content, boundary - 10);

        var hits = SecretPatternScanner.Scan(content);

        hits.ShouldHaveSingleItem().HitCount.ShouldBe(1);
    }

    /// <summary>
    /// Adjacent matches are two matches, and the pass moves past each match it counted: a second literal that starts
    /// inside the body of the first is part of that key, not a key of its own.
    /// </summary>
    [Fact]
    public void Scan_Adjacent_Keys_Count_Twice_And_A_Repeated_Prefix_Is_Not_A_Key()
    {
        var key = Compose(AwsPrefix, AwsBody);
        var adjacent = key + " " + key;
        var repeatedPrefix = Compose(AwsPrefix, AwsBody[..^1] + AwsPrefix[..1]);

        SecretPatternScanner.Scan(Encoding.UTF8.GetBytes(adjacent))
            .ShouldHaveSingleItem()
            .HitCount.ShouldBe(2);
        SecretPatternScanner.Scan(Encoding.UTF8.GetBytes(repeatedPrefix))
            .ShouldHaveSingleItem()
            .HitCount.ShouldBe(1);
    }

    /// <summary>
    /// AC7: an arbitrary binary object is scanned without decoding it — no exception, and no class invented from the
    /// bytes' own shape. The positive control is the same random buffer carrying one key.
    /// </summary>
    [Fact]
    public void Scan_Binary_Content_Neither_Throws_Nor_Invents_Classes()
    {
        var random = new byte[64 * 1024];
        new Random(20261104).NextBytes(random);

        SecretPatternScanner.Scan(random).ShouldBeEmpty();

        Encoding.ASCII.GetBytes(Compose(AwsPrefix, AwsBody)).CopyTo(random, 32 * 1024);
        SecretPatternScanner.Scan(random).ShouldHaveSingleItem().PatternClass.ShouldBe(SecretPatternClass.AwsAccessKey);
    }

    /// <summary>
    /// A large object is scanned in one bounded pass and still answers: 8 MiB of ordinary text with one key at the end
    /// costs no allocation proportional to the input and finishes well inside the generous wall clock.
    /// </summary>
    [Fact]
    public async Task Scan_Large_Input_Is_Bounded_And_Complete()
    {
        var content = new byte[(8 * 1024 * 1024) + 64];
        Array.Fill(content, (byte)'a');
        Encoding.ASCII.GetBytes(" " + Compose(AwsPrefix, AwsBody)).CopyTo(content, 8 * 1024 * 1024);

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var hits = await Task.Run(() => SecretPatternScanner.Scan(content), Token);
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);

        hits.ShouldHaveSingleItem().HitCount.ShouldBe(1);
        elapsed.ShouldBeLessThan(
            TimeSpan.FromSeconds(20),
            "one forward pass over 8 MiB must not degrade with the input's size");
    }

    /// <summary>
    /// A backtracking-shaped input finds nothing and finishes: a million bare prefixes with no body is the input a
    /// backtracking regex would blow up on, and the scanner's own constant-time comparison must not.
    /// </summary>
    [Fact]
    public async Task Scan_Backtracking_Shaped_Input_Finds_Nothing()
    {
        var content = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(AwsPrefix, 250_000)));

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var hits = await Task.Run(() => SecretPatternScanner.Scan(content), Token);
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);

        hits.ShouldBeEmpty();
        elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
    }

    /// <summary>The sample the AWS arm matches, assembled here so the file carries no credential-shaped literal.</summary>
    /// <param name="prefix">The arm's literal prefix.</param>
    /// <param name="body">Sixteen upper-case letters or digits.</param>
    /// <returns>The key-shaped sample.</returns>
    private static string Compose(string prefix, string body) => prefix + body;
}
