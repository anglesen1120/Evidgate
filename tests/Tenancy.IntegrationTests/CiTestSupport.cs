using System.Security.Cryptography;

namespace Tenancy.IntegrationTests;

/// <summary>The CI key material the test host is configured with (02.4.1/02.4.2), fixed per run.</summary>
internal sealed class CiTestKeys
{
    public CiTestKeys()
    {
        SigningKey = RandomNumberGenerator.GetBytes(32);
        Pepper = RandomNumberGenerator.GetBytes(32);
    }

    public byte[] SigningKey { get; }

    public byte[] Pepper { get; }

    public const string Issuer = "evidgate-test";

    public const string Audience = "evidgate-test-aud";

    public IReadOnlyDictionary<string, string?> Configuration => new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["EVIDGATE:Ci:SigningKey"] = Convert.ToBase64String(SigningKey),
        ["EVIDGATE:Ci:Issuer"] = Issuer,
        ["EVIDGATE:Ci:Audience"] = Audience,
        ["EVIDGATE:Ci:Pepper"] = Convert.ToBase64String(Pepper),
        ["EVIDGATE:Ci:PepperVersion"] = "2",
        ["EVIDGATE:Identity:RateLimits:CiExchangeLimit"] = "1000",
    };
}
