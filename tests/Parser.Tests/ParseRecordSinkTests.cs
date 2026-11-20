using System.Text;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Shouldly;
using Xunit;

namespace Parser.Tests;

public sealed class ParseRecordSinkTests
{
    private static readonly string[] FirstRecord = ["{\"n\":1}"];
    private static readonly string[] FirstTwoRecords = ["{\"n\":1}", "{\"n\":2}"];

    [Fact]
    public async Task ReadAsync_ValidStream_DeliversRecordsInOrderWithoutMarker()
    {
        var sink = new RecordingSink();
        using var stream = new MemoryStream("{\"n\":1}\n{\"n\":2}\n{\"done\":true,\"count\":2}\n"u8.ToArray());
        var protocol = new ChildOutputProtocol(1024, 3, 1024, sink);
        var failures = 0;

        var capture = await protocol.ReadAsync(stream, () => failures++);

        capture.Failure.ShouldBeNull();
        capture.RecordCount.ShouldBe(2);
        capture.Lines.ShouldBe(3);
        failures.ShouldBe(0);
        sink.Records.ShouldBe(FirstTwoRecords);
    }

    [Fact]
    public async Task ReadAsync_ThirdLineExceedsRecordCap_DeliversOnlyFirstTwoRecords()
    {
        var sink = new RecordingSink();
        using var stream = new MemoryStream("{\"n\":1}\n{\"n\":2}\n{\"n\":3}\n{\"done\":true,\"count\":3}\n"u8.ToArray());
        var protocol = new ChildOutputProtocol(1024, 2, 1024, sink);
        var failures = 0;

        var capture = await protocol.ReadAsync(stream, () => failures++);

        capture.Failure.ShouldNotBeNull().ShouldBe(ParseRunFailureKind.OutputRecordsExceeded);
        capture.RecordCount.ShouldBe(2);
        capture.Lines.ShouldBe(3);
        failures.ShouldBe(1);
        sink.Records.ShouldBe(FirstTwoRecords);
    }

    [Fact]
    public async Task ReadAsync_ThirdReadExceedsByteCap_DeliversOnlyFirstTwoRecords()
    {
        var sink = new RecordingSink();
        using var stream = new RecordChunkStream("{\"n\":1}\n{\"n\":2}\n{\"n\":3}\n{\"done\":true,\"count\":3}\n");
        var protocol = new ChildOutputProtocol(16, 10, 1024, sink);
        var failures = 0;

        var capture = await protocol.ReadAsync(stream, () => failures++);

        capture.Failure.ShouldNotBeNull().ShouldBe(ParseRunFailureKind.OutputBytesExceeded);
        capture.OutputBytes.ShouldBe(24);
        capture.RecordCount.ShouldBe(2);
        failures.ShouldBe(1);
        sink.Records.ShouldBe(FirstTwoRecords);
    }

    [Fact]
    public async Task ReadAsync_ExactByteCapIncludingMarker_AcceptsStream()
    {
        var sink = new RecordingSink();
        using var stream = new RecordChunkStream("{\"n\":1}\n{\"n\":2}\n{\"done\":true,\"count\":2}\n");
        var protocol = new ChildOutputProtocol(40, 3, 1024, sink);
        var failures = 0;

        var capture = await protocol.ReadAsync(stream, () => failures++);

        capture.Failure.ShouldBeNull();
        capture.OutputBytes.ShouldBe(40);
        capture.RecordCount.ShouldBe(2);
        failures.ShouldBe(0);
        sink.Records.ShouldBe(FirstTwoRecords);
    }

    [Theory]
    [InlineData("\n{\"n\":2}\n{\"done\":true,\"count\":2}\n", 1)]
    [InlineData("not-json\n{\"n\":2}\n{\"done\":true,\"count\":2}\n", 1)]
    [InlineData("{\"n\":2}\n{\"done\":true,\"count\":1}\n", 2)]
    public async Task ReadAsync_MalformedFrame_DeliversOnlyPreviouslyAcceptedRecords(string tail, int accepted)
    {
        var sink = new RecordingSink();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"n\":1}\n" + tail));
        var protocol = new ChildOutputProtocol(1024, 10, 1024, sink);
        var failures = 0;

        var capture = await protocol.ReadAsync(stream, () => failures++);

        capture.Failure.ShouldNotBeNull().ShouldBe(ParseRunFailureKind.MalformedOutput);
        capture.RecordCount.ShouldBe(accepted);
        failures.ShouldBe(1);
        sink.Records.ShouldBe(accepted == 1 ? FirstRecord : FirstTwoRecords);
    }

    [Fact]
    public async Task ReadAsync_PipeThrowsAfterFirstRecord_ReturnsMalformedWithFirstRecordDelivered()
    {
        var sink = new RecordingSink();
        using var stream = new RecordChunkStream("{\"n\":1}\n{\"done\":true,\"count\":1}\n", failAfterFirstRead: true);
        var protocol = new ChildOutputProtocol(1024, 3, 1024, sink);
        var failures = 0;

        var capture = await protocol.ReadAsync(stream, () => failures++);

        capture.Failure.ShouldNotBeNull().ShouldBe(ParseRunFailureKind.MalformedOutput);
        capture.OutputBytes.ShouldBe(8);
        capture.RecordCount.ShouldBe(1);
        failures.ShouldBe(0);
        sink.Records.ShouldBe(FirstRecord);
    }

    [Fact]
    public void Accept_DefaultBufferingSink_CopiesRecordBytes()
    {
        var sink = new BufferingParseRecordSink();
        var bytes = "{\"n\":1}"u8.ToArray();

        sink.Accept(bytes);
        bytes[5] = (byte)'2';

        sink.Records.Count.ShouldBe(1);
        Encoding.UTF8.GetString(sink.Records[0].Utf8Json.Span).ShouldBe("{\"n\":1}");
    }

    private sealed class RecordingSink : IParseRecordSink
    {
        private readonly List<string> records = [];

        public IReadOnlyList<string> Records => records;

        public void Accept(ReadOnlySpan<byte> utf8Json) => records.Add(Encoding.UTF8.GetString(utf8Json));
    }

    private sealed class RecordChunkStream(string text, bool failAfterFirstRead = false)
        : MemoryStream(Encoding.UTF8.GetBytes(text))
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (failAfterFirstRead && Position >= 8)
            {
                throw new IOException("The pipe broke mid-stream.");
            }

            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 8)], cancellationToken);
        }
    }
}
