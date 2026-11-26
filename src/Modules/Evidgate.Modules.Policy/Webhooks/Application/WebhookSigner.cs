using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Evidgate.Modules.Policy.Webhooks.Application;

/// <summary>The wire format a receiver verifies: headers and a v1 HMAC over <c>timestamp + "." + body</c>.</summary>
internal static class WebhookSigner
{
    /// <summary>The dedupe key header; the evaluation id.</summary>
    public const string EventIdHeader = "Evidgate-Event-Id";

    /// <summary>The event type header.</summary>
    public const string EventTypeHeader = "Evidgate-Event-Type";

    /// <summary>Unix seconds the signature was made at; part of the signed text.</summary>
    public const string TimestampHeader = "Evidgate-Timestamp";

    /// <summary>The signature header, <c>v1=&lt;lowercase hex&gt;</c>.</summary>
    public const string SignatureHeader = "Evidgate-Signature";

    /// <summary>Signs <paramref name="body"/> with the UTF-8 bytes of <paramref name="secret"/> as the key.</summary>
    /// <param name="secret">The endpoint secret as shown to the user (<c>whsec_...</c>).</param>
    /// <param name="unixSeconds">The timestamp sent in <see cref="TimestampHeader"/>.</param>
    /// <param name="body">The exact bytes posted.</param>
    public static string Sign(string secret, long unixSeconds, ReadOnlySpan<byte> body)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret));
        hmac.AppendData(Encoding.ASCII.GetBytes(unixSeconds.ToString(CultureInfo.InvariantCulture)));
        hmac.AppendData("."u8);
        hmac.AppendData(body);

        return "v1=" + Convert.ToHexStringLower(hmac.GetHashAndReset());
    }
}
