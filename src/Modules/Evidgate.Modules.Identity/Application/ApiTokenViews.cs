using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Domain;

namespace Evidgate.Modules.Identity.Application;

/// <summary>Maps a token row to the two wire shapes the module serves, so neither list nor create can leak a stored hash.</summary>
internal static class ApiTokenViews
{
    /// <summary>The create answer: the metadata plus the plaintext that was just minted.</summary>
    /// <param name="token">The inserted row.</param>
    /// <param name="plaintext">The full credential, which is not stored.</param>
    public static ApiTokenCreatedResponse Created(ApiToken token, string plaintext) => new(
        token.Id,
        token.Name,
        token.Prefix,
        plaintext,
        token.Permissions,
        token.ExpiresAt);

    /// <summary>The listed metadata: id, label, prefix, subset, derived status and lifetime fields.</summary>
    /// <param name="token">The row.</param>
    /// <param name="now">The request's clock, for the derived status.</param>
    public static ApiTokenView Listed(ApiToken token, DateTimeOffset now) => new(
        token.Id,
        token.Name,
        token.Prefix,
        token.Permissions,
        token.StatusAt(now),
        token.ExpiresAt,
        token.LastUsedAt,
        token.CreatedBy);
}
