// 06.3.2-T1.1 (AC1): oversized.bin is GENERATED, never committed — the only property that matters is that its
// size exceeds the kind cap the corpus declares, which a seed reproduces exactly.
namespace Tenancy.IntegrationTests;

/// <summary>
/// The 06.3.2 corpus's oversized object: <see cref="DeclaredBytes"/> bytes (above
/// <see cref="HostileFixtureCorpus.KindCapBytes"/>) of a deterministic filler. The completion refuses it with
/// <c>too_large</c> from the length the store's <c>HEAD</c> reports, before any prefix is read, so the object
/// costs one bounded allocation. The bytes are a pure function of <paramref name="seed"/>.
/// </summary>
internal static class GenerateOversizedFixture
{
    /// <summary>The fixture name the manifest marks <c>generated: true</c>.</summary>
    internal const string FileName = "oversized.bin";

    /// <summary>The seed every byte derives from.</summary>
    internal const int DefaultSeed = 20_261_013;

    /// <summary>The size in bytes: above <see cref="HostileFixtureCorpus.KindCapBytes"/>, far below the 8 MiB kind cap.</summary>
    internal const int DeclaredBytes = 8 * 1024;

    /// <summary>Builds the object (one allocation of <see cref="DeclaredBytes"/> bytes).</summary>
    /// <param name="seed">The seed the filler derives from.</param>
    /// <returns>The object bytes.</returns>
    internal static byte[] Build(int seed = DefaultSeed)
    {
        var content = new byte[DeclaredBytes];

        for (var index = 0; index < content.Length; index++)
        {
            content[index] = Filler(seed, index);
        }

        return content;
    }

    /// <summary>The deterministic filler byte of one offset: a prime-modulus walk, so no long run repeats.</summary>
    /// <param name="seed">The corpus seed.</param>
    /// <param name="index">The zero-based offset.</param>
    private static byte Filler(int seed, int index) => (byte)((seed + index) % 251);
}
