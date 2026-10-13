// 06.3.2-T2.1 (AC4; plan §9 "Nightly fuzz budget finds no unreported crash" and "A seeded crash is promoted
// into the corpus, not swallowed"): the seeded-mutation fuzz harness over the 06.3.1 parser profile, plus the
// promotion helper that writes a finding into fixtures/hostile/ when an operator asks for it.
//
// [DECISION] (06.3.2-T2.1) The harness is a SEEDED MUTATION FUZZER and adds no dependency, instead of SharpFuzz.
// SharpFuzz is only coverage-guided together with the AFL/libFuzzer native tooling and an IL-instrumented copy of
// the parser assembly (`sharpfuzz` rewriting Evidgate.Modules.Evidence.dll) in the CI image — a native toolchain
// and a rewritten build artifact the pipeline does not have — and any new dependency needs a licence-scan pass
// and an ADR-0002 row (00 §5.7 S6). scripts/licence-policy.json has no SharpFuzz entry and
// docs/adr/0002-compatibility-and-licence-matrix.md has no SharpFuzz row (both grepped: zero hits), so the
// "or a seeded mutation fuzzer if the licence check fails" arm of the 06.3.2-T2.1 line applies.
//
// The fuzzer mutates the committed corpus bytes in-process under a recorded seed, drives every mutated input
// through IParsing.ParseXmlReport and IParsing.EnumerateArchive, and asserts the profile's DOCUMENTED outcomes
// only (a success, or a failure carrying one of ParsingErrorCode with the closed ParsingFailureReason set). Any
// other outcome — an escaped exception, an input the per-input bound abandons, a failure whose code, status or
// reason the profile never documents — is a finding: its bytes are written to the findings directory and the run
// fails. Mutated bytes are never executed, extracted to disk or written into the repository unless the promotion
// helper is explicitly asked to (EVIDGATE_FUZZ_PROMOTE=1).
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;

namespace Tenancy.IntegrationTests;

/// <summary>The two entry points the harness fuzzes (06.3.2-T2.1): the XML report and the archive walk.</summary>
internal enum FuzzTarget
{
    /// <summary><see cref="IParsing.ParseXmlReport"/>.</summary>
    XmlReport,

    /// <summary><see cref="IParsing.EnumerateArchive"/>.</summary>
    Archive,
}

/// <summary>One corpus file the harness seeds from (name for the report, bytes for the mutation).</summary>
/// <param name="Name">The manifest file name the seed came from.</param>
/// <param name="Bytes">The seed bytes.</param>
internal sealed record FuzzSeed(string Name, byte[] Bytes);

/// <summary>
/// One input the profile answered outside its documented outcomes, with the bytes that caused it. Written to
/// the findings directory (never into the repository) and, only when asked, promoted into the corpus.
/// </summary>
/// <param name="SeedName">The corpus file the input was mutated from.</param>
/// <param name="Target">The entry point that answered unexpectedly.</param>
/// <param name="Iteration">The zero-based iteration that produced the input.</param>
/// <param name="Detail">What was observed instead (an exception type and message, an abandoned input, a failure shape).</param>
/// <param name="Bytes">The offending bytes.</param>
internal sealed record FuzzFinding(string SeedName, FuzzTarget Target, long Iteration, string Detail, byte[] Bytes);

/// <summary>The outcome of one fuzz run: the seed, the counts, and every finding (empty when the run is clean).</summary>
/// <param name="Seed">The recorded PRNG seed, so any run can be replayed.</param>
/// <param name="SeedCount">How many corpus seeds were used.</param>
/// <param name="Iterations">How many mutated inputs were driven through the profile.</param>
/// <param name="Elapsed">Wall-clock time the run took.</param>
/// <param name="Findings">Every finding, in discovery order.</param>
internal sealed record FuzzRunResult(
    ulong Seed,
    int SeedCount,
    long Iterations,
    TimeSpan Elapsed,
    IReadOnlyList<FuzzFinding> Findings)
{
    /// <summary>True when no input answered outside the documented outcomes.</summary>
    internal bool IsClean => Findings.Count == 0;
}

/// <summary>
/// The run's bounds, read from the environment so the same class serves the few-second smoke (<c>test</c> and
/// <c>fixtures-hostile</c> stages) and the <c>fixtures-fuzz</c> stage's 10-minute nightly budget. Both a
/// wall-clock budget and an iteration cap are enforced; the seed is always recorded.
/// </summary>
/// <param name="Seed">The deterministic PRNG seed.</param>
/// <param name="BudgetSeconds">The wall-clock budget.</param>
/// <param name="MaxIterations">The iteration cap.</param>
/// <param name="PerInputMilliseconds">How long one input may take before it is abandoned as a finding.</param>
/// <param name="FindingsDirectory">Where a finding's bytes are written.</param>
internal sealed record FuzzRunOptions(
    ulong Seed,
    int BudgetSeconds,
    long MaxIterations,
    int PerInputMilliseconds,
    string FindingsDirectory)
{
    /// <summary>Overrides the PRNG seed.</summary>
    internal const string SeedVariable = "EVIDGATE_FUZZ_SEED";

    /// <summary>Overrides the wall-clock budget in seconds.</summary>
    internal const string BudgetVariable = "EVIDGATE_FUZZ_BUDGET_SECONDS";

    /// <summary>Overrides the iteration cap.</summary>
    internal const string IterationsVariable = "EVIDGATE_FUZZ_MAX_ITERATIONS";

    /// <summary>Overrides the per-input bound in milliseconds.</summary>
    internal const string PerInputVariable = "EVIDGATE_FUZZ_PER_INPUT_MS";

    /// <summary>Overrides where findings are written.</summary>
    internal const string FindingsDirectoryVariable = "EVIDGATE_FUZZ_FINDINGS_DIR";

    /// <summary>The constant default seed, so a run with no environment is exactly reproducible.</summary>
    internal const ulong DefaultSeed = 20_261_013;

    /// <summary>The default budget when nothing is set: the few-second smoke the per-PR stages use.</summary>
    internal const int SmokeBudgetSeconds = 5;

    /// <summary>
    /// The default iteration cap: measured at ~110k inputs/s, the 600 s nightly budget ends a run long before
    /// this, so the clock is the bound the stages rely on and this only backstops a runaway budget value.
    /// </summary>
    internal const long DefaultMaxIterations = 100_000_000;

    /// <summary>The default per-input bound: far above any documented parse, where a breach is a runaway.</summary>
    internal const int DefaultPerInputMilliseconds = 5_000;

    /// <summary>The bounds for this process: every value from its variable, or the documented default.</summary>
    /// <returns>The options the harness runs with.</returns>
    internal static FuzzRunOptions FromEnvironment() => new(
        Seed: Parse(SeedVariable, DefaultSeed),
        BudgetSeconds: Parse(BudgetVariable, SmokeBudgetSeconds),
        MaxIterations: Parse(IterationsVariable, DefaultMaxIterations),
        PerInputMilliseconds: Parse(PerInputVariable, DefaultPerInputMilliseconds),
        FindingsDirectory: Environment.GetEnvironmentVariable(FindingsDirectoryVariable) is { Length: > 0 } directory
            ? directory
            : Path.Combine(Path.GetTempPath(), "evidgate-fuzz-findings"));

    private static ulong Parse(string name, ulong fallback) =>
        ulong.TryParse(Environment.GetEnvironmentVariable(name), CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static int Parse(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static long Parse(string name, long fallback) =>
        long.TryParse(Environment.GetEnvironmentVariable(name), CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}

/// <summary>
/// The seeded-mutation fuzzer itself: seeds from <c>fixtures/hostile/</c>, mutates them with a deterministic
/// PRNG, drives every input through both <see cref="IParsing"/> entry points, and classifies the answer against
/// the documented outcome set. No network, no disk write beyond the findings directory, no extraction.
/// </summary>
internal static class HostileFuzz
{
    /// <summary>The largest input a mutation may produce; the kind cap is 8 MiB, so this is not the size guard.</summary>
    internal const int MaxMutatedBytes = 1024 * 1024;

    private const int MaxMutationsPerInput = 3;
    private const int MaxRecordedFindings = 50;
    private const int MaxBurstBytes = 16;
    private const int MaxReplacedBytes = 8;
    private const int MaxDuplicatedChunk = 4096;
    private const int DeepNestingElements = 200;
    private const int EndOfCentralDirectoryLength = 22;
    private const int MaxCommentLength = 0xFFFF;

    /// <summary>Every corpus seed the harness mutates: the committed files, plus the cheap generated object.</summary>
    /// <returns>The seeds, generated half included where it is cheap.</returns>
    internal static IReadOnlyList<FuzzSeed> LoadSeeds()
    {
        var seeds = new List<FuzzSeed>();

        foreach (var entry in HostileFixtureCorpus.All)
        {
            // zipbomb.zip is generated with ParsingLimits.MaxZipEntries + 500 entries: building it per run is
            // cheap, but a mutated copy costs a central-directory walk over ~10k entries, which has no bound well
            // under the per-input limit. It is covered by the corpus test and the mutation smoke instead.
            var bytes = entry.Generated
                ? Generated(entry.File)
                : File.ReadAllBytes(HostileFixtureCorpus.PathOf(entry));

            if (bytes is not null)
            {
                seeds.Add(new FuzzSeed(entry.File, bytes));
            }
        }

        return seeds;
    }

    /// <summary>
    /// Runs the harness for the given bounds. Every mutated input is classified against the documented outcome
    /// set; an input that answers anything else becomes a finding, its bytes are written to
    /// <see cref="FuzzRunOptions.FindingsDirectory"/>, and — only when the promotion variable is set — it is also
    /// promoted into the corpus.
    /// </summary>
    /// <param name="options">The bounds and the recorded seed.</param>
    /// <param name="cancellationToken">Stops the run (a caller cancellation is never a finding).</param>
    /// <param name="log">An optional sink for the run's own progress.</param>
    /// <returns>What the run did and what it found.</returns>
    internal static FuzzRunResult Run(FuzzRunOptions options, CancellationToken cancellationToken, Action<string>? log = null)
    {
        var seeds = LoadSeeds();
        var reasons = ClosedReasons();
        var parsing = new ParsingService();
        var findings = new List<FuzzFinding>();
        var recorded = new HashSet<string>(StringComparer.Ordinal);
        var random = new FuzzRandom(options.Seed);
        var budget = TimeSpan.FromSeconds(options.BudgetSeconds);
        var stopwatch = Stopwatch.StartNew();

        long iteration = 0;

        while (iteration < options.MaxIterations
            && stopwatch.Elapsed < budget
            && findings.Count < MaxRecordedFindings)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var seed = seeds[random.NextInt(seeds.Count)];
            var partner = seeds[random.NextInt(seeds.Count)].Bytes;
            var input = Mutate(seed.Bytes, partner, ref random);
            var target = random.NextInt(2) == 0 ? FuzzTarget.XmlReport : FuzzTarget.Archive;

            var detail = Drive(parsing, reasons, target, input, options.PerInputMilliseconds, cancellationToken);

            if (detail is not null && recorded.Add($"{target}:{Sha256Hex(input)}"))
            {
                var finding = new FuzzFinding(seed.Name, target, iteration, detail, input);

                findings.Add(finding);
                FuzzFindings.Write(options.FindingsDirectory, finding);
                log?.Invoke($"fuzz finding: {finding.SeedName} [{finding.Target}] #{finding.Iteration}: {finding.Detail}");

                if (FuzzPromotion.Enabled)
                {
                    // The row's expected code is the conservative documented refusal; the operator reviewing a
                    // real crash tightens it to the reason the fixed profile returns (the corpus test then proves
                    // the row and fails if it is still wrong).
                    var promoted = FuzzPromotion.Promote(finding, HostileFixtureCorpus.DirectoryPath, ParsingFailureReason.Malformed, allowPromotion: true);
                    log?.Invoke($"fuzz promoted: {promoted.File} ({promoted.GuardId})");
                }
            }

            iteration++;
        }

        stopwatch.Stop();
        FuzzFindings.WriteRun(options.FindingsDirectory, options, seeds.Count, iteration, stopwatch.Elapsed, findings.Count);

        return new FuzzRunResult(options.Seed, seeds.Count, iteration, stopwatch.Elapsed, findings);
    }

    /// <summary>
    /// Drives one input through one entry point and returns null when the answer is documented, or a sentence
    /// describing the undocumented outcome. The caller's token cancels the whole run; the per-input bound
    /// abandons an input on its own and is a finding, because the profile's caps are what bound a parse.
    /// </summary>
    private static string? Drive(
        ParsingService parsing,
        IReadOnlySet<string> reasons,
        FuzzTarget target,
        byte[] bytes,
        int perInputMilliseconds,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(perInputMilliseconds);

        try
        {
            using var content = new MemoryStream(bytes, writable: false);

            return target == FuzzTarget.XmlReport
                ? Classify(parsing.ParseXmlReport(content, HostileFixtureCorpus.MaxBytes, timeoutSource.Token), reasons)
                : Classify(parsing.EnumerateArchive(content, HostileFixtureCorpus.MaxBytes, timeoutSource.Token), reasons);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return $"no outcome within {perInputMilliseconds} ms";
        }
        catch (Exception exception)
        {
            return $"{exception.GetType().Name}: {exception.Message}";
        }
    }

    /// <summary>
    /// Classifies one answered <see cref="Result{T}"/>: null for a success or a refusal the profile documents
    /// (one <c>content</c> detail whose reason is in the closed <see cref="ParsingFailureReason"/> set, under the
    /// code and status that reason is documented with), otherwise a sentence naming the breach.
    /// </summary>
    private static string? Classify<T>(Result<T> result, IReadOnlySet<string> reasons)
    {
        if (result.IsSuccess)
        {
            return null;
        }

        var error = result.Error;

        if (error is null)
        {
            return "a failure without an error";
        }

        if (error.Details is null || error.Details.Count != 1)
        {
            return $"a failure with {error.Details?.Count ?? 0} details";
        }

        var detail = error.Details[0];

        if (!string.Equals(detail.Path, "content", StringComparison.Ordinal))
        {
            return $"a failure whose detail path is '{detail.Path}'";
        }

        if (!reasons.Contains(detail.Reason))
        {
            return $"a failure with the undocumented reason '{detail.Reason}'";
        }

        var expected = string.Equals(detail.Reason, ParsingFailureReason.SizeExceeded, StringComparison.Ordinal)
            ? ParsingErrorCode.PayloadTooLarge
            : ParsingErrorCode.ArtifactInvalid;

        return string.Equals(error.Code, expected, StringComparison.Ordinal)
            ? null
            : $"reason '{detail.Reason}' answered '{error.Code}'/{error.Status} (expected '{expected}')";
    }

    /// <summary>The closed set of documented reasons, read from the constant type rather than duplicated here.</summary>
    private static HashSet<string> ClosedReasons() =>
    [
        .. typeof(ParsingFailureReason)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!),
    ];

    /// <summary>The generated half of the corpus the harness seeds from, or null for the ones it skips.</summary>
    private static byte[]? Generated(string file) =>
        string.Equals(file, GenerateOversizedFixture.FileName, StringComparison.Ordinal)
            ? GenerateOversizedFixture.Build()
            : null;

    /// <summary>One to three chained mutations of a seed, bounded to <see cref="MaxMutatedBytes"/>.</summary>
    private static byte[] Mutate(byte[] input, byte[] partner, ref FuzzRandom random)
    {
        var current = input;

        for (var step = 0; step < 1 + random.NextInt(MaxMutationsPerInput); step++)
        {
            current = Apply(random.NextInt(9), current, partner, ref random);
        }

        return current.Length > MaxMutatedBytes ? current[..MaxMutatedBytes] : current;
    }

    private static byte[] Apply(int operation, byte[] input, byte[] partner, ref FuzzRandom random) => operation switch
    {
        0 => BitFlip(input, ref random),
        1 => Insert(input, ref random),
        2 => Delete(input, ref random),
        3 => Replace(input, ref random),
        4 => DuplicateChunk(input, ref random),
        5 => Cut(input, ref random),
        6 => Splice(input, partner, ref random),
        7 => ArchiveTweak(input, ref random),
        _ => MarkupTweak(input, ref random),
    };

    private static byte[] BitFlip(byte[] input, ref FuzzRandom random)
    {
        if (input.Length == 0)
        {
            return [random.NextByte()];
        }

        var copy = (byte[])input.Clone();
        copy[random.NextInt(copy.Length)] ^= (byte)(1 << random.NextInt(8));

        return copy;
    }

    private static byte[] Insert(byte[] input, ref FuzzRandom random)
    {
        var inserted = new byte[1 + random.NextInt(MaxBurstBytes)];

        for (var index = 0; index < inserted.Length; index++)
        {
            inserted[index] = random.NextByte();
        }

        return InsertAt(input, random.NextInt(input.Length + 1), inserted);
    }

    private static byte[] Delete(byte[] input, ref FuzzRandom random)
    {
        if (input.Length == 0)
        {
            return input;
        }

        var offset = random.NextInt(input.Length);
        var length = 1 + random.NextInt(Math.Min(MaxBurstBytes, input.Length - offset));
        var result = new byte[input.Length - length];

        input.AsSpan(0, offset).CopyTo(result);
        input.AsSpan(offset + length).CopyTo(result.AsSpan(offset));

        return result;
    }

    private static byte[] Replace(byte[] input, ref FuzzRandom random)
    {
        if (input.Length == 0)
        {
            return Insert(input, ref random);
        }

        var copy = (byte[])input.Clone();
        var offset = random.NextInt(copy.Length);
        var length = 1 + random.NextInt(Math.Min(MaxReplacedBytes, copy.Length - offset));

        for (var index = 0; index < length; index++)
        {
            copy[offset + index] = random.NextByte();
        }

        return copy;
    }

    private static byte[] DuplicateChunk(byte[] input, ref FuzzRandom random)
    {
        if (input.Length == 0)
        {
            return Insert(input, ref random);
        }

        var length = Math.Min(input.Length, 1 + random.NextInt(Math.Min(input.Length, MaxDuplicatedChunk)));
        var offset = random.NextInt(input.Length - length + 1);
        var take = Math.Min(length, MaxMutatedBytes - input.Length);

        if (take <= 0)
        {
            return input;
        }

        var result = new byte[input.Length + take];

        input.CopyTo(result, 0);
        input.AsSpan(offset, take).CopyTo(result.AsSpan(input.Length));

        return result;
    }

    private static byte[] Cut(byte[] input, ref FuzzRandom random) =>
        input.Length == 0 ? input : input[..random.NextInt(input.Length)];

    private static byte[] Splice(byte[] input, byte[] partner, ref FuzzRandom random)
    {
        if (partner.Length == 0)
        {
            return input;
        }

        var head = input.Length == 0 ? 0 : random.NextInt(input.Length + 1);
        var tail = random.NextInt(partner.Length + 1);
        var length = Math.Min(head + partner.Length - tail, MaxMutatedBytes);
        var result = new byte[length];

        var kept = Math.Min(head, length);

        input.AsSpan(0, kept).CopyTo(result);
        partner.AsSpan(tail, Math.Min(partner.Length - tail, length - kept)).CopyTo(result.AsSpan(kept));

        return result;
    }

    /// <summary>
    /// A zip-aware tweak: when the input carries an end-of-central-directory record, corrupt its signature, its
    /// declared entry count, its comment length or its central-directory fields — the bytes 06.3.1-T2's pre-check
    /// reads — otherwise fall back to a generic byte poke.
    /// </summary>
    private static byte[] ArchiveTweak(byte[] input, ref FuzzRandom random)
    {
        var copy = (byte[])input.Clone();
        var record = LastEndOfCentralDirectory(copy);

        if (record < 0)
        {
            return Replace(copy, ref random);
        }

        switch (random.NextInt(6))
        {
            case 0:
                copy[record] ^= 0xFF;
                break;
            case 1:
                BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(record + 10, 2), (ushort)random.NextInt(0x1_0000));
                break;
            case 2:
                BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(record + 20, 2), (ushort)random.NextInt(0x1_0001));
                break;
            case 3:
                BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(record + 12, 4), (uint)random.NextUInt64());
                break;
            case 4:
                BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(record + 16, 4), (uint)random.NextUInt64());
                break;
            default:
                copy[random.NextInt(copy.Length)] ^= 0xFF;
                break;
        }

        return copy;
    }

    /// <summary>
    /// The last end-of-central-directory signature (<c>PK\x05\x06</c>) in the record region — the same record
    /// <c>System.IO.Compression.ZipArchive</c> picks, so a tweak corrupts the bytes the profile's pre-check reads
    /// rather than a decoy elsewhere in the file.
    /// </summary>
    private static int LastEndOfCentralDirectory(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < EndOfCentralDirectoryLength)
        {
            return -1;
        }

        var lowerBound = Math.Max(0, bytes.Length - EndOfCentralDirectoryLength - MaxCommentLength);

        for (var index = bytes.Length - EndOfCentralDirectoryLength; index >= lowerBound; index--)
        {
            if (bytes[index] == 0x50 && bytes[index + 1] == 0x4B && bytes[index + 2] == 0x05 && bytes[index + 3] == 0x06)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// An XML-aware tweak: inject a DOCTYPE with an external or a nested entity (the <c>xml-dtd</c> guard), grow
    /// the document past the depth cap, or re-encode it as UTF-16/UTF-32 (the wide-encoding path 06.3.1-T2's
    /// sniff table added).
    /// </summary>
    private static byte[] MarkupTweak(byte[] input, ref FuzzRandom random) => random.NextInt(5) switch
    {
        0 => InjectAfterPrologue(input, "<!DOCTYPE r [<!ENTITY x SYSTEM \"file:///etc/passwd\">]>"),
        1 => InjectAfterPrologue(input, "<!DOCTYPE r [<!ENTITY a \"x\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">]>"),
        2 => NestDeeper(input),
        3 => ReEncode(input, "utf-16", Encoding.Unicode),
        _ => ReEncode(input, "utf-32", Encoding.UTF32),
    };

    /// <summary>Inserts a DTD into the prologue, where the profile's byte scan looks for it.</summary>
    private static byte[] InjectAfterPrologue(byte[] input, string markup) =>
        InsertAt(input, PrologueEnd(input), Encoding.UTF8.GetBytes(markup));

    /// <summary>Opens <see cref="DeepNestingElements"/> nested elements inside the root element, past the depth cap.</summary>
    private static byte[] NestDeeper(byte[] input)
    {
        var nesting = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("<a>", DeepNestingElements)));
        var start = PrologueEnd(input);
        var open = IndexOf(input, (byte)'<', start);
        var close = open < 0 ? -1 : IndexOf(input, (byte)'>', open);

        return InsertAt(input, close < 0 ? start : close + 1, nesting);
    }

    /// <summary>Decodes the input byte-for-byte and re-encodes it wide, with the BOM and declaration that match.</summary>
    private static byte[] ReEncode(byte[] input, string encodingName, Encoding encoding)
    {
        var body = encoding.GetBytes($"<?xml version=\"1.0\" encoding=\"{encodingName}\"?>{Encoding.Latin1.GetString(input)}");
        var preamble = encoding.GetPreamble();
        var result = new byte[preamble.Length + body.Length];

        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);

        return result;
    }

    /// <summary>Where the prologue ends: after an XML declaration when the document starts with one, else 0.</summary>
    private static int PrologueEnd(byte[] input)
    {
        ReadOnlySpan<byte> declaration = "<?xml"u8;

        if (!input.AsSpan().StartsWith(declaration))
        {
            return 0;
        }

        var close = IndexOf(input, (byte)'>');

        return close < 0 ? 0 : close + 1;
    }

    private static byte[] InsertAt(byte[] input, int index, ReadOnlySpan<byte> inserted)
    {
        var result = new byte[input.Length + inserted.Length];

        input.AsSpan(0, index).CopyTo(result);
        inserted.CopyTo(result.AsSpan(index));
        input.AsSpan(index).CopyTo(result.AsSpan(index + inserted.Length));

        return result;
    }

    private static int IndexOf(byte[] bytes, byte value, int from = 0)
    {
        for (var index = from; index < bytes.Length; index++)
        {
            if (bytes[index] == value)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>The lowercase SHA-256 hex of an input, the identity the findings and the promotion are keyed on.</summary>
    internal static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

/// <summary>
/// The deterministic PRNG the harness mutates with (splitmix64): a recorded seed reproduces a run exactly,
/// which <see cref="Random"/> does not promise across runtimes.
/// </summary>
internal struct FuzzRandom
{
    private ulong _state;

    /// <summary>Seeds the generator.</summary>
    /// <param name="seed">The recorded seed.</param>
    internal FuzzRandom(ulong seed) => _state = seed;

    /// <summary>The next 64 bits of the stream.</summary>
    internal ulong NextUInt64()
    {
        _state += 0x9E3779B97F4A7C15UL;

        var value = _state;

        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;

        return value ^ (value >> 31);
    }

    /// <summary>A value in <c>[0, maxExclusive)</c>.</summary>
    /// <param name="maxExclusive">The exclusive upper bound.</param>
    internal int NextInt(int maxExclusive) => maxExclusive <= 1 ? 0 : (int)(NextUInt64() % (ulong)maxExclusive);

    /// <summary>One random byte.</summary>
    internal byte NextByte() => (byte)NextUInt64();
}

/// <summary>
/// The findings sink (06.3.2-T2.1): one <c>.bin</c> per finding carrying exactly the offending bytes, a JSONL
/// line describing it, and one <c>run.json</c> per run with the seed and the counts — so a green nightly run
/// still leaves an auditable record and a red one uploads the bytes.
/// </summary>
internal static class FuzzFindings
{
    /// <summary>The one indented options instance; CA1869 forbids one per call.</summary>
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Writes one finding: its bytes and one JSONL metadata line, under the findings directory.</summary>
    /// <param name="directory">The findings directory (created when absent).</param>
    /// <param name="finding">The finding to write.</param>
    internal static void Write(string directory, FuzzFinding finding)
    {
        Directory.CreateDirectory(directory);

        var digest = HostileFuzz.Sha256Hex(finding.Bytes);
        var file = $"{digest[..16]}-{finding.SeedName}-{finding.Target}.bin";

        File.WriteAllBytes(Path.Combine(directory, file), finding.Bytes);

        var record = JsonSerializer.Serialize(new FuzzFindingRecord(
            SeedName: finding.SeedName,
            Target: finding.Target.ToString(),
            Iteration: finding.Iteration,
            Detail: finding.Detail,
            Sha256: digest,
            Bytes: finding.Bytes.Length,
            File: file));

        File.AppendAllText(Path.Combine(directory, "findings.jsonl"), record + Environment.NewLine);
    }

    /// <summary>Writes the run's own record, so a clean run is still evidence and the seed is always recorded.</summary>
    /// <param name="directory">The findings directory (created when absent).</param>
    /// <param name="options">The bounds the run used.</param>
    /// <param name="seedCount">How many corpus seeds were used.</param>
    /// <param name="iterations">How many inputs were driven.</param>
    /// <param name="elapsed">How long the run took.</param>
    /// <param name="findingCount">How many findings the run recorded.</param>
    internal static void WriteRun(
        string directory,
        FuzzRunOptions options,
        int seedCount,
        long iterations,
        TimeSpan elapsed,
        int findingCount)
    {
        Directory.CreateDirectory(directory);

        var record = JsonSerializer.Serialize(
            new FuzzRunRecord(
                Seed: options.Seed,
                BudgetSeconds: options.BudgetSeconds,
                MaxIterations: options.MaxIterations,
                PerInputMilliseconds: options.PerInputMilliseconds,
                SeedCount: seedCount,
                Iterations: iterations,
                ElapsedSeconds: Math.Round(elapsed.TotalSeconds, 3),
                Findings: findingCount),
            Indented);

        File.WriteAllText(Path.Combine(directory, "run.json"), record + Environment.NewLine);
    }

    /// <summary>One finding's metadata line in <c>findings.jsonl</c>.</summary>
    private sealed record FuzzFindingRecord(
        string SeedName,
        string Target,
        long Iteration,
        string Detail,
        string Sha256,
        int Bytes,
        string File);

    /// <summary>The run's record in <c>run.json</c>.</summary>
    private sealed record FuzzRunRecord(
        ulong Seed,
        int BudgetSeconds,
        long MaxIterations,
        int PerInputMilliseconds,
        int SeedCount,
        long Iterations,
        double ElapsedSeconds,
        int Findings);
}

/// <summary>
/// The promotion helper (06.3.2-T2.1, plan §7/§9): it writes one finding into <c>fixtures/hostile/</c> as a new
/// committed fixture and appends its <c>manifest.json</c> row, so a crash the nightly run finds becomes a
/// permanent regression case. It NEVER runs by itself: <see cref="Enabled"/> reads
/// <c>EVIDGATE_FUZZ_PROMOTE=1</c>, which no CI job sets, and every call also has to pass
/// <c>allowPromotion: true</c>.
/// </summary>
internal static class FuzzPromotion
{
    /// <summary>The variable that turns promotion on for a local run: <c>EVIDGATE_FUZZ_PROMOTE=1</c>.</summary>
    internal const string PromoteVariable = "EVIDGATE_FUZZ_PROMOTE";

    /// <summary>The guard id a promoted XML finding names: the reader profile's well-formedness decision point.</summary>
    internal const string MarkupGuardId = "xml-wellformed";

    /// <summary>The guard id a promoted archive finding names: the central-directory pre-check.</summary>
    internal const string ArchiveGuardId = "zip-central-directory";

    /// <summary>True when the operator asked for promotion; false in CI, where the corpus stays read-only.</summary>
    internal static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable(PromoteVariable), "1", StringComparison.Ordinal);

    /// <summary>
    /// Writes <paramref name="finding"/>'s bytes into the corpus directory and appends its manifest row. The row
    /// names the guard that owns the finding's entry point, the entry point itself, and the documented refusal the
    /// input must produce — so the promoted file is an ordinary corpus row the 06.3.2-T1 suite asserts from then on.
    /// </summary>
    /// <param name="finding">The finding to promote.</param>
    /// <param name="corpusDirectory">The corpus directory (the real one locally, a temp copy in the smoke test).</param>
    /// <param name="expectedFailureCode">The documented <see cref="ParsingFailureReason"/> the fixed profile must answer with.</param>
    /// <param name="allowPromotion">The explicit opt-in; false refuses and writes nothing.</param>
    /// <returns>The row that was written.</returns>
    internal static PromotedFixture Promote(
        FuzzFinding finding,
        string corpusDirectory,
        string expectedFailureCode,
        bool allowPromotion)
    {
        if (!allowPromotion)
        {
            throw new InvalidOperationException(
                $"promotion writes into {corpusDirectory}; set {PromoteVariable}=1 and pass allowPromotion: true");
        }

        if (Array.IndexOf(ClosedReasons, expectedFailureCode) < 0)
        {
            throw new InvalidOperationException($"'{expectedFailureCode}' is not a documented parsing failure reason");
        }

        var markupless = finding.Target == FuzzTarget.Archive;
        var guardId = markupless ? ArchiveGuardId : MarkupGuardId;
        var driveMode = markupless ? HostileFixture.EnumerateMode : HostileFixture.ParseXmlMode;
        var extension = markupless ? ".zip" : ".xml";
        var file = $"fuzz_{HostileFuzz.Sha256Hex(finding.Bytes)[..12]}{extension}";
        var path = Path.Combine(corpusDirectory, file);

        if (File.Exists(path))
        {
            throw new InvalidOperationException($"{file} is already in {corpusDirectory}");
        }

        // The guard sentence names no exception text: a finding's detail can quote input bytes, and the corpus
        // manifest is committed (00 §5.7 S3). The detail stays in the findings directory.
        var guard = $"{guardId} — fuzz-promoted from {finding.SeedName}; the profile answered outside its documented outcomes";

        File.WriteAllBytes(path, finding.Bytes);
        AppendRow(corpusDirectory, file, guard, driveMode, expectedFailureCode);

        return new PromotedFixture(file, guard, driveMode, expectedFailureCode, path);
    }

    /// <summary>The promoted row, as it now stands in the corpus manifest.</summary>
    /// <param name="File">The new fixture's file name.</param>
    /// <param name="Guard">The guard sentence.</param>
    /// <param name="DrivenThrough">The entry point the row is driven through.</param>
    /// <param name="ExpectedFailureCode">The documented refusal the row expects.</param>
    /// <param name="Path">The new fixture's absolute path.</param>
    internal sealed record PromotedFixture(
        string File,
        string Guard,
        string DrivenThrough,
        string ExpectedFailureCode,
        string Path)
    {
        /// <summary>The guard id: the first word of <see cref="Guard"/>, as the manifest loader reads it.</summary>
        internal string GuardId => Guard.Split(' ', 2)[0];
    }

    /// <summary>The closed reason set, read from the constant type rather than duplicated.</summary>
    private static readonly string[] ClosedReasons =
    [
        .. typeof(ParsingFailureReason)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!),
    ];

    /// <summary>
    /// Appends one row to the corpus manifest, preserving every existing row byte-for-byte in value and writing
    /// the array with the same two-space indentation the corpus uses (so the file stays prettier-clean).
    /// </summary>
    private static void AppendRow(
        string corpusDirectory,
        string file,
        string guard,
        string driveMode,
        string expectedFailureCode)
    {
        var manifest = Path.Combine(corpusDirectory, HostileFixtureCorpus.ManifestFileName);

        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();

            foreach (var row in document.RootElement.EnumerateArray())
            {
                row.WriteTo(writer);
            }

            writer.WriteStartObject();
            writer.WriteString("file", file);
            writer.WriteString("guard", guard);
            writer.WriteString("driven_through", driveMode);
            writer.WriteString("expected_failure_code", expectedFailureCode);
            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        File.WriteAllBytes(manifest, [.. buffer.ToArray(), (byte)'\n']);
    }
}
