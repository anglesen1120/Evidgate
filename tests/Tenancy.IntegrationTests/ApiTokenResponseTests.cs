using Evidgate.Modules.Identity.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The create answer is the one response type that carries the plaintext token, so its textual form is a place the
/// secret can escape: a record's generated <c>ToString</c> prints every positional member, and that string reaches log
/// lines, exception messages and debuggers. The answer reports the token as its length instead, and this pins it.
/// </summary>
public sealed class ApiTokenResponseTests
{
    [Fact]
    public void Created_Response_Does_Not_Print_The_Plaintext()
    {
        var prefix = "AbCdEf012345";
        var secretPart = new string('S', 43);
        var secret = $"evg_{prefix}_{secretPart}";
        var response = new ApiTokenCreatedResponse(
            Guid.CreateVersion7(),
            "dashboard",
            prefix,
            secret,
            ["run:read"],
            DateTimeOffset.UtcNow.AddDays(30));

        var text = response.ToString();

        text.ShouldNotContain(secret, Case.Sensitive, "the plaintext token must never be printed");
        text.ShouldNotContain(secretPart, Case.Sensitive, "the secret half must never be printed either");
        text.ShouldContain($"{secret.Length} chars", Case.Sensitive, "the token is reported as its length");

        // Non-vacuity: the redaction keeps the rest of the answer, so an empty or stub ToString cannot pass this.
        text.ShouldContain("dashboard", Case.Sensitive);
        text.ShouldContain(prefix, Case.Sensitive);
        text.ShouldContain(response.Id.ToString("D"), Case.Sensitive);
        text.ShouldContain("run:read", Case.Sensitive);
    }
}
