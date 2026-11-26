using Evidgate.Modules.Authoring.Application.Intake;
using Shouldly;
using UglyToad.PdfPig;
using Xunit;
using static Tenancy.IntegrationTests.AuthoringExtractionSafetyTests;
using Fx = Tenancy.IntegrationTests.AuthoringExtractionFixtures;

namespace Tenancy.IntegrationTests;

/// <summary>
/// PDF text extraction and the refusal of encrypted, active or file-carrying PDFs, with a positive control for each
/// refusal. In-memory fixtures and the real parser profile, no Docker.
/// </summary>
public sealed class AuthoringPdfSafetyTests
{
    private const string BodyWithMarkerLines = "(hello\nendstream\nendobj\n/Launch) Tj";

    [Fact]
    public void Pdf_TwoPages_ReturnedInOrder()
    {
        var outcome = ExtractPdf(Fx.Pdf(["Login must lock after five failures", "Second page text"]));

        outcome.Succeeded.ShouldBeTrue();
        outcome.Text.ShouldBe("Login must lock after five failures\nSecond page text");
    }

    [Fact]
    public void Pdf_NoText_IsRefused()
    {
        ShouldBeRefused(ExtractPdf(Fx.Pdf([string.Empty])), IntakeFailureCode.NoExtractableText);
    }

    [Fact]
    public void Pdf_NotAPdfBody_IsRefusedWithAClosedCode()
    {
        var outcome = ExtractPdf(System.Text.Encoding.ASCII.GetBytes("%PDF-1.7\nthis is not a pdf at all"));

        outcome.Succeeded.ShouldBeFalse();
        outcome.FailureCode.ShouldBeOneOf(IntakeFailureCode.ExtractionFailed, IntakeFailureCode.NoExtractableText);
    }

    [Fact]
    public void Pdf_PageCap_AtCapAccepted_OneOverRefused()
    {
        var caps = IntakeCaps.Default with { MaxPdfPages = 2 };

        ExtractPdf(Fx.Pdf(["one", "two"]), caps).Text.ShouldBe("one\ntwo");
        ShouldBeRefused(ExtractPdf(Fx.Pdf(["one", "two", "three"]), caps), IntakeFailureCode.PageLimitExceeded);
    }

    [Fact]
    public void Pdf_CharCap_AtCapAccepted_OneOverRefused()
    {
        var caps = IntakeCaps.Default with { MaxExtractedChars = 10 };

        ExtractPdf(Fx.Pdf(["ABCDEFGHIJ"]), caps).Text.ShouldBe("ABCDEFGHIJ");
        ShouldBeRefused(ExtractPdf(Fx.Pdf(["ABCDEFGHIJK"]), caps), IntakeFailureCode.CharLimitExceeded);
    }

    [Fact]
    public void EncryptedPdf_Rejected()
    {
        var encryption = $"<< /Filter /Standard /V 1 /R 2 /O ({new string('a', 32)}) /U ({new string('b', 32)}) /P -4 >>";
        var encrypted = Fx.Pdf(["secret"], trailerExtra: $"/Encrypt {Fx.FirstExtraObject(1)} 0 R", extraObjects: [encryption]);

        ShouldBeRefused(ExtractPdf(encrypted), IntakeFailureCode.EncryptionUnsupported);
        ExtractPdf(Fx.Pdf(["secret"])).Text.ShouldBe("secret");
    }

    [Theory]
    [InlineData("/OpenAction << /S /JavaScript /JS (x) >>", "", IntakeFailureCode.MacroPresent)]
    [InlineData("", "/AA << /O << /S /GoTo >> >>", IntakeFailureCode.MacroPresent)]
    [InlineData("/Names << /JavaScript << /Names [(a) << /S /JavaScript /JS (x) >>] >> >>", "", IntakeFailureCode.MacroPresent)]
    [InlineData("/Other << /S /Launch /F (calc.exe) >>", "", IntakeFailureCode.MacroPresent)]
    [InlineData("/Names << /EmbeddedFiles << /Names [(a.txt) << /Type /Filespec /F (a.txt) >>] >> >>", "", IntakeFailureCode.ExternalReferencePresent)]
    [InlineData("/Other /EF", "", IntakeFailureCode.ExternalReferencePresent)]
    [InlineData("/Other /FileAttachment", "", IntakeFailureCode.ExternalReferencePresent)]
    [InlineData("/Other /Filespec", "", IntakeFailureCode.ExternalReferencePresent)]
    public void Pdf_ActiveOrCarriedContent_Rejected(string catalogExtra, string pageExtra, string expectedCode)
    {
        var hostile = Fx.Pdf(["hello"], catalogExtra, pageExtra);

        ShouldBeRefused(ExtractPdf(hostile), expectedCode);
        ExtractPdf(Fx.Pdf(["hello"])).Text.ShouldBe("hello");
    }

    [Theory]
    [InlineData("/S /Launch")]
    [InlineData("/OpenActio#6E << /S /GoTo >>")]
    [InlineData("/S /Launc#68 /F (calc.exe)")]
    public void Pdf_DirectAndIndirectActions_RawAndStructuralScansRefuse(string action)
    {
        var direct = Fx.Pdf(["hello"], catalogExtra: $"/Other << {action} >>");
        var indirect = Fx.Pdf(["hello"], catalogExtra: $"/Other {Fx.FirstExtraObject(1)} 0 R", extraObjects: [$"<< {action} >>"]);

        ShouldBeRefused(ExtractPdf(direct), IntakeFailureCode.MacroPresent);
        ShouldBeRefused(ExtractPdf(indirect), IntakeFailureCode.MacroPresent);
        WalkShouldRefuse(direct, IntakeFailureCode.MacroPresent);
        WalkShouldRefuse(indirect, IntakeFailureCode.MacroPresent);
        ExtractPdf(Fx.Pdf(["hello"])).Text.ShouldBe("hello");
    }

    [Theory]
    [InlineData("Call /Launch after login")]
    [InlineData("The /Launch action is forbidden")]
    [InlineData("The /JavaScript configuration")]
    [InlineData("Review the /JavaScript configuration")]
    [InlineData("/AA is a PDF dictionary key")]
    public void Pdf_ForbiddenSpellingInPageText_IsAccepted(string text) =>
        ExtractPdf(Fx.Pdf([text])).Text.ShouldBe(text);

    [Theory]
    [InlineData("/Note (Call /Launch after login)")]
    [InlineData("/Note (nested (/Launch) and escaped \\) /JavaScript)")]
    [InlineData("/Note (escaped \\\\(nested /AA))")]
    [InlineData("/Note <2F4C61756E6368>")]
    [InlineData("% /Launch /JavaScript /AA\n/Note (hello)")]
    public void Pdf_StringsAndComments_AreNotActionNames(string catalogExtra) =>
        ExtractPdf(Fx.Pdf(["hello"], catalogExtra: catalogExtra)).Text.ShouldBe("hello");

    [Theory]
    [InlineData("BT /F1 12 Tf 72 700 Td (hello) Tj ET\n/Launch /JavaScript /AA /EF", "hello")]
    [InlineData("BT /F1 12 Tf 72 700 Td (hello endstream) Tj ET\n/Launch /JavaScript /AA /EF", "hello endstream")]
    public void Pdf_StreamBody_IsNotScannedAsDictionaryNames(string content, string expectedText) =>
        ExtractPdf(Fx.Pdf(["hello"], pageContents: [content])).Text.ShouldBe(expectedText);

    [Theory]
    [InlineData("/Length 99999")]
    [InlineData("/Length 7 0 R")]
    [InlineData("/Length")]
    public void ProbeRaw_EndstreamWordInsideBody_DoesNotEndTheStream(string lengthEntry)
    {
        var inside = $"1 0 obj\n<< {lengthEntry} >>\nstream\nhello endstream /Launch\nendstream\nendobj\n";

        Should.NotThrow(() => Probe(inside));
        Should.Throw<ExtractionRefusedException>(() => Probe(inside + "2 0 obj\n<< /S /Launch >>\nendobj\n"))
            .FailureCode.ShouldBe(IntakeFailureCode.MacroPresent);
    }

    [Fact]
    public void ProbeRaw_IndirectLengthResolved_BodyWithEndstreamEndobjLines_DoesNotEndTheStream()
    {
        var inside = $"5 0 obj\n{BodyWithMarkerLines.Length}\nendobj\n6 0 obj\n<< /Length 5 0 R >>\nstream\n{BodyWithMarkerLines}\nendstream\nendobj\n";

        Should.NotThrow(() => Probe(inside));
        Should.Throw<ExtractionRefusedException>(() => Probe(inside + "7 0 obj\n<< /S /Launch >>\nendobj\n"))
            .FailureCode.ShouldBe(IntakeFailureCode.MacroPresent);
    }

    [Fact]
    public void Pdf_IndirectLength_BodyWithEndstreamEndobjLines_IsAccepted()
    {
        var content = $"BT /F1 12 Tf 72 700 Td {BodyWithMarkerLines} ET";

        ExtractPdf(Fx.Pdf(["hello"], pageContents: [content], indirectLengths: true)).Succeeded.ShouldBeTrue();
        ShouldBeRefused(ExtractPdf(Fx.Pdf(["hello"], catalogExtra: "/Other << /S /Launch >>", indirectLengths: true)), IntakeFailureCode.MacroPresent);
    }

    private static void Probe(string raw)
    {
        using var deadline = new ExtractionDeadline(IntakeCaps.Default.ExtractionTimeout, CancellationToken.None);

        PdfActionScan.ProbeRaw(System.Text.Encoding.ASCII.GetBytes(raw), deadline);
    }

    [Fact]
    public void Pdf_UnclosedString_IsRefusedNotSkippedToTheEnd()
    {
        ShouldBeRefused(ExtractPdf(Fx.Pdf(["hello"], catalogExtra: "/Note (never closed /Launch")), IntakeFailureCode.ExtractionFailed);
        ExtractPdf(Fx.Pdf(["hello"], catalogExtra: "/Note (closed)")).Text.ShouldBe("hello");
    }

    [Fact]
    public void Pdf_EncryptionWinsOverAMacroThatComesFirstInTheFile()
    {
        var encryption = $"<< /Filter /Standard /V 1 /R 2 /O ({new string('a', 32)}) /U ({new string('b', 32)}) /P -4 >>";
        var both = Fx.Pdf(
            ["hello"],
            catalogExtra: "/OpenAction << /S /JavaScript /JS (x) >>",
            trailerExtra: $"/Encrypt {Fx.FirstExtraObject(1)} 0 R",
            extraObjects: [encryption]);

        ShouldBeRefused(ExtractPdf(both), IntakeFailureCode.EncryptionUnsupported);
    }

    [Fact]
    public void Pdf_FileAttachmentWithoutEmbeddedStreamType_IsRefused()
    {
        var first = Fx.FirstExtraObject(1);
        var attached = Fx.Pdf(["hello"], pageExtra: $"/Annots [{first} 0 R]", extraObjects:
        [
            $"<< /Subtype /FileAttachment /FS << /Type /Filespec /EF << /F {first + 1} 0 R >> >> >>",
            "<< /Length 3 >>\nstream\nabc\nendstream",
        ]);

        ShouldBeRefused(ExtractPdf(attached), IntakeFailureCode.ExternalReferencePresent);
        WalkShouldRefuse(attached, IntakeFailureCode.ExternalReferencePresent);
        ExtractPdf(Fx.Pdf(["hello"])).Text.ShouldBe("hello");
    }

    private static void WalkShouldRefuse(byte[] pdf, string failureCode)
    {
        using var document = PdfDocument.Open(pdf);
        using var deadline = new ExtractionDeadline(IntakeCaps.Default.ExtractionTimeout, CancellationToken.None);

        Should.Throw<ExtractionRefusedException>(() => PdfActionScan.WalkReachable(document, deadline))
            .FailureCode.ShouldBe(failureCode);
    }

    [Fact]
    public void Pdf_TextThatMerelyStartsWithAForbiddenName_IsNotAnAction()
    {
        ExtractPdf(Fx.Pdf(["/AAPL stock"])).Text.ShouldBe("/AAPL stock");
    }
}
