using System.Text;

namespace Evidgate.Modules.Healing.Domain;

/// <summary>The kinds of AI credit entry; <see cref="CreditMath.Wire"/> maps each to its column value.</summary>
public enum CreditEntryKind
{
    /// <summary>Credit added to the available balance.</summary>
    Grant,

    /// <summary>Credit held for a call in flight: available moves to reserved.</summary>
    Reserve,

    /// <summary>A call's actual cost: the credit leaves the reserved part for good.</summary>
    Consume,

    /// <summary>A reserve given back: reserved moves to available.</summary>
    Refund,

    /// <summary>A grant that lapsed: credit leaves the available balance.</summary>
    Expire,

    /// <summary>An operator correction: the one signed kind.</summary>
    Adjust,
}

/// <summary>An account's two parts, both in micro-units and never negative.</summary>
/// <param name="AvailableMicro">What a new reserve may draw on.</param>
/// <param name="ReservedMicro">What calls in flight hold.</param>
internal readonly record struct CreditBalance(long AvailableMicro, long ReservedMicro);

/// <summary>
/// The one definition of what an entry does to an account (integer micro-units, no floating point). The writer applies
/// the same function to the stored account, and the tests fold an entry list through it, so the log and the balance
/// cannot disagree about a kind.
/// </summary>
internal static class CreditMath
{
    /// <summary>The longest <c>reason</c> an entry holds, in UTF-16 units (the column bound).</summary>
    internal const int MaxReasonLength = 500;

    /// <summary>The longest caller key of a call-less entry, which keeps the whole billing key inside 200 characters.</summary>
    internal const int MaxIdempotencyKeyLength = 150;

    /// <summary>The column value of <paramref name="kind"/>.</summary>
    /// <param name="kind">A declared kind.</param>
    internal static string Wire(CreditEntryKind kind) => kind switch
    {
        CreditEntryKind.Grant => "grant",
        CreditEntryKind.Reserve => "reserve",
        CreditEntryKind.Consume => "consume",
        CreditEntryKind.Refund => "refund",
        CreditEntryKind.Expire => "expire",
        CreditEntryKind.Adjust => "adjust",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a credit entry kind"),
    };

    /// <summary>True for the kinds that belong to one call (<c>call_id</c> set).</summary>
    /// <param name="kind">A declared kind.</param>
    internal static bool IsCallBound(CreditEntryKind kind) =>
        kind is CreditEntryKind.Reserve or CreditEntryKind.Consume or CreditEntryKind.Refund;

    /// <summary>True when <paramref name="amountMicro"/> is a legal amount for <paramref name="kind"/>.</summary>
    /// <param name="kind">A declared kind.</param>
    /// <param name="amountMicro">The amount: positive, except an adjustment, which is non-zero.</param>
    internal static bool IsValidAmount(CreditEntryKind kind, long amountMicro) =>
        kind == CreditEntryKind.Adjust ? amountMicro != 0 && amountMicro != long.MinValue : amountMicro > 0;

    /// <summary>
    /// Applies one entry to <paramref name="balance"/>. It refuses (false) an amount illegal for the kind, an overflow of
    /// either part, and any result that would leave a part below zero, so a refused entry changes nothing.
    /// </summary>
    /// <param name="balance">The account before the entry.</param>
    /// <param name="kind">The entry kind.</param>
    /// <param name="amountMicro">The entry amount.</param>
    /// <param name="next">The account after the entry when it is applied.</param>
    internal static bool TryApply(CreditBalance balance, CreditEntryKind kind, long amountMicro, out CreditBalance next)
    {
        next = balance;

        if (!IsValidAmount(kind, amountMicro) || balance.AvailableMicro < 0 || balance.ReservedMicro < 0)
        {
            return false;
        }

        var (available, reserved) = Deltas(kind, amountMicro);
        var availableAfter = balance.AvailableMicro + available;
        var reservedAfter = balance.ReservedMicro + reserved;

        if (availableAfter < 0 || reservedAfter < 0 || availableAfter > long.MaxValue || reservedAfter > long.MaxValue)
        {
            return false;
        }

        next = new CreditBalance((long)availableAfter, (long)reservedAfter);
        return true;
    }

    /// <summary>The effect of one valid entry on each part, widened to <see cref="decimal"/> so no sum can wrap.</summary>
    /// <param name="kind">The entry kind.</param>
    /// <param name="amountMicro">The entry amount.</param>
    internal static (decimal Available, decimal Reserved) Deltas(CreditEntryKind kind, long amountMicro)
    {
        decimal amount = amountMicro;

        return kind switch
        {
            CreditEntryKind.Grant => (amount, 0m),
            CreditEntryKind.Reserve => (-amount, amount),
            CreditEntryKind.Consume => (0m, -amount),
            CreditEntryKind.Refund => (amount, -amount),
            CreditEntryKind.Expire => (-amount, 0m),
            CreditEntryKind.Adjust => (amount, 0m),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a credit entry kind"),
        };
    }

    /// <summary>
    /// True when <paramref name="value"/> holds no NUL and no unpaired surrogate, which PostgreSQL <c>text</c> cannot store.
    /// </summary>
    /// <param name="value">The candidate text.</param>
    internal static bool IsStorable(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\0' || !Rune.TryGetRuneAt(value, index, out var rune))
            {
                return false;
            }

            if (!rune.IsBmp)
            {
                index++;
            }
        }

        return true;
    }
}
