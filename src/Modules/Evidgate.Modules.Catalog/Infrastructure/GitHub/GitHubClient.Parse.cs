using System.Text.Json;
using Evidgate.BuildingBlocks.SharedKernel.GitHub;
using Evidgate.BuildingBlocks.SharedKernel.Results;

namespace Evidgate.Modules.Catalog.Infrastructure.GitHub;

/// <summary>
/// The parsers behind <see cref="GitHubClient"/>: each answer is read from an already-bounded
/// <see cref="JsonDocument"/> and refused as a whole when a member is missing, of the wrong kind,
/// or outside the shape the sync may act on — a partial object never leaves this file.
/// </summary>
internal sealed partial class GitHubClient
{
    private static Result<GitHubInstallation> ParseInstallation(JsonElement root, long expectedId)
    {
        if (!GitHubJson.TryGetId(root, "id", out var id)
            || id != expectedId
            || !root.TryGetProperty("account", out var account)
            || account.ValueKind != JsonValueKind.Object
            || !GitHubJson.TryGetString(account, "login", 256, out var login)
            || !GitHubJson.TryGetString(root, "repository_selection", 32, out var selection))
        {
            return Result.Failure<GitHubInstallation>(GitHubResponseReader.Unusable());
        }

        return Result.Success(new GitHubInstallation(id, login, selection));
    }

    private static Result<GitHubRepository> ParseRepository(JsonElement root, long expectedId)
    {
        if (!GitHubJson.TryGetId(root, "id", out var id)
            || id != expectedId
            || !GitHubJson.TryGetString(root, "full_name", 256, out var fullName)
            || !GitHubJson.TryGetString(root, "default_branch", 256, out var defaultBranch))
        {
            return Result.Failure<GitHubRepository>(GitHubResponseReader.Unusable());
        }

        return Result.Success(new GitHubRepository(id, fullName, defaultBranch));
    }

    private Result<GitHubTree> ParseTree(JsonElement root)
    {
        if (!GitHubJson.TryGetString(root, "sha", 64, out var treeSha)
            || !IsObjectId(treeSha)
            || !root.TryGetProperty("truncated", out var truncated)
            || truncated.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !root.TryGetProperty("tree", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
        {
            return Result.Failure<GitHubTree>(GitHubResponseReader.Unusable());
        }

        var maxEntries = _options.Value.MaxTreeEntries;
        if (entries.GetArrayLength() > maxEntries)
        {
            return Result.Failure<GitHubTree>(GitHubResponseReader.TooLarge());
        }

        var parsed = new List<GitHubTreeEntry>(entries.GetArrayLength());
        foreach (var entry in entries.EnumerateArray())
        {
            if (!TryParseTreeEntry(entry, out var item))
            {
                return Result.Failure<GitHubTree>(GitHubResponseReader.Unusable());
            }

            parsed.Add(item);
        }

        return Result.Success(new GitHubTree(treeSha, truncated.GetBoolean(), parsed));
    }

    private static bool TryParseTreeEntry(JsonElement entry, out GitHubTreeEntry item)
    {
        item = null!;

        if (entry.ValueKind != JsonValueKind.Object
            || !GitHubJson.TryGetString(entry, "path", 1024, out var path)
            || !IsSafeRepositoryPath(path)
            || !GitHubJson.TryGetString(entry, "type", 8, out var type)
            || type is not ("blob" or "tree" or "commit")
            || !GitHubJson.TryGetString(entry, "sha", 64, out var sha)
            || !IsObjectId(sha))
        {
            return false;
        }

        long size = 0;
        if (entry.TryGetProperty("size", out var declared))
        {
            if (declared.ValueKind != JsonValueKind.Number || !declared.TryGetInt64(out size) || size < 0)
            {
                return false;
            }
        }

        item = new GitHubTreeEntry(path, type, sha, size);
        return true;
    }

    private Result<GitHubBlob> ParseBlob(JsonElement root, string expectedSha)
    {
        if (!GitHubJson.TryGetPropertyString(root, "sha", out var sha)
            || !string.Equals(sha, expectedSha, StringComparison.Ordinal)
            || !GitHubJson.TryGetPropertyString(root, "encoding", out var encoding)
            || !string.Equals(encoding, "base64", StringComparison.Ordinal)
            || !root.TryGetProperty("size", out var sizeValue)
            || sizeValue.ValueKind != JsonValueKind.Number
            || !sizeValue.TryGetInt64(out var size)
            || size < 0
            || !GitHubJson.TryGetPropertyString(root, "content", out var content))
        {
            return Result.Failure<GitHubBlob>(GitHubResponseReader.Unusable());
        }

        var maxBytes = _options.Value.MaxBlobBytes;
        if (size > maxBytes || content.Count(static character => !char.IsWhiteSpace(character)) > ((maxBytes + 2) / 3 * 4))
        {
            return Result.Failure<GitHubBlob>(GitHubResponseReader.TooLarge());
        }

        var buffer = new byte[size];
        if (!Convert.TryFromBase64String(content, buffer, out var written) || written != size)
        {
            return Result.Failure<GitHubBlob>(GitHubResponseReader.Unusable());
        }

        return Result.Success(new GitHubBlob(sha, size, buffer));
    }
}
