using System.Text;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Shouldly;
using Xunit;

namespace Parser.Tests;

/// <summary>
/// A run's mapped cases are what the parent keeps, never the records or their text: the record sink maps each line
/// as it is framed, so a report's memory cost follows its testcase count and not its size. The byte measurements here
/// run in their own non-parallel collection, because they read process-wide memory.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ParseRecordStreamingMemoryGroup
{
    /// <summary>The collection name the measurement class joins.</summary>
    public const string Name = "Parse record streaming memory";
}

/// <summary>
/// The parent-side memory property of the streaming record path, measured on generated NDJSON: a sink that maps on
/// the spot retains a small constant per case whatever the text volume, while a <see cref="BufferingParseRecordSink"/>
/// retains the text and would fail the same asserts — that control is the proof the measurement can see buffering.
/// </summary>
[Collection(ParseRecordStreamingMemoryGroup.Name)]
public sealed class ParseRecordStreamingMemoryTests(ITestOutputHelper output)
{
    private const int TextBytes = 2048;
    private const int SmallTextBytes = 256;
    private const int LargeTextBytes = 8192;
    private const int MaxRecordBytes = 64 * 1024;
    private const long MaxOutputBytes = 512L * 1024 * 1024;

    /// <summary>
    /// The most one mapped case may retain: measured at about 644 B/case, one <see cref="MappedCase"/> with its
    /// execution key and bounded echo plus its resolved-dictionary entry, independent of the text volume.
    /// </summary>
    private const long MaxRetainedPerCase = 800;

    /// <summary>How far two runs at one case count may differ when their text volume grows eightfold.</summary>
    private const long TextVolumeTolerance = 256 * 1024;

    private static readonly JUnitMapContext Context = new(
        Guid.Empty,
        (classname, name) => classname + "::" + name);

    [Fact]
    public async Task RecordSink_PerCaseRetainedBytes_StayBoundedAtBothSizes()
    {
        await WarmUpAsync();

        var thousand = await MeasureMapperAsync(1_000, TextBytes);
        var twentyThousand = await MeasureMapperAsync(20_000, TextBytes);
        var perCase = thousand / 1_000;
        var deltaPerCase = (twentyThousand - thousand) / 19_000;

        output.WriteLine(
            $"retained: cases=1000 {thousand} B ({perCase} B/case), cases=20000 {twentyThousand} B "
            + $"({twentyThousand / 20_000} B/case), delta {twentyThousand - thousand} B "
            + $"({deltaPerCase} B/case), bound {MaxRetainedPerCase} B/case");

        perCase.ShouldBeLessThanOrEqualTo(MaxRetainedPerCase);
        (twentyThousand - thousand).ShouldBeLessThan(MaxRetainedPerCase * 19_000);
    }

    [Fact]
    public async Task RecordSink_RetainedBytes_DoNotFollowTheTextVolume()
    {
        await WarmUpAsync();

        var small = await MeasureMapperAsync(1_000, SmallTextBytes);
        var large = await MeasureMapperAsync(1_000, LargeTextBytes);

        output.WriteLine(
            $"text {SmallTextBytes} B: {small} B retained; text {LargeTextBytes} B: {large} B retained; "
            + $"delta {large - small} B");

        (large - small).ShouldBeLessThan(TextVolumeTolerance);
    }

    [Fact]
    public async Task BufferingSink_RetainsTheTextThatTheMapperDoesNot_Control()
    {
        await WarmUpAsync();

        var retained = await MeasureBufferingAsync(1_000, TextBytes);
        var perCase = retained / 1_000;

        output.WriteLine($"buffered: {retained} B retained ({perCase} B/case)");

        // The control must be red against the mapper's own bound, or the measurement above proves nothing.
        perCase.ShouldBeGreaterThanOrEqualTo(TextBytes);
        perCase.ShouldBeGreaterThan(MaxRetainedPerCase);
    }

    [Fact]
    public async Task RecordSink_InFlightPerCaseRetainedBytes_StayBoundedAtBothSizes()
    {
        await MeasureInFlightAsync(10, SmallTextBytes);
        var thousand = await MeasureInFlightAsync(1_000, TextBytes);
        var twentyThousand = await MeasureInFlightAsync(20_000, TextBytes);

        output.WriteLine($"in flight: 1000 cases {thousand} B; 20000 cases {twentyThousand} B");
        (thousand / 1_000).ShouldBeLessThanOrEqualTo(MaxRetainedPerCase);
        (twentyThousand - thousand).ShouldBeLessThan(MaxRetainedPerCase * 19_000);
    }

    [Fact]
    public async Task RecordSink_InFlightRetainedBytes_DoNotFollowTheTextVolume()
    {
        await MeasureInFlightAsync(10, SmallTextBytes);
        var small = await MeasureInFlightAsync(1_000, SmallTextBytes);
        var large = await MeasureInFlightAsync(1_000, LargeTextBytes);

        output.WriteLine($"in flight: text {SmallTextBytes} B retains {small} B; text {LargeTextBytes} B retains {large} B");
        (large - small).ShouldBeLessThan(TextVolumeTolerance);
    }

    private static async Task<long> MeasureInFlightAsync(int cases, int textBytes)
    {
        var mapper = new PytestJUnitMapper(new ParsingService()).OpenRecordMapper(Context);
        var protocol = new ChildOutputProtocol(MaxOutputBytes, 3 * cases + 3, MaxRecordBytes, mapper);
        using var stdout = new PausingRecordStream(new GeneratedRecordStream(cases, textBytes));
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var reading = protocol.ReadAsync(stdout, static () => { });
        long retained;
        try
        {
            await stdout.WaitPausedAsync(TestContext.Current.CancellationToken);
            reading.IsCompleted.ShouldBeFalse();

            // Measured with the read still suspended after the last testcase: the accumulator is rooted, so a sink
            // that appended every record to a list and dropped it at the end is visible here, while the mapped cases
            // that are kept by design stay inside the per-case bound above.
            retained = GC.GetTotalMemory(forceFullCollection: true) - before;
            GC.KeepAlive(reading);
            GC.KeepAlive(mapper);
        }
        finally
        {
            stdout.Release();
            var capture = await reading;
            capture.Failure.ShouldBeNull();
            capture.RecordCount.ShouldBe(3 * cases + 2);
        }

        var mapped = mapper.Finish();
        mapped.Cases.Count.ShouldBe(cases);
        mapped.Discrepancies.ShouldBeEmpty();
        return retained;
    }

    private sealed class PausingRecordStream(GeneratedRecordStream source) : Stream
    {
        private readonly TaskCompletionSource paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitPausedAsync(CancellationToken cancellationToken) => await paused.Task.WaitAsync(cancellationToken);
        public void Release() => released.TrySetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (source.BeforeRootEnd)
            {
                paused.TrySetResult();
                await released.Task.WaitAsync(cancellationToken);
            }

            return source.Read(buffer.Span);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                source.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private static async Task WarmUpAsync() => await MeasureMapperAsync(10, SmallTextBytes);

    private static async Task<long> MeasureMapperAsync(int cases, int textBytes)
    {
        var mapper = new PytestJUnitMapper(new ParsingService()).OpenRecordMapper(Context);
        var protocol = new ChildOutputProtocol(MaxOutputBytes, 3 * cases + 3, MaxRecordBytes, mapper);
        using var stdout = new GeneratedRecordStream(cases, textBytes);

        var before = GC.GetTotalMemory(forceFullCollection: true);
        var capture = await protocol.ReadAsync(stdout, static () => { });
        capture.Failure.ShouldBeNull();
        capture.RecordCount.ShouldBe(3 * cases + 2);

        var result = mapper.Finish();
        result.Cases.Count.ShouldBe(cases);
        result.Discrepancies.ShouldBeEmpty();

        var retained = GC.GetTotalMemory(forceFullCollection: true) - before;
        GC.KeepAlive(result);
        GC.KeepAlive(mapper);

        return retained;
    }

    private static async Task<long> MeasureBufferingAsync(int cases, int textBytes)
    {
        var sink = new BufferingParseRecordSink();
        var protocol = new ChildOutputProtocol(MaxOutputBytes, 3 * cases + 3, MaxRecordBytes, sink);
        using var stdout = new GeneratedRecordStream(cases, textBytes);

        var before = GC.GetTotalMemory(forceFullCollection: true);
        var capture = await protocol.ReadAsync(stdout, static () => { });
        capture.Failure.ShouldBeNull();
        sink.Records.Count.ShouldBe(3 * cases + 2);

        var retained = GC.GetTotalMemory(forceFullCollection: true) - before;
        GC.KeepAlive(sink);

        return retained;
    }

    /// <summary>
    /// The child's stdout as a read-only stream that writes each line when it is asked for one, so a test that
    /// buffered the whole document would be visible in the measurement rather than in the fixture.
    /// </summary>
    private sealed class GeneratedRecordStream(int cases, int textBytes) : Stream
    {
        private int nextCase;
        private int stage;
        private byte[] line = [];
        private int offset;

        public bool BeforeRootEnd => stage == 4 && offset == line.Length;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (offset == line.Length && !Next())
            {
                return 0;
            }

            var take = Math.Min(buffer.Length, line.Length - offset);
            line.AsSpan(offset, take).CopyTo(buffer);
            offset += take;

            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private bool Next()
        {
            offset = 0;

            switch (stage)
            {
                case 0:
                    stage = 1;
                    line = Line("{\"t\":\"xml\",\"e\":\"start\",\"n\":\"testsuite\",\"a\":[[\"tests\",\"" + cases + "\"]]}");

                    return true;
                case 1:
                    stage = 2;
                    line = Line(
                        "{\"t\":\"xml\",\"e\":\"start\",\"n\":\"testcase\",\"a\":"
                        + "[[\"classname\",\"tests.test_a\"],[\"name\",\"test_" + nextCase + "\"]]}");

                    return true;
                case 2:
                    stage = 3;
                    line = Line("{\"t\":\"xml\",\"e\":\"text\",\"v\":\"" + new string('x', textBytes) + "\"}");

                    return true;
                case 3:
                    nextCase++;
                    stage = nextCase < cases ? 1 : 4;
                    line = Line("{\"t\":\"xml\",\"e\":\"end\"}");

                    return true;
                case 4:
                    stage = 5;
                    line = Line("{\"t\":\"xml\",\"e\":\"end\"}");

                    return true;
                case 5:
                    stage = 6;
                    line = Line("{\"done\":true,\"count\":" + (3 * cases + 2) + "}");

                    return true;
                default:
                    return false;
            }
        }

        private static byte[] Line(string json) => Encoding.UTF8.GetBytes(json + "\n");
    }
}
