using Evidgate.Modules.Identity.Login;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login;

/// <summary>
/// 02.3.1 §5 / §8 (<c>ReturnUrlPolicyTests</c>): the <c>returnUrl</c> allowlist accepts app-origin paths
/// only. Anything that could leave the origin — in any percent-encoded form, with any separator a browser
/// might reinterpret — is replaced by the app root rather than turned into an error.
/// </summary>
// The assembly fixture of this project (DatabaseFixture) needs Docker, so the smoke build, which has none, skips Category=Docker.
[Trait("Category", "Docker")]
public sealed class ReturnUrlPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/runs/42?x=1#a")]
    [InlineData("/orgs/0190f000-0000-7000-8000-000000000000/projects/0190f000-0000-7000-8000-000000000001/runs")]
    [InlineData("/runs?page=2&limit=50")]
    public void An_App_Path_Is_Kept(string? candidate) =>
        ReturnUrlPolicy.Resolve(candidate)
            .ShouldBe(string.IsNullOrEmpty(candidate) ? ReturnUrlPolicy.Root : candidate);

    [Theory]
    [InlineData("//evil.example")] // network-path reference: leaves the origin
    [InlineData("/\\evil.example")] // some clients normalise the backslash to '/'
    [InlineData("\\\\evil.example")] // UNC-shaped
    [InlineData("https://evil.example")] // absolute URL
    [InlineData("javascript:alert(1)")] // scheme, not a path
    [InlineData("evil.example/path")] // relative path, resolves against the login route
    [InlineData("%2f%2fevil.example")] // the whole value encoded, without a leading slash
    [InlineData("/%2f%2fevil.example")] // encoded leading double slash
    [InlineData("/%2F%2Fevil.example")] // same, upper-case hex
    [InlineData("/%5cevil.example")] // encoded backslash
    [InlineData("/%252f%252fevil.example")] // double-encoded double slash
    [InlineData("/%2525252fevil")] // an encoding chain deeper than the decoding bound
    [InlineData("/runs\t42")] // control character
    [InlineData("/runs\n42")] // newline injection
    [InlineData("/runs\r\nSet-Cookie: x=1")] // header injection attempt
    [InlineData("/runs 42")] // whitespace
    [InlineData(" /runs/42")] // leading whitespace
    public void A_Value_That_Leaves_The_Origin_Falls_Back_To_The_Root(string candidate) =>
        ReturnUrlPolicy.Resolve(candidate).ShouldBe(ReturnUrlPolicy.Root);

    [Fact]
    public void An_Over_Long_Value_Falls_Back_To_The_Root()
    {
        var candidate = "/" + new string('a', ReturnUrlPolicy.MaxLength);

        candidate.Length.ShouldBe(ReturnUrlPolicy.MaxLength + 1);
        ReturnUrlPolicy.Resolve(candidate).ShouldBe(ReturnUrlPolicy.Root);
    }

    [Fact]
    public void A_Value_At_The_Length_Bound_Is_Kept()
    {
        var candidate = "/" + new string('a', ReturnUrlPolicy.MaxLength - 1);

        candidate.Length.ShouldBe(ReturnUrlPolicy.MaxLength);
        ReturnUrlPolicy.Resolve(candidate).ShouldBe(candidate);
    }

    [Fact]
    public void IsAppPath_Rejects_Control_Characters_And_Backslashes()
    {
        ReturnUrlPolicy.IsAppPath("/ok").ShouldBeTrue();
        ReturnUrlPolicy.IsAppPath("/a\\b").ShouldBeFalse();
        ReturnUrlPolicy.IsAppPath("/a\0b").ShouldBeFalse();
        ReturnUrlPolicy.IsAppPath(new string('a', 8) + "://evil").ShouldBeFalse();
        ReturnUrlPolicy.IsAppPath(null).ShouldBeFalse();
    }

    [Fact]
    public void A_Non_Breaking_Space_Does_Not_Pass_As_An_Ordinary_Character()
    {
        ReturnUrlPolicy.Resolve("/runs\u00a042").ShouldBe(ReturnUrlPolicy.Root);
    }
}
