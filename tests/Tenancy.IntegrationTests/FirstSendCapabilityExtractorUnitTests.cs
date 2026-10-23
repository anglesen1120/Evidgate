using System.Text.Json;
using Evidgate.Modules.Ingestion.Domain;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 04.7.1-T2.3 without a database: the pure first-send extractor over the first
/// envelope of a run — carrier detection, the adapter name from the <c>ext.schema</c>
/// prefix, and the <c>/ext/...</c> detail paths of a malformed carrier.
/// </summary>
public sealed class FirstSendCapabilityExtractorUnitTests
{
    /// <summary>Parses one envelope literal into the element the extractor reads.</summary>
    private static JsonElement Envelope(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Extract_ValidCarrier_TakesNameFromSchema()
    {
        var extraction = FirstSendCapabilityExtractor.Extract(Envelope(
            """
            {"type":"run.log","sequence":1,"ext":{"schema":"playwright/1","capability":{"protocol":{"major":1,"minor":0}}}}
            """));

        extraction.IsCarrier.ShouldBeTrue();
        extraction.AdapterName.ShouldBe("playwright");
        extraction.Errors.ShouldBeEmpty();
        extraction.Document.ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Theory]
    // A type other than run.log carries no capability.
    [InlineData("""{"type":"run.step","sequence":1,"ext":{"schema":"playwright/1","capability":{"protocol":{"major":1,"minor":0}}}}""")]
    // Only the first envelope (sequence 1) is the capability carrier.
    [InlineData("""{"type":"run.log","sequence":2,"ext":{"schema":"playwright/1","capability":{"protocol":{"major":1,"minor":0}}}}""")]
    // No ext.capability member: nothing to register.
    [InlineData("""{"type":"run.log","sequence":1,"ext":{"schema":"playwright/1"}}""")]
    // No ext member at all.
    [InlineData("""{"type":"run.log","sequence":1}""")]
    public void Extract_NonCarrierKinds_IsNotACarrier(string envelope)
    {
        var extraction = FirstSendCapabilityExtractor.Extract(Envelope(envelope));

        extraction.IsCarrier.ShouldBeFalse();
        extraction.AdapterName.ShouldBeNull();
        extraction.Errors.ShouldBeEmpty();
    }

    [Theory]
    // Uppercase name is outside the ^[a-z0-9][a-z0-9._-]{0,63}$ shape.
    [InlineData("""{"type":"run.log","sequence":1,"ext":{"schema":"Playwright/1","capability":{}}}""")]
    // No slash: the major is missing.
    [InlineData("""{"type":"run.log","sequence":1,"ext":{"schema":"playwright","capability":{}}}""")]
    // A non-numeric major.
    [InlineData("""{"type":"run.log","sequence":1,"ext":{"schema":"playwright/x","capability":{}}}""")]
    // An empty schema.
    [InlineData("""{"type":"run.log","sequence":1,"ext":{"schema":"","capability":{}}}""")]
    // A schema that is not a string.
    [InlineData("""{"type":"run.log","sequence":1,"ext":{"schema":7,"capability":{}}}""")]
    // No schema member.
    [InlineData("""{"type":"run.log","sequence":1,"ext":{"capability":{}}}""")]
    public void Extract_BadSchema_ReportsExtSchemaDetail(string envelope)
    {
        var extraction = FirstSendCapabilityExtractor.Extract(Envelope(envelope));

        extraction.IsCarrier.ShouldBeFalse();
        extraction.Errors.ShouldHaveSingleItem().Path.ShouldBe("/ext/schema");
    }

    [Theory]
    // ext.capability must be an object (the capability.v1 document root).
    [InlineData("""{"type":"run.log","sequence":1,"ext":{"schema":"playwright/1","capability":"text"}}""")]
    [InlineData("""{"type":"run.log","sequence":1,"ext":{"schema":"playwright/1","capability":5}}""")]
    [InlineData("""{"type":"run.log","sequence":1,"ext":{"schema":"playwright/1","capability":[]}}""")]
    [InlineData("""{"type":"run.log","sequence":1,"ext":{"schema":"playwright/1","capability":null}}""")]
    public void Extract_CapabilityNotAnObject_ReportsExtCapabilityDetail(string envelope)
    {
        var extraction = FirstSendCapabilityExtractor.Extract(Envelope(envelope));

        extraction.IsCarrier.ShouldBeFalse();
        extraction.Errors.ShouldHaveSingleItem().Path.ShouldBe("/ext/capability");
    }
}
