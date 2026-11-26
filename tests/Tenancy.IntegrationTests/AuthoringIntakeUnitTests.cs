using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private static readonly JsonSerializerOptions SectionJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

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

    [Theory]
    [MemberData(nameof(AuthoringIntakeCapsJsonTests.InvalidMembers), MemberType = typeof(AuthoringIntakeCapsJsonTests))]
    public void Validate_InvalidMember_RefusesTheSection(string member, string value) =>
        Validator.Validate(null, Section(member, value)).Succeeded.ShouldBeFalse();

    [Theory]
    [MemberData(nameof(AuthoringIntakeCapsJsonTests.ValidBoundaries), MemberType = typeof(AuthoringIntakeCapsJsonTests))]
    public void Validate_ValidBoundary_Passes(string member, string value) =>
        Validator.Validate(null, Section(member, value)).Succeeded.ShouldBeTrue();

    private static IntakeCapsOptions Section(string member, string value) =>
        JsonSerializer.Deserialize<IntakeCapsOptions>(AuthoringIntakeCapsJsonTests.Document(member, value), SectionJson).ShouldNotBeNull();
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

    public static TheoryData<string, string> InvalidMembers => new()
    {
        { "max_document_bytes", "-1" },
        { "max_document_bytes", "0" },
        { "max_document_bytes", "1073741825" },
        { "max_image_bytes", "-1" },
        { "max_image_bytes", "0" },
        { "max_image_bytes", "1073741825" },
        { "max_zip_entries", "-1" },
        { "max_zip_entries", "0" },
        { "max_zip_entries", "100001" },
        { "max_compression_ratio", "-1" },
        { "max_compression_ratio", "0" },
        { "max_compression_ratio", "10001" },
        { "max_pdf_pages", "-1" },
        { "max_pdf_pages", "0" },
        { "max_pdf_pages", "100001" },
        { "max_extracted_chars", "-1" },
        { "max_extracted_chars", "0" },
        { "max_extracted_chars", "2000001" },
        { "max_text_code_points", "-1" },
        { "max_text_code_points", "0" },
        { "max_text_code_points", "1048577" },
        { "max_concurrent_extractions_per_tenant", "-1" },
        { "max_concurrent_extractions_per_tenant", "0" },
        { "max_concurrent_extractions_per_tenant", "65" },
        { "extraction_timeout", "\"00:00:00\"" },
        { "extraction_timeout", "\"-00:00:00.001\"" },
        { "extraction_timeout", "\"-00:00:05\"" },
        { "extraction_timeout", "\"-10675199.02:48:05.4775808\"" },
        { "extraction_timeout", "\"00:05:00.0000001\"" },
    };

    public static TheoryData<string, string> ValidBoundaries => new()
    {
        { "max_document_bytes", "1" },
        { "max_document_bytes", "1073741824" },
        { "max_image_bytes", "1" },
        { "max_image_bytes", "1073741824" },
        { "max_zip_entries", "1" },
        { "max_zip_entries", "100000" },
        { "max_compression_ratio", "1" },
        { "max_compression_ratio", "10000" },
        { "max_pdf_pages", "1" },
        { "max_pdf_pages", "100000" },
        { "max_extracted_chars", "1" },
        { "max_extracted_chars", "2000000" },
        { "max_text_code_points", "1" },
        { "max_text_code_points", "1048576" },
        { "max_concurrent_extractions_per_tenant", "1" },
        { "max_concurrent_extractions_per_tenant", "64" },
        { "extraction_timeout", "\"00:00:00.0000001\"" },
        { "extraction_timeout", "\"00:05:00\"" },
    };

    [Theory]
    [MemberData(nameof(InvalidMembers))]
    public void Deserialize_InvalidMember_ReturnsNull(string member, string value) =>
        IntakeCapsJson.Deserialize(Document(member, value)).ShouldBeNull();

    [Theory]
    [InlineData("max_compression_ratio", "\"wrong type\"")]
    [InlineData("max_document_bytes", "null")]
    [InlineData("extraction_timeout", "5")]
    public void Deserialize_WrongMemberType_ReturnsNull(string member, string value) =>
        IntakeCapsJson.Deserialize(Document(member, value)).ShouldBeNull();

    [Fact]
    public void Deserialize_TextLimitAboveExtractedLimit_ReturnsNull() =>
        IntakeCapsJson.Deserialize(IntakeCapsJson.Serialize(
            IntakeCaps.Default with { MaxExtractedChars = 1, MaxTextCodePoints = 2 })).ShouldBeNull();

    [Fact]
    public void Deserialize_ExtraMember_KeepsKnownCaps() =>
        IntakeCapsJson.Deserialize(Document("future_limit", "42")).ShouldBe(
            IntakeCaps.Default with { MaxExtractedChars = 2_000_000, MaxTextCodePoints = 1 });

    [Theory]
    [MemberData(nameof(ValidBoundaries))]
    public void Deserialize_ValidBoundary_ReadsTheDocument(string member, string value) =>
        IntakeCapsJson.Deserialize(Document(member, value)).ShouldNotBeNull();

    internal static string Document(string member, string value)
    {
        var caps = IntakeCaps.Default with { MaxExtractedChars = 2_000_000, MaxTextCodePoints = 1 };
        var document = JsonNode.Parse(IntakeCapsJson.Serialize(caps)).ShouldNotBeNull().AsObject();
        document[member] = JsonNode.Parse(value);
        return document.ToJsonString();
    }
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
