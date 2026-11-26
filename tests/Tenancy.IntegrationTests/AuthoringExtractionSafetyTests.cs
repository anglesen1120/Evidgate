using Evidgate.Modules.Authoring.Application.Intake;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using Fx = Tenancy.IntegrationTests.AuthoringExtractionFixtures;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Document refusal and positive controls, using in-memory fixtures and the real parser profile without Docker.
/// </summary>
public sealed class AuthoringExtractionSafetyTests
{
    private const string DecomposedVietnamese = "Ye\u0302u ca\u0302\u0300u";
    private const string ComposedVietnamese = "Y\u00EAu c\u1EA7u";

    private static readonly DocumentTextExtractor Extractor = new(new ParsingService());
    private static readonly byte[] SmallBinary = [1, 2, 3];

    // --- DOCX: what a clean document yields ---

    [Fact]
    public void Docx_Paragraphs_Footnotes_And_Vietnamese_ReturnedOnceInNfc()
    {
        var document = Fx.DocxWithBody(
            $"<w:p><w:r><w:t>{DecomposedVietnamese}</w:t></w:r></w:p><w:p><w:r><w:t>Second paragraph</w:t></w:r></w:p>",
            Fx.Footnotes("Footnote text"));

        var outcome = ExtractDocx(document);

        outcome.Succeeded.ShouldBeTrue();
        outcome.FailureCode.ShouldBeNull();
        outcome.Text.ShouldBe(ComposedVietnamese + "\nSecond paragraph\nFootnote text");
    }

    [Fact]
    public void Docx_Runs_Tabs_And_Breaks_AreJoinedInDocumentOrder()
    {
        var body = "<w:p><w:r><w:t>Hel</w:t></w:r><w:r><w:t>lo</w:t></w:r><w:r><w:tab/></w:r><w:r><w:t>x</w:t></w:r>"
            + "<w:r><w:br/></w:r><w:r><w:t>y</w:t></w:r></w:p>";

        ExtractDocx(Fx.DocxWithBody(body)).Text.ShouldBe("Hello\tx\ny");
    }

    [Fact]
    public void Docx_DeletedText_And_FieldCodes_AreNotText()
    {
        var body = "<w:p><w:r><w:t>keep</w:t></w:r><w:del><w:r><w:delText>gone</w:delText></w:r></w:del>"
            + "<w:r><w:instrText>FIELD</w:instrText></w:r></w:p>";

        ExtractDocx(Fx.DocxWithBody(body)).Text.ShouldBe("keep");
    }

    [Fact]
    public void Docx_ExternalHyperlink_IsAccepted_ButAnyOtherExternalRelationshipIsNot()
    {
        var link = ExtractDocx(Fx.DocxWithBody(Paragraph("linked"), Fx.ExternalRelationship("hyperlink")));
        var template = ExtractDocx(Fx.DocxWithBody(Paragraph("linked"), Fx.ExternalRelationship("attachedTemplate")));

        link.Text.ShouldBe("linked");
        ShouldBeRefused(template, IntakeFailureCode.ExternalReferencePresent);
    }

    [Fact]
    public void Docx_NoText_IsRefused_NotAnEmptySuccess()
    {
        ShouldBeRefused(ExtractDocx(Fx.DocxWithBody(string.Empty)), IntakeFailureCode.NoExtractableText);
        ShouldBeRefused(ExtractDocx(Fx.Docx("   ")), IntakeFailureCode.NoExtractableText);
    }

    [Fact]
    public void Docx_MissingDocumentPart_And_NotAZip_AreExtractionFailed()
    {
        ShouldBeRefused(ExtractDocx(Fx.Zip(("hello.txt", SmallBinary))), IntakeFailureCode.ExtractionFailed);
        ShouldBeRefused(ExtractDocx([0x50, 0x4B, 0x03, 0x04, 0xFF, 0xFE, 0x00, 0x01]), IntakeFailureCode.ExtractionFailed);
    }

    // --- DOCX: hostile archives ---

    [Fact]
    public void ZipBomb_Rejected()
    {
        // 8 MiB of zeros deflates to a few KiB: far past the 100:1 cap, and far below every byte cap.
        var bomb = Fx.DocxWithBody(Paragraph("x"), ("customXml/bomb.xml", new byte[8 * 1024 * 1024]));

        ShouldBeRefused(ExtractDocx(bomb), IntakeFailureCode.ArchiveLimitExceeded);
        ExtractDocx(Fx.Docx("x")).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Docx_EntryCount_AtCapAccepted_OneOverRefused()
    {
        var caps = IntakeCaps.Default with { MaxZipEntries = 5 };

        // The base package holds three parts.
        var atCap = Fx.DocxWithBody(Paragraph("x"), ("customXml/a.bin", SmallBinary), ("customXml/b.bin", SmallBinary));
        var overCap = Fx.DocxWithBody(Paragraph("x"), ("customXml/a.bin", SmallBinary), ("customXml/b.bin", SmallBinary), ("customXml/c.bin", SmallBinary));

        ExtractDocx(atCap, caps).Succeeded.ShouldBeTrue();
        ShouldBeRefused(ExtractDocx(overCap, caps), IntakeFailureCode.ArchiveLimitExceeded);
    }

    [Fact]
    public void Docx_NestedArchive_Rejected_WhateverItsName()
    {
        var inner = Fx.Docx("inner");

        var nested = Fx.DocxWithBody(Paragraph("outer"), ("word/media/nested.bin", inner));
        var plain = Fx.DocxWithBody(Paragraph("outer"), ("word/media/plain.bin", SmallBinary));

        ShouldBeRefused(ExtractDocx(nested), IntakeFailureCode.ArchiveLimitExceeded);
        ExtractDocx(plain).Text.ShouldBe("outer");
    }

    [Fact]
    public void Docx_EmbeddedObjectPackage_Rejected()
    {
        var embedded = Fx.DocxWithBody(Paragraph("x"), ("word/embeddings/oleObject1.bin", SmallBinary));

        ShouldBeRefused(ExtractDocx(embedded), IntakeFailureCode.ArchiveLimitExceeded);
    }

    [Fact]
    public void MacroDocument_Rejected()
    {
        var docm = Fx.DocxWithMainContentType("application/vnd.ms-word.document.macroEnabled.main+xml", "x");
        var vbaProject = Fx.DocxWithBody(Paragraph("x"), ("word/vbaProject.bin", SmallBinary));
        var upperCase = Fx.DocxWithBody(Paragraph("x"), ("word/VBAPROJECT.BIN", SmallBinary));

        ShouldBeRefused(ExtractDocx(docm), IntakeFailureCode.MacroPresent);
        ShouldBeRefused(ExtractDocx(vbaProject), IntakeFailureCode.MacroPresent);
        ShouldBeRefused(ExtractDocx(upperCase), IntakeFailureCode.MacroPresent);
        ExtractDocx(Fx.Docx("x")).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void ExternalReference_Rejected()
    {
        const string BillionLaughs =
            "<?xml version=\"1.0\"?><!DOCTYPE lolz [<!ENTITY lol \"lol\">"
            + "<!ENTITY lol2 \"&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;\">"
            + "<!ENTITY lol3 \"&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;\">]>"
            + $"<w:document xmlns:w=\"{Fx.WordNamespace}\"><w:body><w:p><w:r><w:t>&lol3;</w:t></w:r></w:p></w:body></w:document>";
        const string ExternalEntity =
            "<?xml version=\"1.0\"?><!DOCTYPE d [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]>"
            + $"<w:document xmlns:w=\"{Fx.WordNamespace}\"><w:body><w:p><w:r><w:t>&xxe;</w:t></w:r></w:p></w:body></w:document>";

        ShouldBeRefused(ExtractDocx(Fx.DocxWithDocumentXml(BillionLaughs)), IntakeFailureCode.ExternalReferencePresent);
        ShouldBeRefused(ExtractDocx(Fx.DocxWithDocumentXml(ExternalEntity)), IntakeFailureCode.ExternalReferencePresent);
        ExtractDocx(Fx.Docx("x")).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Docx_DtdInAPartThatIsNotTheBody_IsStillRefused()
    {
        var core = Fx.Utf8("<?xml version=\"1.0\"?><!DOCTYPE core [<!ENTITY e \"x\">]><core>&e;</core>");

        ShouldBeRefused(ExtractDocx(Fx.DocxWithBody(Paragraph("x"), ("docProps/core.xml", core))), IntakeFailureCode.ExternalReferencePresent);
    }

    // --- DOCX and PDF: caps, deadline, shape ---

    [Fact]
    public void Docx_CharCap_AtCapAccepted_OneOverRefused_NewlinesCount()
    {
        var caps = IntakeCaps.Default with { MaxExtractedChars = 10 };

        ExtractDocx(Fx.Docx("0123456789"), caps).Text.ShouldBe("0123456789");
        ShouldBeRefused(ExtractDocx(Fx.Docx("01234567890"), caps), IntakeFailureCode.CharLimitExceeded);

        // "12345" + newline + "67890" is eleven characters.
        ExtractDocx(Fx.Docx("12345", "67890"), caps with { MaxExtractedChars = 11 }).Text.ShouldBe("12345\n67890");
        ShouldBeRefused(ExtractDocx(Fx.Docx("12345", "67890"), caps), IntakeFailureCode.CharLimitExceeded);
    }

    [Fact]
    public void Document_Size_AtCapAccepted_OneOverRefused()
    {
        var document = Fx.Docx("size");

        ExtractDocx(document, IntakeCaps.Default with { MaxDocumentBytes = document.Length }).Succeeded.ShouldBeTrue();
        ShouldBeRefused(
            ExtractDocx(document, IntakeCaps.Default with { MaxDocumentBytes = document.Length - 1 }),
            IntakeFailureCode.ArchiveLimitExceeded);
    }

    [Fact]
    public void Document_Stream_IsNeverReadPastTheCap()
    {
        // An endless, non-seekable source: only a cap checked while reading can stop it before it fills memory.
        using var endless = new EndlessStream();
        var caps = IntakeCaps.Default with { MaxDocumentBytes = 1000 };

        var outcome = Extractor.TryExtract(endless, "docx", DocumentTextExtractor.DocxMediaType, caps, CancellationToken.None);

        ShouldBeRefused(outcome, IntakeFailureCode.ArchiveLimitExceeded);
        endless.TotalRead.ShouldBeLessThanOrEqualTo(1000 + 81920);
    }

    [Fact]
    public void Deadline_Aborts()
    {
        var expired = IntakeCaps.Default with { ExtractionTimeout = TimeSpan.Zero };

        ShouldBeRefused(ExtractDocx(Fx.Docx("x"), expired), IntakeFailureCode.ExtractionTimeout);
        ShouldBeRefused(ExtractPdf(Fx.Pdf(["x"]), expired), IntakeFailureCode.ExtractionTimeout);
        ExtractDocx(Fx.Docx("x")).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Docx_NonzeroDeadlineInsideParser_ReturnsExtractionTimeout(bool duringXml)
    {
        var clock = new FakeTimeProvider();
        var parsing = new Fx.DeadlineParsing(clock, duringXml);
        var extractor = new DocumentTextExtractor(parsing, clock);
        var caps = IntakeCaps.Default with { ExtractionTimeout = TimeSpan.FromSeconds(1) };
        using var content = new MemoryStream(Fx.Docx("hello"));
        using var caller = new CancellationTokenSource();

        var outcome = extractor.TryExtract(content, "docx", DocumentTextExtractor.DocxMediaType, caps, caller.Token);

        parsing.ObservedCancellation.ShouldBeTrue();
        caller.IsCancellationRequested.ShouldBeFalse();
        ShouldBeRefused(outcome, IntakeFailureCode.ExtractionTimeout);
        ExtractDocx(Fx.Docx("hello"), caps).Text.ShouldBe("hello");
    }

    [Fact]
    public void CallerCancellation_Propagates_NotAFailureCode()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var content = new MemoryStream(Fx.Docx("x"));

        Should.Throw<OperationCanceledException>(() => Extractor.TryExtract(
            content, "docx", DocumentTextExtractor.DocxMediaType, IntakeCaps.Default, cancelled.Token));
    }

    [Fact]
    public void ContainerMismatch_BothDirections_AndUntrustedDeclarations()
    {
        var docx = Fx.Docx("x");
        var pdf = Fx.Pdf(["x"]);

        // The bytes are the other container.
        ShouldBeRefused(Extract(docx, "pdf", DocumentTextExtractor.PdfMediaType), IntakeFailureCode.ContainerMismatch);
        ShouldBeRefused(Extract(pdf, "docx", DocumentTextExtractor.DocxMediaType), IntakeFailureCode.ContainerMismatch);

        // The empty-archive record is a zip but no DOCX.
        ShouldBeRefused(Extract([0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0], "docx", DocumentTextExtractor.DocxMediaType), IntakeFailureCode.ContainerMismatch);
        ShouldBeRefused(Extract([], "pdf", DocumentTextExtractor.PdfMediaType), IntakeFailureCode.ContainerMismatch);

        // The declaration is not the type this kind accepts, even though the bytes are a valid DOCX.
        ShouldBeRefused(Extract(docx, "docx", "text/plain"), IntakeFailureCode.ContainerMismatch);
        ShouldBeRefused(Extract(docx, "docx", "application/vnd.ms-word.document.macroEnabled.12"), IntakeFailureCode.ContainerMismatch);
        ShouldBeRefused(Extract(docx, "docx", DocumentTextExtractor.PdfMediaType), IntakeFailureCode.ContainerMismatch);
        ShouldBeRefused(Extract(docx, "docm", DocumentTextExtractor.DocxMediaType), IntakeFailureCode.ContainerMismatch);

        // Positive controls: the same bytes under the right declaration, parameters and casing ignored.
        Extract(docx, "docx", DocumentTextExtractor.DocxMediaType.ToUpperInvariant()).Succeeded.ShouldBeTrue();
        Extract(pdf, "pdf", "application/pdf; charset=binary").Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void TextNotDuplicated()
    {
        const string Marker = "marker-7f3a";

        var accepted = ExtractDocx(Fx.Docx($"before {Marker} after"));
        var refused = ExtractDocx(Fx.DocxWithBody(Paragraph($"secret {Marker}"), ("word/vbaProject.bin", SmallBinary)));

        accepted.Text!.IndexOf(Marker, StringComparison.Ordinal).ShouldBe(accepted.Text.LastIndexOf(Marker, StringComparison.Ordinal));
        accepted.Text.ShouldContain(Marker);

        ShouldBeRefused(refused, IntakeFailureCode.MacroPresent);
        refused.ToString().ShouldNotContain(Marker);
    }

    [Fact]
    public void FailureCodes_AreTheClosedSet_And_RefusedRejectsAnythingElse()
    {
        IntakeFailureCode.All.Count.ShouldBe(10);
        Should.Throw<ArgumentException>(() => ExtractionOutcome.Refused("secret text from a document"));
        default(ExtractionOutcome).Succeeded.ShouldBeFalse();
    }

    private static string Paragraph(string text) => $"<w:p><w:r><w:t>{System.Security.SecurityElement.Escape(text)}</w:t></w:r></w:p>";

    private static ExtractionOutcome ExtractDocx(byte[] document, IntakeCaps? caps = null) =>
        Extract(document, "docx", DocumentTextExtractor.DocxMediaType, caps);

    internal static ExtractionOutcome ExtractPdf(byte[] document, IntakeCaps? caps = null) =>
        Extract(document, "pdf", DocumentTextExtractor.PdfMediaType, caps);

    private static ExtractionOutcome Extract(byte[] document, string kind, string declaredMediaType, IntakeCaps? caps = null)
    {
        using var content = new MemoryStream(document);

        return Extractor.TryExtract(content, kind, declaredMediaType, caps ?? IntakeCaps.Default, CancellationToken.None);
    }

    internal static void ShouldBeRefused(ExtractionOutcome outcome, string failureCode)
    {
        outcome.Succeeded.ShouldBeFalse();
        outcome.Text.ShouldBeNull();
        outcome.FailureCode.ShouldBe(failureCode);
    }

    /// <summary>A non-seekable stream of zero bytes that never ends, counting what was asked of it.</summary>
    private sealed class EndlessStream : Stream
    {
        public long TotalRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            TotalRead += count;

            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
