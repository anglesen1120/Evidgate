using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;

namespace Evidgate.Modules.Authoring.Application.Intake;

/// <summary>
/// Refuses a PDF that is encrypted, runs code or carries other files, from its structure alone and before any
/// page content is decoded. Two passes: a raw byte probe that runs before the parser sees the file, and a walk
/// of every object reachable from the catalog, which also sees names written with <c>#xx</c> escapes and
/// objects packed in object streams.
/// <para>
/// Mapping (a name is matched as a dictionary key or as a name value, e.g. <c>/S /Launch</c>):
/// <list type="table">
/// <item><term><c>/Encrypt</c></term><description><c>encryption_unsupported</c>: no password is ever tried.</description></item>
/// <item><term><c>/JavaScript</c>, <c>/JS</c>, <c>/OpenAction</c>, <c>/AA</c>, <c>/Launch</c>, <c>/RichMedia</c></term>
/// <description><c>macro_present</c>: content that runs or starts a program when the file is opened or used.</description></item>
/// <item><term><c>/EmbeddedFile</c>, <c>/EmbeddedFiles</c>, <c>/GoToR</c>, <c>/GoToE</c>, <c>/ImportData</c>, <c>/SubmitForm</c></term>
/// <description><c>external_reference_present</c>: the document carries or reaches content outside itself.</description></item>
/// </list>
/// </para>
/// </summary>
internal static class PdfActionScan
{
    private const int MaxObjects = 500_000;
    private const int ObjectsPerDeadlineCheck = 256;

    private static readonly Dictionary<string, string> Forbidden = new(StringComparer.Ordinal)
    {
        ["Encrypt"] = IntakeFailureCode.EncryptionUnsupported,
        ["JavaScript"] = IntakeFailureCode.MacroPresent,
        ["JS"] = IntakeFailureCode.MacroPresent,
        ["OpenAction"] = IntakeFailureCode.MacroPresent,
        ["AA"] = IntakeFailureCode.MacroPresent,
        ["Launch"] = IntakeFailureCode.MacroPresent,
        ["RichMedia"] = IntakeFailureCode.MacroPresent,
        ["EmbeddedFile"] = IntakeFailureCode.ExternalReferencePresent,
        ["EmbeddedFiles"] = IntakeFailureCode.ExternalReferencePresent,
        ["GoToR"] = IntakeFailureCode.ExternalReferencePresent,
        ["GoToE"] = IntakeFailureCode.ExternalReferencePresent,
        ["ImportData"] = IntakeFailureCode.ExternalReferencePresent,
        ["SubmitForm"] = IntakeFailureCode.ExternalReferencePresent,
    };

    private static readonly byte[][] RawPatterns = [.. Forbidden.Keys.Select(name => System.Text.Encoding.ASCII.GetBytes('/' + name))];
    private static readonly string[] RawCodes = [.. Forbidden.Values];

    /// <summary>Searches the raw bytes for a forbidden name, so the file is refused before any parser reads it.</summary>
    /// <param name="document">The PDF bytes.</param>
    /// <param name="deadline">The wall-clock budget.</param>
    /// <exception cref="ExtractionRefusedException">A forbidden name is present.</exception>
    public static void ProbeRaw(byte[] document, ExtractionDeadline deadline)
    {
        for (var index = 0; index < RawPatterns.Length; index++)
        {
            deadline.ThrowIfExpired();

            if (ContainsName(document, RawPatterns[index]))
            {
                throw new ExtractionRefusedException(RawCodes[index]);
            }
        }
    }

    /// <summary>Walks every object reachable from the catalog and trailer, without decoding any stream.</summary>
    /// <param name="document">The opened document.</param>
    /// <param name="deadline">The wall-clock budget.</param>
    /// <exception cref="ExtractionRefusedException">A forbidden name is reachable, or the graph exceeds the object cap.</exception>
    public static void WalkReachable(PdfDocument document, ExtractionDeadline deadline)
    {
        var scanner = document.Structure.TokenScanner;
        var visited = new HashSet<IndirectReference>();
        var pending = new Stack<IToken>();
        pending.Push(document.Structure.Catalog.CatalogDictionary);

        if (document.Structure.Trailer.EncryptionToken is { } encryption)
        {
            pending.Push(encryption);
        }

        var steps = 0;

        while (pending.TryPop(out var token))
        {
            if (++steps % ObjectsPerDeadlineCheck == 0)
            {
                deadline.ThrowIfExpired();
            }

            if (steps > MaxObjects)
            {
                throw new ExtractionRefusedException(IntakeFailureCode.ArchiveLimitExceeded);
            }

            Visit(token, scanner, visited, pending);
        }
    }

    private static void Visit(
        IToken token,
        UglyToad.PdfPig.Tokenization.Scanner.IPdfTokenScanner scanner,
        HashSet<IndirectReference> visited,
        Stack<IToken> pending)
    {
        switch (token)
        {
            case NameToken name:
                RefuseName(name.Data);
                break;
            case DictionaryToken dictionary:
                foreach (var (key, value) in dictionary.Data)
                {
                    RefuseName(key);
                    pending.Push(value);
                }

                break;
            case ArrayToken array:
                foreach (var item in array.Data)
                {
                    pending.Push(item);
                }

                break;
            case StreamToken stream:
                pending.Push(stream.StreamDictionary);
                break;
            case ObjectToken wrapped:
                pending.Push(wrapped.Data);
                break;
            case IndirectReferenceToken reference when visited.Add(reference.Data):
                if (scanner.Get(reference.Data) is { } resolved)
                {
                    pending.Push(resolved);
                }

                break;
        }
    }

    private static void RefuseName(string name)
    {
        if (Forbidden.TryGetValue(name, out var code))
        {
            throw new ExtractionRefusedException(code);
        }
    }

    /// <summary>True when <paramref name="pattern"/> occurs as a whole name: followed by whitespace, a delimiter or the end.</summary>
    private static bool ContainsName(ReadOnlySpan<byte> document, ReadOnlySpan<byte> pattern)
    {
        var offset = 0;

        while (offset <= document.Length - pattern.Length)
        {
            var found = document[offset..].IndexOf(pattern);

            if (found < 0)
            {
                return false;
            }

            var end = offset + found + pattern.Length;

            if (end == document.Length || IsNameTerminator(document[end]))
            {
                return true;
            }

            offset += found + 1;
        }

        return false;
    }

    private static bool IsNameTerminator(byte value) =>
        value is 0 or 9 or 10 or 12 or 13 or 32
            or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
            or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
}
