using System.Diagnostics;
using Evidgate.Modules.Tenancy.Application;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The three request-path shapes under inputs far past their caps: a value of 10^5 or 10^6
/// characters is refused by one length check, so no host load can turn the refusal into an
/// exception the way the timed <c>[GeneratedRegex]</c> could.
/// </summary>
public sealed class TenancyRulesHostileInputTests
{
    private const int HundredThousand = 100_000;
    private const int OneMillion = 1_000_000;

    private static readonly string[] HostileSlugs =
    [
        new string('a', HundredThousand),
        new string('a', OneMillion),
        new string('a', HundredThousand - 1) + "-",
        new string('a', HundredThousand - 1) + "\0",
    ];

    private static readonly string[] HostileCasePrefixes =
    [
        new string('A', HundredThousand),
        new string('A', OneMillion),
        new string('A', HundredThousand - 1) + "\n",
        new string('A', HundredThousand - 1) + "\u0430",
    ];

    private static readonly string[] HostileDomains =
    [
        new string('a', HundredThousand),
        new string('a', OneMillion),
        string.Join('.', Enumerable.Repeat(new string('a', 63), 1_000)),
        new string('a', 253) + "." + new string('a', 253),
    ];

    [Fact]
    public void Slug_HugeValues_AreRefusedWithinOneSecond() =>
        RefusesFast(TenancyRules.IsSlug, "ab", HostileSlugs);

    [Fact]
    public void CasePrefix_HugeValues_AreRefusedWithinOneSecond() =>
        RefusesFast(TenancyRules.IsCasePrefix, "AB", HostileCasePrefixes);

    [Fact]
    public void Domain_HugeValues_AreRefusedWithinOneSecond() =>
        RefusesFast(TenancyRules.IsDomain, "acme.example", HostileDomains);

    /// <summary>Each hostile value is refused in bounded time; the control shows only the value changed.</summary>
    private static void RefusesFast(Func<string?, bool> shape, string accepted, IReadOnlyList<string> hostile)
    {
        shape(accepted).ShouldBeTrue("the control value is accepted, so only the hostile value differs");

        foreach (var value in hostile)
        {
            var stopwatch = Stopwatch.StartNew();
            shape(value).ShouldBeFalse("a value past the cap is refused");
            stopwatch.Stop();

            stopwatch.Elapsed.ShouldBeLessThan(
                TimeSpan.FromSeconds(1),
                "the refusal is one length check with no regex engine and no timeout");
        }
    }
}
