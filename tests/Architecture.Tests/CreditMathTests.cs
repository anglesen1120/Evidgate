using Evidgate.Modules.Healing.Domain;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>The integer credit arithmetic of 0135: signs, bounds, and the sum of entries equalling the account.</summary>
public class CreditMathTests
{
    private static readonly CreditEntryKind[] Kinds = Enum.GetValues<CreditEntryKind>();

    /// <summary>The account a list of applied entries describes, computed from the definition and not from the code under test.</summary>
    private static (long Available, long Reserved) Model(IEnumerable<(CreditEntryKind Kind, long Amount)> applied)
    {
        long available = 0;
        long reserved = 0;

        foreach (var (kind, amount) in applied)
        {
            switch (kind)
            {
                case CreditEntryKind.Grant:
                    available += amount;
                    break;
                case CreditEntryKind.Reserve:
                    available -= amount;
                    reserved += amount;
                    break;
                case CreditEntryKind.Consume:
                    reserved -= amount;
                    break;
                case CreditEntryKind.Refund:
                    available += amount;
                    reserved -= amount;
                    break;
                case CreditEntryKind.Expire:
                    available -= amount;
                    break;
                case CreditEntryKind.Adjust:
                    available += amount;
                    break;
            }
        }

        return (available, reserved);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void CreditMath_SumEqualsBalance(int seed)
    {
        var random = new Random(seed);
        var balance = new CreditBalance(0, 0);
        var applied = new List<(CreditEntryKind, long)>();
        var refused = 0;

        for (var step = 0; step < 2000; step++)
        {
            var kind = Kinds[random.Next(Kinds.Length)];
            var amount = random.NextInt64(1, 10_000) * (kind == CreditEntryKind.Adjust && random.Next(2) == 0 ? -1 : 1);

            if (CreditMath.TryApply(balance, kind, amount, out var next))
            {
                applied.Add((kind, amount));
                balance = next;
            }
            else
            {
                refused++;
                next.ShouldBe(balance);
            }

            balance.AvailableMicro.ShouldBeGreaterThanOrEqualTo(0);
            balance.ReservedMicro.ShouldBeGreaterThanOrEqualTo(0);
        }

        var (available, reserved) = Model(applied);
        balance.ShouldBe(new CreditBalance(available, reserved));
        applied.Count.ShouldBeGreaterThan(100);
        refused.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData(CreditEntryKind.Grant, 0, false)]
    [InlineData(CreditEntryKind.Grant, -5, false)]
    [InlineData(CreditEntryKind.Reserve, 0, false)]
    [InlineData(CreditEntryKind.Consume, -1, false)]
    [InlineData(CreditEntryKind.Adjust, 0, false)]
    [InlineData(CreditEntryKind.Adjust, long.MinValue, false)]
    [InlineData(CreditEntryKind.Adjust, -7, true)]
    [InlineData(CreditEntryKind.Adjust, 7, true)]
    [InlineData(CreditEntryKind.Grant, long.MaxValue, true)]
    public void IsValidAmount_FollowsTheKind(CreditEntryKind kind, long amount, bool expected) =>
        CreditMath.IsValidAmount(kind, amount).ShouldBe(expected);

    [Fact]
    public void Grant_AtTheTopOfTheRange_IsRefusedInsteadOfWrapping()
    {
        var full = new CreditBalance(long.MaxValue, 0);

        CreditMath.TryApply(full, CreditEntryKind.Grant, 1, out var next).ShouldBeFalse();
        next.ShouldBe(full);
        CreditMath.TryApply(full, CreditEntryKind.Adjust, 1, out _).ShouldBeFalse();
        CreditMath.TryApply(new CreditBalance(0, long.MaxValue), CreditEntryKind.Refund, 1, out var reduced).ShouldBeTrue();
        reduced.ShouldBe(new CreditBalance(1, long.MaxValue - 1));
    }

    [Fact]
    public void Refund_AtTheTopOfTheAvailablePart_IsRefused()
    {
        // A refund moves credit from reserved to available; the sum no longer fits the available part.
        CreditMath.TryApply(new CreditBalance(long.MaxValue, 5), CreditEntryKind.Refund, 1, out _).ShouldBeFalse();
    }

    [Fact]
    public void Reserve_CannotOverdraw_AndConsumeCannotExceedWhatIsReserved()
    {
        var account = new CreditBalance(100, 0);

        CreditMath.TryApply(account, CreditEntryKind.Reserve, 101, out _).ShouldBeFalse();
        CreditMath.TryApply(account, CreditEntryKind.Reserve, 100, out var held).ShouldBeTrue();
        held.ShouldBe(new CreditBalance(0, 100));
        CreditMath.TryApply(held, CreditEntryKind.Consume, 101, out _).ShouldBeFalse();
        CreditMath.TryApply(held, CreditEntryKind.Consume, 100, out var spent).ShouldBeTrue();
        spent.ShouldBe(new CreditBalance(0, 0));
        CreditMath.TryApply(account, CreditEntryKind.Adjust, -101, out _).ShouldBeFalse();
    }

    [Fact]
    public void Wire_NamesMatchTheColumnCheck()
    {
        Kinds.Select(CreditMath.Wire).ShouldBe(["grant", "reserve", "consume", "refund", "expire", "adjust"]);
        Kinds.Where(CreditMath.IsCallBound).ShouldBe([CreditEntryKind.Reserve, CreditEntryKind.Consume, CreditEntryKind.Refund]);
    }

    [Theory]
    [InlineData("plain", true)]
    [InlineData("emoji \U0001F600 pair", true)]
    [InlineData("nul\0inside", false)]
    [InlineData("lone \uD83D high", false)]
    [InlineData("lone \uDE00 low", false)]
    public void IsStorable_RefusesNulAndUnpairedSurrogates(string text, bool expected) =>
        CreditMath.IsStorable(text).ShouldBe(expected);
}
