using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;

namespace Evidgate.Modules.Authoring.Application.Intake;

/// <summary>
/// Refuses a PDF that is encrypted, runs code or carries other files, from its structure alone and before any
/// page content is decoded. Two passes: a lexical probe of the raw bytes that runs before the parser sees the file,
/// and a walk of every object reachable from the catalog, which is the authoritative check and sees objects
/// packed in object streams. Both decode <c>#xx</c> escapes in names.
/// <para>
/// The probe looks only at name tokens: literal strings, hex strings, comments and stream bodies are skipped, so
/// page text such as <c>(Call /Launch after login)</c> is not an action. It mimics the PDF lexer, not PdfPig's
/// tolerant one; where the two disagree the walk decides.
/// </para>
/// <para>
/// Mapping (a name is matched as a dictionary key or as a name value, e.g. <c>/S /Launch</c>):
/// <list type="table">
/// <item><term><c>/Encrypt</c></term><description><c>encryption_unsupported</c>: no password is ever tried.</description></item>
/// <item><term><c>/JavaScript</c>, <c>/JS</c>, <c>/OpenAction</c>, <c>/AA</c>, <c>/Launch</c>, <c>/RichMedia</c></term>
/// <description><c>macro_present</c>: content that runs or starts a program when the file is opened or used.</description></item>
/// <item><term><c>/EmbeddedFile</c>, <c>/EmbeddedFiles</c>, <c>/EF</c>, <c>/FileAttachment</c>, <c>/Filespec</c>, <c>/GoToR</c>, <c>/GoToE</c>, <c>/ImportData</c>, <c>/SubmitForm</c></term>
/// <description><c>external_reference_present</c>: the document carries or reaches content outside itself. <c>/EF</c> and
/// <c>/Filespec</c> are listed because <c>/Type /EmbeddedFile</c> is optional on the attached stream.</description></item>
/// </list>
/// </para>
/// </summary>
internal static class PdfActionScan
{
    private const int MaxObjects = 500_000;
    private const int ObjectsPerDeadlineCheck = 256;
    private const int TokensPerDeadlineCheck = 1024;
    private const string LengthName = "Length";

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
        ["EF"] = IntakeFailureCode.ExternalReferencePresent,
        ["FileAttachment"] = IntakeFailureCode.ExternalReferencePresent,
        ["Filespec"] = IntakeFailureCode.ExternalReferencePresent,
        ["GoToR"] = IntakeFailureCode.ExternalReferencePresent,
        ["GoToE"] = IntakeFailureCode.ExternalReferencePresent,
        ["ImportData"] = IntakeFailureCode.ExternalReferencePresent,
        ["SubmitForm"] = IntakeFailureCode.ExternalReferencePresent,
    };

    private static readonly int MaxNameLength = Forbidden.Keys.Max(static name => name.Length);
    private static readonly byte[] StreamKeyword = "stream"u8.ToArray();

    /// <summary>
    /// Looks for a forbidden name among the name tokens of the raw bytes, so the file is refused before any parser
    /// reads it. Encryption wins over a macro, a macro over an external reference, whatever the file order.
    /// </summary>
    /// <param name="document">The PDF bytes.</param>
    /// <param name="deadline">The wall-clock budget.</param>
    /// <exception cref="ExtractionRefusedException">A forbidden name is present, or a string is never closed.</exception>
    public static void ProbeRaw(byte[] document, ExtractionDeadline deadline)
    {
        ReadOnlySpan<byte> bytes = document;
        string? found = null;
        var boundary = new PdfStreamBoundary();
        var index = 0;
        var steps = 0;

        while (index < bytes.Length)
        {
            if (++steps % TokensPerDeadlineCheck == 0)
            {
                deadline.ThrowIfExpired();
            }

            var current = bytes[index];

            index = current switch
            {
                (byte)'%' => SkipComment(bytes, index),
                (byte)'(' => SkipLiteralString(bytes, index),
                (byte)'<' when index + 1 < bytes.Length && bytes[index + 1] == (byte)'<' => index + 2,
                (byte)'<' => SkipHexString(bytes, index),
                (byte)'/' => ReadName(bytes, index, ref found, boundary),
                _ when IsNameTerminator(current) => index + 1,
                _ => SkipKeyword(bytes, index, boundary),
            };
        }

        if (found is not null)
        {
            throw new ExtractionRefusedException(found);
        }
    }

    /// <summary>
    /// Walks every object reachable from the catalog and trailer. Stream data is never read, except that the parser
    /// decodes an object stream to resolve an object packed inside it; page content streams are not decoded.
    /// </summary>
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

    private static int SkipComment(ReadOnlySpan<byte> bytes, int start)
    {
        var index = start + 1;

        while (index < bytes.Length && bytes[index] is not (10 or 13))
        {
            index++;
        }

        return index;
    }

    /// <summary>Skips a literal string: nested parentheses balance and a backslash escapes the next byte.</summary>
    private static int SkipLiteralString(ReadOnlySpan<byte> bytes, int start)
    {
        var depth = 0;
        var index = start;

        while (index < bytes.Length)
        {
            var current = bytes[index++];

            if (current == (byte)'\\')
            {
                index++;
            }
            else if (current == (byte)'(')
            {
                depth++;
            }
            else if (current == (byte)')' && --depth == 0)
            {
                return index;
            }
        }

        throw new ExtractionRefusedException(IntakeFailureCode.ExtractionFailed);
    }

    private static int SkipHexString(ReadOnlySpan<byte> bytes, int start)
    {
        var close = bytes[(start + 1)..].IndexOf((byte)'>');

        return close < 0
            ? throw new ExtractionRefusedException(IntakeFailureCode.ExtractionFailed)
            : start + 1 + close + 1;
    }

    /// <summary>Skips a plain token; the <c>stream</c> keyword also skips the whole stream body.</summary>
    private static int SkipKeyword(ReadOnlySpan<byte> bytes, int start, PdfStreamBoundary boundary)
    {
        var end = start;

        while (end < bytes.Length && !IsNameTerminator(bytes[end]))
        {
            end++;
        }

        if (end >= bytes.Length || bytes[end] is not (10 or 13) || !bytes[start..end].SequenceEqual(StreamKeyword))
        {
            return end;
        }

        var bodyStart = bytes[end] == 13 && end + 1 < bytes.Length && bytes[end + 1] == 10 ? end + 2 : end + 1;

        return boundary.FindEnd(bytes, bodyStart) is var stop and >= 0 ? stop : end;
    }

    /// <summary>Reads the name after <c>/</c>, decodes <c>#xx</c> escapes and records or throws when it is forbidden.</summary>
    private static int ReadName(ReadOnlySpan<byte> bytes, int slash, ref string? found, PdfStreamBoundary boundary)
    {
        Span<char> name = stackalloc char[MaxNameLength + 1];
        var length = 0;
        var index = slash + 1;

        while (index < bytes.Length && !IsNameTerminator(bytes[index]))
        {
            var value = bytes[index];

            if (value == (byte)'#' && index + 2 < bytes.Length && HexValue(bytes[index + 1]) is >= 0 and var high && HexValue(bytes[index + 2]) is >= 0 and var low)
            {
                value = (byte)((high << 4) | low);
                index += 2;
            }

            if (length < name.Length)
            {
                name[length] = (char)value;
            }

            length++;
            index++;
        }

        if (length == LengthName.Length && name[..length].SequenceEqual(LengthName.AsSpan()))
        {
            boundary.NoteLength(bytes, index);
        }

        if (length <= MaxNameLength && Forbidden.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name[..length], out var code))
        {
            if (code == IntakeFailureCode.EncryptionUnsupported)
            {
                throw new ExtractionRefusedException(code);
            }

            found = found == IntakeFailureCode.MacroPresent ? found : code;
        }

        return index;
    }

    private static int HexValue(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - '0',
        >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
        _ => -1,
    };

    private static bool IsNameTerminator(byte value) =>
        PdfStreamBoundary.IsWhitespace(value)
        || value is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
            or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
}
