using System.Security.Cryptography;
using Evidgate.Modules.Authoring.Trackers.Application;
using Microsoft.AspNetCore.DataProtection;

namespace Evidgate.Modules.Authoring.Trackers.Infrastructure;

/// <summary>
/// Protects tracker credentials at rest with ASP.NET Core Data Protection. Each provider's tokens and the PKCE
/// verifier have their own purpose, so a ciphertext copied from one provider's row into another provider's (or into a
/// consent row) cannot be unprotected there. A tampered or foreign value throws <see cref="CryptographicException"/>
/// without echoing any part of the plaintext.
/// </summary>
internal sealed class TrackerTokenProtector(IDataProtectionProvider provider)
{
    /// <summary>
    /// The largest plaintext token, in UTF-8 bytes, that is ever protected. After Data Protection framing and base64url
    /// a 4096-byte token stays well under the 8192-character cipher column limit.
    /// </summary>
    internal const int MaxPlaintextBytes = 4096;

    private const string TokenPurposePrefix = "Evidgate.Trackers.Token.v1.";
    private const string PkcePurpose = "Evidgate.Trackers.Pkce.v1";

    private readonly IDataProtector _jira = provider.CreateProtector(TokenPurposePrefix + "jira_cloud");
    private readonly IDataProtector _linear = provider.CreateProtector(TokenPurposePrefix + "linear");
    private readonly IDataProtector _pkce = provider.CreateProtector(PkcePurpose);

    /// <summary>Protects an access or refresh token of <paramref name="tracker"/> into the stored text.</summary>
    /// <param name="tracker">The provider the token was issued by.</param>
    /// <param name="token">The plaintext token.</param>
    public string ProtectToken(TrackerProvider tracker, string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return TokenProtector(tracker).Protect(token);
    }

    /// <summary>Recovers a token; throws <see cref="CryptographicException"/> for foreign or tampered input.</summary>
    /// <param name="tracker">The provider the stored token belongs to.</param>
    /// <param name="cipher">The stored text.</param>
    public string UnprotectToken(TrackerProvider tracker, string cipher)
    {
        ArgumentException.ThrowIfNullOrEmpty(cipher);
        return TokenProtector(tracker).Unprotect(cipher);
    }

    /// <summary>Protects a PKCE verifier into the stored text.</summary>
    /// <param name="verifier">The plaintext verifier.</param>
    public string ProtectPkceVerifier(string verifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(verifier);
        return _pkce.Protect(verifier);
    }

    /// <summary>Recovers a PKCE verifier; throws <see cref="CryptographicException"/> for foreign or tampered input.</summary>
    /// <param name="cipher">The stored text.</param>
    public string UnprotectPkceVerifier(string cipher)
    {
        ArgumentException.ThrowIfNullOrEmpty(cipher);
        return _pkce.Unprotect(cipher);
    }

    private IDataProtector TokenProtector(TrackerProvider tracker) => tracker switch
    {
        TrackerProvider.JiraCloud => _jira,
        TrackerProvider.Linear => _linear,
        _ => throw new ArgumentOutOfRangeException(nameof(tracker), tracker, "unknown tracker provider"),
    };

    /// <inheritdoc />
    public override string ToString() => nameof(TrackerTokenProtector);
}
