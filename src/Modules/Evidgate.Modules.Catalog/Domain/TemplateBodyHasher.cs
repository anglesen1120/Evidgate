using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Unicode;

namespace Evidgate.Modules.Catalog.Domain;

/// <summary>
/// The content address of one packaged template version (<c>sha256:&lt;hex&gt;</c>). The frame is fixed so a
/// directory hashed here and by the release tooling is the same value: for each file, sorted by ITS UTF-8 path
/// bytes, the path, a NUL, the decimal byte length of the normalised content, a NUL, the content and a line feed.
/// </summary>
/// <remarks>
/// Text is refused rather than guessed at: a byte-order mark, invalid UTF-8 or a lone carriage return means the
/// packaged file was written by a tool this hash cannot reproduce, so it is an error, never a silent normalisation.
/// The caller reads the directory; the domain does no IO.
/// </remarks>
internal static class TemplateBodyHasher
{
    /// <summary>Largest number of packaged files hashed.</summary>
    internal const int MaxFiles = 256;

    /// <summary>Largest total number of content bytes hashed.</summary>
    internal const long MaxTotalBytes = 8L * 1024 * 1024;

    private static readonly UTF8Encoding PathUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly byte[] Utf8ByteOrderMark = [0xEF, 0xBB, 0xBF];

    /// <summary>Hashes the packaged files of one template version.</summary>
    /// <param name="files">Relative paths (forward slashes) and their raw bytes, each read from the packaged directory.</param>
    /// <returns><c>sha256:&lt;lowercase hex&gt;</c>.</returns>
    /// <exception cref="ArgumentException">A path is not relative and canonical, two files share a path, or a file is not normalised UTF-8 text.</exception>
    public static string Compute(IEnumerable<(string RelativePath, byte[] Bytes)> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var packed = new List<(byte[] Path, byte[] Content)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;

        foreach (var (relativePath, bytes) in files)
        {
            ArgumentNullException.ThrowIfNull(bytes);

            if (packed.Count >= MaxFiles)
            {
                throw new ArgumentException($"A template version carries at most {MaxFiles} files.", nameof(files));
            }

            if (!IsSafeRelativePath(relativePath))
            {
                throw new ArgumentException($"'{relativePath}' is not a canonical relative path.", nameof(files));
            }

            total += bytes.Length;
            if (total > MaxTotalBytes)
            {
                throw new ArgumentException($"A template version carries more than {MaxTotalBytes} bytes.", nameof(files));
            }

            if (!seen.Add(relativePath))
            {
                throw new ArgumentException($"'{relativePath}' is listed twice.", nameof(files));
            }

            packed.Add((PathUtf8.GetBytes(relativePath), Normalise(relativePath, bytes)));
        }

        packed.Sort(static (left, right) => left.Path.AsSpan().SequenceCompareTo(right.Path));

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (path, content) in packed)
        {
            hash.AppendData(path);
            hash.AppendData([0]);
            hash.AppendData(PathUtf8.GetBytes(content.Length.ToString(CultureInfo.InvariantCulture)));
            hash.AppendData([0]);
            hash.AppendData(content);
            hash.AppendData([10]);
        }

        return "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// True for a forward-slash path that a UTF-8 encoder can carry: no control, line-separator or bidi-control
    /// character, no lone surrogate, no drive, no backslash and no empty, <c>.</c> or <c>..</c> segment.
    /// </summary>
    /// <param name="path">A relative path as packaged.</param>
    internal static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 512 || path[0] == '/' || path.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        for (var index = 0; index < path.Length; index++)
        {
            var character = path[index];
            if (IsUnsafePathCharacter(character))
            {
                return false;
            }

            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= path.Length || !char.IsLowSurrogate(path[index + 1]))
                {
                    return false;
                }

                index++;
                continue;
            }

            if (char.IsLowSurrogate(character))
            {
                return false;
            }
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.Contains(':', StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True for a control character (C0, DEL or C1), a line or paragraph separator, or a bidirectional control: each
    /// can split or reorder the path where it is shown, so the bytes and the displayed path would disagree.
    /// </summary>
    private static bool IsUnsafePathCharacter(char character) =>
        char.IsControl(character)
        || character is '\u2028' or '\u2029'
        || character is >= '\u200E' and <= '\u200F'
        || character is >= '\u202A' and <= '\u202E'
        || character is >= '\u2066' and <= '\u2069';

    private static byte[] Normalise(string relativePath, byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return [];
        }

        if (bytes.AsSpan().StartsWith(Utf8ByteOrderMark))
        {
            throw new ArgumentException($"'{relativePath}' starts with a byte-order mark.", nameof(bytes));
        }

        if (!Utf8.IsValid(bytes))
        {
            throw new ArgumentException($"'{relativePath}' is not valid UTF-8.", nameof(bytes));
        }

        // A file without a carriage return needs no copy: the validated bytes are the normalised bytes.
        var carriageReturns = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != '\r')
            {
                continue;
            }

            if (index + 1 >= bytes.Length || bytes[index + 1] != '\n')
            {
                throw new ArgumentException($"'{relativePath}' holds a lone carriage return at {index}.", nameof(bytes));
            }

            carriageReturns++;
        }

        if (carriageReturns == 0)
        {
            return bytes;
        }

        var folded = new byte[bytes.Length - carriageReturns];
        var written = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            var value = bytes[index];
            if (value == '\r')
            {
                index++;
                value = (byte)'\n';
            }

            folded[written++] = value;
        }

        return folded;
    }
}
