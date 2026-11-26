using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.Modules.Authoring.Application.Intake;
using Evidgate.Modules.Authoring.Infrastructure.Intake.Jobs;
using Evidgate.Modules.Evidence.Contracts;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>The caps section may never state a limit the intake row refuses: the row's own CHECKs are the ceiling.</summary>
public sealed class AuthoringIntakeCapsValidatorTests
{
    private static readonly IntakeCapsOptionsValidator Validator = new();

    [Fact]
    public void Validate_DefaultSection_Passes() =>
        Validator.Validate(null, new IntakeCapsOptions()).Succeeded.ShouldBeTrue();

    [Theory]
    [InlineData(2_000_000, true)]
    [InlineData(2_000_001, false)]
    [InlineData(100_000_000, false)]
    public void Validate_MaxExtractedChars_StopsAtTheStoredTextCheck(int value, bool valid) =>
        Validator.Validate(null, new IntakeCapsOptions { MaxExtractedChars = value }).Succeeded.ShouldBe(valid);

    [Theory]
    [InlineData(1_048_576, true)]
    [InlineData(1_048_577, false)]
    public void Validate_MaxTextCodePoints_StopsAtTheInputTextCheck(int value, bool valid) =>
        Validator.Validate(null, new IntakeCapsOptions { MaxExtractedChars = 2_000_000, MaxTextCodePoints = value }).Succeeded.ShouldBe(valid);
}

/// <summary>A stored caps document is either complete or unreadable: a partial one never becomes zero limits.</summary>
public sealed class AuthoringIntakeCapsJsonTests
{
    [Fact]
    public void Deserialize_SerializedDefaults_ReadsTheSameCaps() =>
        IntakeCapsJson.Deserialize(IntakeCapsJson.Serialize(IntakeCaps.Default)).ShouldBe(IntakeCaps.Default);

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"max_document_bytes": 10}""")]
    [InlineData("not json")]
    public void Deserialize_IncompleteOrMalformedDocument_ReturnsNull(string json) =>
        IntakeCapsJson.Deserialize(json).ShouldBeNull();
}

/// <summary>The Worker-wide parser permits: a taken permit stays taken until it is given back.</summary>
public sealed class IntakeExecutionPermitsTests
{
    [Fact]
    public void TryEnter_AllPermitsTaken_RefusesUntilOneIsGivenBack()
    {
        var permits = new IntakeExecutionPermits(Options.Create(new IntakeExecutionOptions { MaxConcurrentParsers = 2 }));

        permits.TryEnter().ShouldBeTrue();
        permits.TryEnter().ShouldBeTrue();
        permits.TryEnter().ShouldBeFalse();

        permits.Exit();

        permits.TryEnter().ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void IsValid_ChecksTheRange(int value, bool valid) =>
        IntakeExecutionOptions.IsValid(new IntakeExecutionOptions { MaxConcurrentParsers = value }).ShouldBe(valid);
}

/// <summary>A failed lease renewal or release is left to lease expiry, but it is counted, never swallowed silently.</summary>
public sealed class AuthoringIntakeSlotFailureTests
{
    [Theory]
    [InlineData("renew", "42501", "privilege")]
    [InlineData("release", "42501", "privilege")]
    [InlineData("renew", "57014", "other")]
    public async Task QuietAsync_FailedStep_IsCountedWithAClosedOperationAndReason(string operation, string sqlState, string reason)
    {
        var seen = new ConcurrentBag<(string? Operation, string? Reason)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, subscriber) =>
        {
            if (instrument.Name == EvidgateTelemetry.AuthoringIntakeSlotFailureMetricName)
            {
                subscriber.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? op = null;
            string? why = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "operation")
                {
                    op = tag.Value as string;
                }
                else if (tag.Key == "reason")
                {
                    why = tag.Value as string;
                }
            }

            seen.Add((op, why));
        });
        listener.Start();

        await ExtractIntakeJob.QuietAsync(
            operation,
            _ => throw new PostgresException("refused", "ERROR", "ERROR", sqlState));

        seen.ShouldContain((operation, reason));
    }

    [Fact]
    public async Task QuietAsync_SucceedingStep_RunsItAndDoesNotThrow()
    {
        var ran = false;

        await ExtractIntakeJob.QuietAsync(ExtractIntakeJob.RenewStep, _ =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        ran.ShouldBeTrue();
    }
}

/// <summary>The one media-type comparison every upload and intake path shares.</summary>
public sealed class MediaTypesTests
{
    private static readonly string[] Allowed = ["application/pdf", "image/png"];

    [Theory]
    [InlineData("application/pdf", "application/pdf")]
    [InlineData("APPLICATION/PDF", "application/pdf")]
    [InlineData("application/pdf; charset=binary", "application/pdf")]
    [InlineData("Image/PNG;x=1", "image/png")]
    public void Match_AcceptedSpelling_ReturnsTheCanonicalType(string declared, string canonical) =>
        MediaTypes.Match(Allowed, declared).ShouldBe(canonical);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" application/pdf")]
    [InlineData("application/pdf ")]
    [InlineData("application/pdf\n")]
    [InlineData("application/pdf\0")]
    [InlineData("application/pdf\u200b")]
    [InlineData("application/pdf\n; charset=binary")]
    [InlineData("application/pdfx")]
    public void Match_RefusedSpelling_ReturnsNull(string? declared) =>
        MediaTypes.Match(Allowed, declared).ShouldBeNull();

    [Theory]
    [InlineData("application/pdf", "APPLICATION/PDF; charset=binary", true)]
    [InlineData("application/pdf", "image/png", false)]
    [InlineData("application/pdf", "application/pdf\n", false)]
    [InlineData(null, "application/pdf", false)]
    public void Equivalent_ComparesTheBareTypeIgnoringCase(string? left, string? right, bool expected) =>
        MediaTypes.Equivalent(left, right).ShouldBe(expected);
}
