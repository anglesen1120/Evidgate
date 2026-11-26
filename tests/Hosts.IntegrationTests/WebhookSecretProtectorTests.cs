using System.Security.Cryptography;
using System.Text;
using Evidgate.Modules.Policy.Webhooks.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

public sealed class WebhookSecretProtectorTests
{
    [Fact]
    public void Generate_RoundTripsOnlyUnderWebhookPurpose_ExposesLastFourAsHint()
    {
        var provider = new EphemeralDataProtectionProvider();
        var protector = new WebhookSecretProtector(provider);
        var secret = protector.Generate();
        secret.Plaintext.ShouldStartWith("whsec_");
        secret.Plaintext.Length.ShouldBe(49);
        secret.Hint.ShouldBe(secret.Plaintext[^4..]);
        secret.Ciphertext.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret.Plaintext)).ShouldBe(-1);
        protector.Unprotect(secret.Ciphertext).ShouldBe(secret.Plaintext);
        Should.Throw<CryptographicException>(() => provider.CreateProtector("another purpose").Unprotect(secret.Ciphertext));
        protector.Generate().Plaintext.ShouldNotBe(secret.Plaintext);
        secret.ToString().ShouldNotContain(secret.Plaintext);
    }
}
