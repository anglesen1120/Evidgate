using System.Text.RegularExpressions;
using Evidgate.BuildingBlocks.SharedKernel.Security;
using Evidgate.Modules.Ingestion.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed class AsciiShapeTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("a", false)]
    [InlineData("ab", true)]
    [InlineData("a-", false)]
    [InlineData("-a", false)]
    [InlineData("a-b", true)]
    [InlineData("ab\n", false)]
    public void IsMatch_WithClosingSet_NeedsTwoCharactersAndBothEnds(string value, bool expected) =>
        AsciiShape.IsMatch(value, 1, 63, AsciiShape.LowerAlnum, AsciiShape.LowerAlnumDash, AsciiShape.LowerAlnum)
            .ShouldBe(expected);

    [Theory]
    [InlineData("a", true)]
    [InlineData("aa", true)]
    [InlineData("aaa", false)]
    [InlineData("", false)]
    public void IsMatch_RespectsMinAndMaxLength(string value, bool expected) =>
        AsciiShape.IsMatch(value, 1, 2, AsciiShape.LowerAlnum, AsciiShape.LowerAlnum).ShouldBe(expected);

    [Fact]
    public void IsMatch_NullString_IsRefused() =>
        AsciiShape.IsMatch(null, 1, 2, AsciiShape.LowerAlnum, AsciiShape.LowerAlnum).ShouldBeFalse();

    [Theory]
    [InlineData("a", true)]
    [InlineData("a.b_c-d", true)]
    [InlineData("A", false)]
    [InlineData("-a", false)]
    [InlineData("a b", false)]
    [InlineData("a\n", false)]
    public void AdapterNamePattern_AndIsValid_AgreeOnTheFirstSendPathGrammar(string name, bool expected)
    {
        var pattern = new Regex("\\A" + AdapterName.Pattern + "\\z", RegexOptions.CultureInvariant);
        pattern.IsMatch(name).ShouldBe(expected);
        AdapterName.IsValid(name).ShouldBe(expected);
    }
}
