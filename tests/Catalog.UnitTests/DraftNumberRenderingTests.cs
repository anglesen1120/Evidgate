using System.Text;
using System.Text.Json;
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
        DraftBody.RenderedLength(Encoding.UTF8.GetBytes(token)).ShouldBe(rendered);

    // Leading zeros and a zero mantissa are the only forms counted above the rendered length (safe side).
    [Theory]
    [InlineData("0e3", 4)]
    [InlineData("0.0001e2", 6)]
    public void RenderedLength_ForLeadingZeroForms_NeverUndercounts(string token, long counted) =>
        DraftBody.RenderedLength(Encoding.UTF8.GetBytes(token)).ShouldBe(counted);

    [Theory]
    [InlineData("1e401")]
    [InlineData("1e-401")]
    [InlineData("1e999999999")]
    [InlineData("1e-999999999999")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345")]
    public void RenderedLength_PastTheTokenOrExponentBound_IsRefused(string token) =>
        DraftBody.RenderedLength(Encoding.UTF8.GetBytes(token)).ShouldBe(-1);

    [Theory]
    [InlineData(61, 64, false)]
    [InlineData(62, 65, true)]
    public void RenderedLength_OfAnExponentTokenAtTheByteBound_FollowsTheLength(int digits, int length, bool refused)
    {
        var token = new string('7', digits) + "e+0";
        token.Length.ShouldBe(length);
        (DraftBody.RenderedLength(Encoding.UTF8.GetBytes(token)) < 0).ShouldBe(refused);
    }

    [Fact]
    public void RenderedLength_OfA64CharacterToken_IsAcceptedAndOf65IsRefused()
    {
        DraftBody.RenderedLength(Encoding.UTF8.GetBytes(new string('7', 64))).ShouldBe(64);
        DraftBody.RenderedLength(Encoding.UTF8.GetBytes(new string('7', 65))).ShouldBe(-1);
    }

    // An over-long token is refused from its raw UTF-8 length: a string of it (two bytes a character, 400 KiB here)
    // would show in the thread's allocation counter, so the bound below pins the order of the check.
    [Fact]
    public void ExceedsNumericRange_OfA200KiBNumberToken_AllocatesNoStringForIt()
    {
        using var document = JsonDocument.Parse("{\"n\":" + new string('7', 200 * 1024) + "}");
        var element = document.RootElement;
        DraftBody.ExceedsNumericRange(element).ShouldBeTrue();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var refused = DraftBody.ExceedsNumericRange(element);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        refused.ShouldBeTrue();
        allocated.ShouldBeLessThan(4096, $"allocated {allocated} bytes for a 200 KiB token");
    }

    [Fact]
    public void ExceedsNumericRange_OfA64CharacterToken_IsWithinRange() =>
        DraftBody.ExceedsNumericRange(JsonDocument.Parse("{\"n\":" + new string('7', 64) + "}").RootElement).ShouldBeFalse();
}
