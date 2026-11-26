using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace Evidgate.Modules.Policy.Webhooks.Infrastructure;

/// <summary>A freshly generated endpoint secret: shown once, stored only as <see cref="Ciphertext"/> and <see cref="Hint"/>.</summary>
internal sealed class GeneratedWebhookSecret
{
    internal GeneratedWebhookSecret(string plaintext, byte[] ciphertext)
    {
        Plaintext = plaintext;
        Ciphertext = ciphertext;
        Hint = plaintext[^4..];
    }

    /// <summary>The <c>whsec_...</c> value for the one response that returns it.</summary>
    public string Plaintext { get; }

    /// <summary>The Data Protection payload to store in <c>secret_ciphertext</c>.</summary>
    public byte[] Ciphertext { get; }

    /// <summary>The last four characters of the plaintext, safe to show.</summary>
    public string Hint { get; }

    /// <inheritdoc />
    public override string ToString() => nameof(GeneratedWebhookSecret);
}

/// <summary>Creates endpoint secrets and protects them at rest with ASP.NET Core Data Protection under a webhook-only purpose.</summary>
internal sealed class WebhookSecretProtector(IDataProtectionProvider provider)
{
    private const string Purpose = "Evidgate.Policy.Webhooks.EndpointSecret.v1";
    private const int SecretBytes = 32;

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    /// <summary>Generates 32 random bytes as <c>whsec_</c> plus base64url and protects them.</summary>
    public GeneratedWebhookSecret Generate()
    {
        var plaintext = "whsec_" + Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));

        return new GeneratedWebhookSecret(plaintext, _protector.Protect(Encoding.UTF8.GetBytes(plaintext)));
    }

    /// <summary>Recovers the plaintext secret; throws <see cref="CryptographicException"/> for foreign or tampered input.</summary>
    /// <param name="ciphertext">The stored payload.</param>
    public string Unprotect(byte[] ciphertext) => Encoding.UTF8.GetString(_protector.Unprotect(ciphertext));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
