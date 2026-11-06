using Evidgate.Modules.Evidence.Application;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The reprocess route's version order, as a pure unit beside the evidence domain tests: it decides whether a
/// requested release is strictly newer than the one that judged a stored report, so the equality and unorderable
/// cases matter as much as the ascending one — an equal version would replace a report with itself, and a guessed
/// pre-release order would let a release the catalogue cannot rank replace a newer report.
/// </summary>
public sealed class ParserVersionOrderTests
{
    [Theory]
    [InlineData("1.10.0", "1.9.0")]
    [InlineData("1.0.1", "1.0.0")]
    [InlineData("2", "1.9.9")]
    [InlineData("1.0.0.1", "1.0.0")]
    public void A_greater_dotted_version_is_newer(string requested, string source) =>
        ParserVersionOrder.IsNewer(requested, source).ShouldBeTrue($"{requested} > {source}");

    [Theory]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.0", "1.0.0")]
    [InlineData("1.0.0", "1.0")]
    [InlineData("0.9.9", "1")]
    public void An_equal_or_older_version_is_not_newer(string requested, string source) =>
        ParserVersionOrder.IsNewer(requested, source).ShouldBeFalse($"{requested} is not newer than {source}");

    [Theory]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("1.0.0+build", "1.0.0")]
    [InlineData("1..2", "1.0.0")]
    [InlineData("1.0.0", "1.0.0-rc.1")]
    [InlineData("v1", "1.0.0")]
    [InlineData("", "1.0.0")]
    [InlineData("   ", "1.0.0")]
    [InlineData("1.0.0", "")]
    [InlineData(null, "1.0.0")]
    [InlineData("1.0.0", null)]
    public void An_unorderable_version_is_never_newer(string? requested, string? source) =>
        ParserVersionOrder.IsNewer(requested, source).ShouldBeFalse($"{requested} cannot be ordered against {source}");

    [Fact]
    public void A_segment_beyond_a_fixed_integer_is_compared_exactly()
    {
        // Two versions that differ only past the 64-bit range: a parser that folded segments into long would see
        // them as equal and refuse a release that is in fact newer.
        var requested = "1.18446744073709551616";
        var source = "1.18446744073709551615";

        ParserVersionOrder.IsNewer(requested, source).ShouldBeTrue();
        ParserVersionOrder.IsNewer(source, requested).ShouldBeFalse();
    }
}
