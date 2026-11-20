using System.Text.Json;
using Evidgate.Modules.Orchestrator.Application;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed class RequestMemberPathTests
{
    [Theory]
    [InlineData("zzz", "/zzz")]
    [InlineData("café", "/café")]
    [InlineData("", "/")]
    [InlineData("a/b", "/<invalid-member-name>")]
    [InlineData("a~b", "/<invalid-member-name>")]
    [InlineData("a\nb", "/<invalid-member-name>")]
    [InlineData("a\u0001b", "/<invalid-member-name>")]
    [InlineData("a\u0000b", "/<invalid-member-name>")]
    [InlineData("a\u007fb", "/<invalid-member-name>")]
    [InlineData("a\u200bb", "/<invalid-member-name>")]
    [InlineData("a\u202eb", "/<invalid-member-name>")]
    [InlineData(null, "/<invalid-member-name>")]
    public void UnknownMember_TextHygiene_PinsPath(string? name, string expected) =>
        RequestMemberPath.ForUnknown(name).ShouldBe(expected);

    [Fact]
    public void UnknownMember_Utf16Length64IsEchoed_65UsesPlaceholder()
    {
        var boundary = new string('a', 64);
        RequestMemberPath.ForUnknown(boundary).ShouldBe("/" + boundary);
        RequestMemberPath.ForUnknown(boundary + "a").ShouldBe("/<invalid-member-name>");
    }

    [Fact]
    public void UnknownMember_AstralRuneAcrossLengthBoundary_IsNeverTruncated()
    {
        var boundary = new string('a', 62) + "\ud83d\ude00";
        RequestMemberPath.ForUnknown(boundary).ShouldBe("/" + boundary);
        RequestMemberPath.ForUnknown("a" + boundary).ShouldBe("/<invalid-member-name>");
        RequestMemberPath.ForUnknown(new string('a', 63) + "\ud83d").ShouldBe("/<invalid-member-name>");
        RequestMemberPath.ForUnknown("\udc00").ShouldBe("/<invalid-member-name>");
        RequestMemberPath.ForUnknown("\ud83d").ShouldBe("/<invalid-member-name>");
        RequestMemberPath.ForUnknown("a\U000e0001b").ShouldBe("/<invalid-member-name>");
    }

    [Fact]
    public void UnknownMember_RefPrefix_AppliesOnlyToEchoableNames()
    {
        RequestMemberPath.ForUnknown("zzz", "/ref").ShouldBe("/ref/zzz");
        RequestMemberPath.ForUnknown(string.Empty, "/ref").ShouldBe("/ref/");
        RequestMemberPath.ForUnknown("a/b", "/ref").ShouldBe("/<invalid-member-name>");
        RequestMemberPath.ForUnknown(new string('a', 65), "/ref").ShouldBe("/<invalid-member-name>");
        RequestMemberPath.ForUnknown(null, "/ref").ShouldBe("/<invalid-member-name>");
    }

    [Fact]
    public void FirstDisallowed_FindsTheFirstUnknownMember_AndNullWhenAllAreAllowed()
    {
        using var unknown = JsonDocument.Parse("""{"reason":"x","zzz":1}""");
        RequestMemberPath.FirstDisallowed(unknown.RootElement, ["reason"]).ShouldBe("/zzz");

        using var allowed = JsonDocument.Parse("""{"reason":"x"}""");
        RequestMemberPath.FirstDisallowed(allowed.RootElement, ["reason"]).ShouldBeNull();
    }

    [Fact]
    public void FirstDisallowed_UnreadableName_AnswersPlaceholderInsteadOfThrowing()
    {
        // The parser accepts the escape; reading the member's name throws while the object is enumerated.
        using var document = JsonDocument.Parse("""{"\ud800":1}""");

        RequestMemberPath.FirstDisallowed(document.RootElement, ["reason"]).ShouldBe("/<invalid-member-name>");
    }
}
