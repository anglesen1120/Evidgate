namespace Evidgate.Modules.Catalog.Domain;

/// <summary>
/// Repository-relative path rules of the catalog (03.1.1): forward slashes only, no absolute path, no empty,
/// <c>.</c> or <c>..</c> segment, no control character, at most 1024 characters. A path that breaks them is never
/// stored or resolved, so a sidecar cannot name a file outside its own directory.
/// </summary>
internal static class RepoPath
{
    /// <summary>The column limit of <c>test_case.file_path</c>.</summary>
    internal const int MaxLength = 1024;

    /// <summary>True when <paramref name="path"/> is a well-formed repository-relative path.</summary>
    /// <param name="path">The candidate.</param>
    public static bool IsValid(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxLength || path[0] == '/' || path[^1] == '/')
        {
            return false;
        }

        if (path.Contains('\\', StringComparison.Ordinal) || !TextRules.IsClean(path))
        {
            return false;
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The directory part of <paramref name="path"/> (empty for a root file).</summary>
    /// <param name="path">A valid path.</param>
    public static string DirectoryOf(string path)
    {
        var slash = path.LastIndexOf('/');

        return slash < 0 ? string.Empty : path[..slash];
    }

    /// <summary>The last segment of <paramref name="path"/>.</summary>
    /// <param name="path">A valid path.</param>
    public static string FileNameOf(string path)
    {
        var slash = path.LastIndexOf('/');

        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary>The path of <paramref name="relative"/> inside <paramref name="directory"/>, or null when <paramref name="relative"/> is not a valid relative path.</summary>
    /// <param name="directory">The manifest's directory (empty for the root).</param>
    /// <param name="relative">The <c>file</c> value of a sidecar entry.</param>
    public static string? Combine(string directory, string relative)
    {
        if (!IsValid(relative))
        {
            return null;
        }

        var combined = directory.Length == 0 ? relative : directory + "/" + relative;

        return IsValid(combined) ? combined : null;
    }
}
