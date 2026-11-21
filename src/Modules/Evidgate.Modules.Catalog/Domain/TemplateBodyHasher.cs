using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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
public static class TemplateBodyHasher
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

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

        foreach (var (relativePath, bytes) in files)
        {
            ArgumentNullException.ThrowIfNull(bytes);

            if (!IsSafeRelativePath(relativePath))
            {
                throw new ArgumentException($"'{relativePath}' is not a canonical relative path.", nameof(files));
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

    /// <summary>True for a forward-slash path with no drive, no backslash and no empty, <c>.</c> or <c>..</c> segment.</summary>
    /// <param name="path">A relative path as packaged.</param>
    internal static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 512 || path[0] == '/' || path.Contains('\\', StringComparison.Ordinal))
        {
            return false;
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

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ArgumentException($"'{relativePath}' is not valid UTF-8.", nameof(bytes), exception);
        }

        var normalised = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character != '\r')
            {
                normalised.Append(character);
                continue;
            }

            if (index + 1 >= text.Length || text[index + 1] != '\n')
            {
                throw new ArgumentException($"'{relativePath}' holds a lone carriage return at {index}.", nameof(bytes));
            }

            normalised.Append('\n');
            index++;
        }

        return StrictUtf8.GetBytes(normalised.ToString());
    }
}
