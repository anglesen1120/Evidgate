using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text.RegularExpressions;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.SharedKernel.Security;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// A secret-shape pattern that runs out of its match budget must answer "secret-shaped" (the refusal every caller
/// already has), never throw and never pass the value as clean. The timeout is forced through the internal matcher
/// seam, so no pathological input and no clock is involved. Pure.
/// </summary>
public sealed class SecretShapeTimeoutTests
{
    // Clean for every pattern: no marker, no prefix, mixed letters and digits below the hex length, so it reaches the
    // random-run and uuid patterns.
    private const string RandomLooking = "abcdefgh1234567890abcdefgh";

    private const string PlainWord = "hello-world";

    private const string LongNonHex = "abcdefghij klmnopqrstuvwxyz012345";

    private static Func<SecretPattern, string, bool> TimesOutOn(SecretPattern target, bool otherwise = false) =>
        (pattern, text) => pattern == target
            ? throw new RegexMatchTimeoutException(string.Empty, pattern.ToString(), TimeSpan.FromMilliseconds(100))
            : otherwise;

    [Theory]
    [InlineData("Jwt", PlainWord)]
    [InlineData("UrlCredentials", PlainWord)]
    [InlineData("HexRun", LongNonHex)]
    [InlineData("RandomRun", RandomLooking)]
    [InlineData("Uuid", RandomLooking)]
    public void TimedOutPattern_AnswersSecretShaped_ForAValueThatIsOtherwiseClean(string targetName, string value)
    {
        var target = Enum.Parse<SecretPattern>(targetName);
        // Baseline: every pattern finishes. For the uuid case the value is a "uuid" after a random-run match, so it is clean.
        bool Finishes(SecretPattern pattern, string text) =>
            pattern == SecretPattern.Uuid || (target == SecretPattern.Uuid && pattern == SecretPattern.RandomRun);

        SecretShape.ValueLooksSecret(value, Finishes).ShouldBeFalse();

        SecretShape.ValueLooksSecret(
            value,
            (pattern, text) => pattern == target
                ? throw new RegexMatchTimeoutException(string.Empty, pattern.ToString(), TimeSpan.FromMilliseconds(100))
                : Finishes(pattern, text)).ShouldBeTrue();
    }

    [Fact]
    public void OtherExceptions_AreNotSwallowed()
    {
        Should.Throw<InvalidOperationException>(
            () => SecretShape.ValueLooksSecret(PlainWord, (_, _) => throw new InvalidOperationException("boom")));
    }

    [Fact]
    public void ShortValue_NeverRunsAPattern()
    {
        SecretShape.ValueLooksSecret("short", (_, _) => throw new InvalidOperationException("no pattern may run")).ShouldBeFalse();
    }

    [Fact]
    public void Timeout_IsCountedOncePerCheck_WithThePatternAndNeverTheValue()
    {
        var measurements = new List<(long Value, string Pattern, List<string> TagValues)>();
        using var listener = Listen(measurements);

        SecretShape.ValueLooksSecret(RandomLooking, TimesOutOn(SecretPattern.RandomRun)).ShouldBeTrue();

        var one = measurements.ShouldHaveSingleItem();
        one.Value.ShouldBe(1);
        one.Pattern.ShouldBe("random_run");
        one.TagValues.ShouldAllBe(tag => !tag.Contains("abcdefgh", StringComparison.Ordinal));
    }

    [Fact]
    public void NoTimeout_RecordsNothing()
    {
        var measurements = new List<(long Value, string Pattern, List<string> TagValues)>();
        using var listener = Listen(measurements);

        SecretShape.ValueLooksSecret("ghp_abcdefghijklmnopqrstuvwxyz0123456789").ShouldBeTrue();
        SecretShape.ValueLooksSecret(PlainWord).ShouldBeFalse();

        measurements.ShouldBeEmpty();
    }

    [Fact]
    public void CounterMeter_IsTheApplicationMeter()
    {
        // The counter is published from a block that cannot reference Observability, so the name is repeated there.
        MetricsType().GetField("MeterName", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()
            .ShouldBe(EvidgateTelemetry.MeterName);
    }

    [Fact]
    public void TheSeam_IsNotPartOfThePublicSurface()
    {
        var publicOverloads = typeof(SecretShape)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == nameof(SecretShape.ValueLooksSecret));

        publicOverloads.ShouldHaveSingleItem().GetParameters().Length.ShouldBe(1);
        MetricsType().IsPublic.ShouldBeFalse();
        MetricsType().Assembly.GetType("Evidgate.BuildingBlocks.SharedKernel.Security.SecretPattern")!.IsPublic.ShouldBeFalse();
    }

    private static Type MetricsType() =>
        typeof(SecretShape).Assembly.GetType("Evidgate.BuildingBlocks.SharedKernel.Security.SecretShapeMetrics")!;

    private static MeterListener Listen(List<(long Value, string Pattern, List<string> TagValues)> sink)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == EvidgateTelemetry.MeterName && instrument.Name == "evidgate_secret_shape_timeout_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var values = new List<string>();
            var pattern = string.Empty;
            foreach (var tag in tags)
            {
                values.Add(tag.Value?.ToString() ?? string.Empty);
                if (tag.Key == "pattern")
                {
                    pattern = tag.Value?.ToString() ?? string.Empty;
                }
            }

            lock (sink)
            {
                sink.Add((value, pattern, values));
            }
        });
        listener.Start();

        return listener;
    }
}
