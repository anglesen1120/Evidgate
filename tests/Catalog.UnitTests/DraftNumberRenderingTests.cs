using Evidgate.Modules.Catalog.Application;
using Shouldly;
using Xunit;

namespace Catalog.UnitTests;

/// <summary>
/// The provenance budget counts the characters PostgreSQL writes for a number (numeric text is never scientific), so a
/// count below the real length would let a small request render into a large jsonb value.
/// </summary>
public sealed class DraftNumberRenderingTests
{
    // Plain integers, plain decimals (trailing zeros kept) and normalised exponent forms: the count is the rendered length.
    [Theory]
    [InlineData("0", 1)]
    [InlineData("5", 1)]
    [InlineData("123", 3)]
    [InlineData("12345678901234567890", 20)]
    [InlineData("0.5", 3)]
    [InlineData("1.50", 4)]
    [InlineData("-1.5", 4)]
    [InlineData("1.5e1", 2)]
    [InlineData("1e2", 3)]
    [InlineData("-1e2", 4)]
    [InlineData("1e+5", 6)]
    [InlineData("123e-5", 7)]
    [InlineData("1E-300", 302)]
    [InlineData("1e308", 309)]
    [InlineData("-1e308", 310)]
    [InlineData("1e400", 401)]
    [InlineData("1e-400", 402)]
    [InlineData("1234567890123456789012345678901234567890123456789012345678901234", 64)]
    public void RenderedLength_ForTheNormalForms_IsExact(string token, long rendered) =>
        DraftBody.RenderedLength(token).ShouldBe(rendered);

    // Leading zeros and a zero mantissa are the only forms counted above the rendered length (safe side).
    [Theory]
    [InlineData("0e3", 4)]
    [InlineData("0.0001e2", 6)]
    public void RenderedLength_ForLeadingZeroForms_NeverUndercounts(string token, long counted) =>
        DraftBody.RenderedLength(token).ShouldBe(counted);

    [Theory]
    [InlineData("1e401")]
    [InlineData("1e-401")]
    [InlineData("1e999999999")]
    [InlineData("1e-999999999999")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345")]
    public void RenderedLength_PastTheTokenOrExponentBound_IsRefused(string token) =>
        DraftBody.RenderedLength(token).ShouldBe(-1);
}
