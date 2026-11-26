using System.Security.Cryptography;
using Evidgate.Modules.Policy.Webhooks.Application;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

public sealed class WebhookSigningTests
{
    [Fact]
    public void Signature_FixedTimestampAndBody_MatchesReceiverVector()
    {
        var body = """{"evaluation_id":"abc"}"""u8;
        var signed = WebhookSigner.Sign("whsec_fixture", 1700000000, body);
        signed.ShouldBe("v1=072f5a50a15e65e92bb9463b5ce2c210da29c84b9397122de496178bb9fea93a");
        var unsignedTime = HMACSHA256.HashData("whsec_fixture"u8, body);
        CryptographicOperations.FixedTimeEquals(unsignedTime, Convert.FromHexString(signed[3..])).ShouldBeFalse();
        WebhookSigner.Sign("whsec_fixture", 1700000001, body).ShouldNotBe(signed);
    }
}
