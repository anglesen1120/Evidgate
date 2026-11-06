using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Parsing;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Junit;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Evidgate.Modules.Ingestion.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The raw-report handler's child seam: every byte of the report reaches a runner and nothing else, a child that
/// crashes, runs out of memory or times out becomes a named refusal whose only row is the report's own, and the child's
/// own rejection record (a document type, an entry budget) is the verdict.
/// </summary>
public sealed partial class EvidenceRawReportParseTests
{
    private const string DtdBody = "<!DOCTYPE testsuite [<!ENTITY x \"y\">]><testsuite><testcase name=\"probe\"/></testsuite>";

    [Fact]
    public async Task The_Report_Bytes_Reach_The_Runner_And_Its_Records_Are_Mapped()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        var runner = new EmulatedChildRunner(world.Host.Services.GetRequiredService<IParsing>());

        var outcome = await ParseAsync(world, artifactId, ProducerKind, runner: runner);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        runner.Inputs.ShouldHaveSingleItem().ShouldBe(ReportBody);
        runner.Profiles.ShouldHaveSingleItem().ShouldBe(ParseRunnerProfile.Xml);
        (await CountAsync(world, "event", artifactId)).ShouldBe(1);
    }

    [Fact]
    public async Task No_Transaction_Is_Open_While_The_Child_Runs()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        long idleInTransaction = -1;
        var runner = new StubParseRunner
        {
            Result = new ParseRunFailed(ParseRunFailureKind.ExitedAbnormally, 3, 0, 0),
            DuringRun = async () => idleInTransaction = await _fixture.ScalarAsync<long>(
                world.Database,
                "select count(*) from pg_stat_activity where datname = current_database() "
                + "and pid <> pg_backend_pid() and state like 'idle in transaction%'"),
        };

        await ParseAsync(world, artifactId, ProducerKind, runner: runner);

        idleInTransaction.ShouldBe(0, "the read scope is closed before the child starts");
    }

    [Theory]
    [InlineData(nameof(ParseRunFailureKind.ExitedAbnormally), 3, ParseRunRejection.Crashed)]
    [InlineData(nameof(ParseRunFailureKind.ExitedAbnormally), 137, ParseRunRejection.Crashed)]
    [InlineData(nameof(ParseRunFailureKind.ExitedAbnormally), 4, ParseRunRejection.Oom)]
    [InlineData(nameof(ParseRunFailureKind.WallClockExceeded), 137, ParseRunRejection.Timeout)]
    [InlineData(nameof(ParseRunFailureKind.OutputBytesExceeded), 0, ParseRunRejection.Crashed)]
    [InlineData(nameof(ParseRunFailureKind.MalformedOutput), 0, ParseRunRejection.Crashed)]
    public async Task A_Failed_Child_Is_A_Named_Refusal_And_Writes_No_Event(
        string kind,
        int exitCode,
        string reason)
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        var runner = new StubParseRunner { Result = new ParseRunFailed(Enum.Parse<ParseRunFailureKind>(kind), exitCode, 0, 0) };

        var outcome = await ParseAsync(world, artifactId, ProducerKind, runner: runner);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Malformed);
        outcome.Reason.ShouldBe(reason);
        await AssertNothingWrittenAsync(world, artifactId, 1);
    }

    [Fact]
    public async Task A_Child_That_Never_Started_Is_Retried_Not_Judged()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        var runner = new StubParseRunner { Result = new ParseRunFailed(ParseRunFailureKind.SpawnFailed, null, 0, 0) };

        await Should.ThrowAsync<InvalidOperationException>(
            () => ParseAsync(world, artifactId, ProducerKind, runner: runner));
        await AssertNothingWrittenAsync(world, artifactId, 0);
    }

    [Fact]
    public async Task A_Document_Type_Is_Refused_By_The_Child_And_Writes_No_Event()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, Encoding.UTF8.GetBytes(DtdBody));
        var runner = new EmulatedChildRunner(world.Host.Services.GetRequiredService<IParsing>());

        var outcome = await ParseAsync(world, artifactId, ProducerKind, runner: runner);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Malformed);
        outcome.Reason.ShouldBe(ParsingFailureReason.DtdProhibited);
        await AssertNothingWrittenAsync(world, artifactId, 1);
    }

    [Fact]
    public async Task A_Report_Over_The_Entry_Budget_Is_Refused_By_The_Child_And_The_Budget_Is_Handed_Over()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        var runner = new StubParseRunner
        {
            Result = new ParseRunSucceeded(
                [Record($$"""{"t":"rejected","code":"artifact_invalid","reason":"{{ArtifactFailureCode.CapExceeded}}"}""")],
                0,
                64,
                1),
        };

        var outcome = await ParseAsync(world, artifactId, ProducerKind, runner: runner);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Malformed);
        outcome.Reason.ShouldBe(ArtifactFailureCode.CapExceeded);
        runner.MaxCases.ShouldHaveSingleItem()
            .ShouldBe(world.Host.Services.GetRequiredService<IOptions<IngestionLimits>>().Value.MaxReportCases);
        await AssertNothingWrittenAsync(world, artifactId, 1);
    }

    [Fact]
    public async Task A_Report_Over_The_Byte_Cap_Never_Reaches_The_Child()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        await _fixture.ExecuteAsync(
            world.Database,
            "insert into public.artifact_kind_policy (id, org_id, project_id, kind, max_bytes, media_types, default_sensitivity) "
            + $"values ('{Guid.CreateVersion7():D}', '{world.Org:D}', '{world.Project:D}', '{ArtifactKind.RawReport}', 8, "
            + $"array['{XmlType}'], 'normal')");
        var runner = new StubParseRunner();

        var outcome = await ParseAsync(world, artifactId, ProducerKind, runner: runner);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Malformed);
        outcome.Reason.ShouldBe("too_large");
        runner.Calls.ShouldBe(0);
        await AssertNothingWrittenAsync(world, artifactId, 1);
    }

    [Fact]
    public async Task Records_That_Do_Not_Form_A_Document_Are_A_Crashed_Child()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, ReportBody);
        var runner = new StubParseRunner
        {
            Result = new ParseRunSucceeded(
                [Record("""{"t":"xml","e":"start","n":"testsuite","a":[]}""")],
                0,
                64,
                1),
        };

        var outcome = await ParseAsync(world, artifactId, ProducerKind, runner: runner);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Malformed);
        outcome.Reason.ShouldBe(ParseRunRejection.Crashed);
        await AssertNothingWrittenAsync(world, artifactId, 1);
    }

    [Theory]
    [InlineData("<testsuites><testsuite name=\"p\" tests=\"2\"><testcase classname=\"a.b\" name=\"t &amp; &lt;1&gt;\" time=\"0.5\"><failure message=\"x&#10;y\"/></testcase><testcase name=\"ok\"/></testsuite></testsuites>")]
    [InlineData("<?xml version=\"1.0\"?><x:testsuite xmlns:x=\"urn:x\" tests=\"1\"><testcase name=\"n\"><skipped/></testcase></x:testsuite>")]
    [InlineData("<testsuite tests=\"1\">\n  <testcase name=\"w\"><system-out><![CDATA[a < b]]></system-out></testcase>\n</testsuite>")]
    public async Task The_Record_Path_Maps_Exactly_What_The_Byte_Path_Maps(string xml)
    {
        var parsing = new ParsingService(maxReportCases: 1000);
        var mapper = new PytestJUnitMapper(parsing);
        var context = new JUnitMapContext(Guid.NewGuid(), (_, name) => name.Length == 0 ? null : new string('b', 64));
        var bytes = Encoding.UTF8.GetBytes(xml);
        var runner = new EmulatedChildRunner(parsing);

        var run = (ParseRunSucceeded)await runner.RunAsync(
            new ParseRunRequest(ParseRunnerProfile.Xml, 1 << 20, "application/xml", new MemoryStream(bytes), 1000),
            Token);

        var viaRecords = mapper.Map(run.Records, context);
        var viaBytes = mapper.Map(new MemoryStream(bytes), context);

        viaRecords.Cases.ShouldBe(viaBytes.Cases);
        viaRecords.Declared.ShouldBe(viaBytes.Declared);
        viaRecords.Discrepancies.ShouldBe(viaBytes.Discrepancies);
    }

    [Fact]
    public void Records_Nested_Past_The_Profile_Depth_Are_A_Crashed_Child()
    {
        var records = Enumerable.Range(0, 200).Select(_ => Record("{\"t\":\"xml\",\"e\":\"start\",\"n\":\"a\",\"a\":[]}")).ToList();
        var mapper = new PytestJUnitMapper(new ParsingService(maxReportCases: 10));

        var refused = Should.Throw<JUnitParseRefusedException>(
            () => mapper.Map(records, new JUnitMapContext(Guid.NewGuid(), (_, _) => null)));

        refused.Reason.ShouldBe("parse_crashed");
    }

    private static ParseRecord Record(string json) => new(Encoding.UTF8.GetBytes(json));

    private static ParseRawReportHandler CreateHandler(World world, IParseRunner? runner, ITenantDb? db = null)
    {
        var services = world.Host.Services;

        return new ParseRawReportHandler(
            services.GetRequiredService<ITenantScopeFactory>(),
            services.GetRequiredService<ITenantDbContextFactory<EvidenceDbContext>>(),
            db ?? services.GetRequiredService<ITenantDb>(),
            services.GetRequiredService<IObjectStorage>(),
            services.GetRequiredService<IReportParserResolver>(),
            runner ?? new EmulatedChildRunner(services.GetRequiredService<IParsing>()),
            services.GetRequiredService<IOptions<IngestionLimits>>(),
            services.GetServices<IJUnitMapper>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IOutboxWriter>(),
            services.GetRequiredService<IAuditTrail>(),
            services.GetRequiredService<TimeProvider>(),
            NullLogger<ParseRawReportHandler>.Instance);
    }
}

/// <summary>
/// The parse child's stand-in for hosts that cannot spawn one: it runs the same parser profile and writes the record
/// stream the child writes (start / text / end, or one rejected record), so the handler is exercised on real bytes.
/// </summary>
internal sealed class EmulatedChildRunner(IParsing parsing) : IParseRunner
{
    public List<byte[]> Inputs { get; } = [];

    public List<ParseRunnerProfile> Profiles { get; } = [];

    public async Task<ParseRunResult> RunAsync(ParseRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var buffer = new MemoryStream();
        await request.Input.CopyToAsync(buffer, cancellationToken);
        Inputs.Add(buffer.ToArray());
        Profiles.Add(request.Profile);
        buffer.Position = 0;

        return Emulate(buffer, request.MaxBytes, cancellationToken);
    }

    private ParseRunSucceeded Emulate(Stream buffer, long maxBytes, CancellationToken cancellationToken)
    {
        var records = new List<ParseRecord>();
        var parsed = parsing.ParseXmlReport(buffer, maxBytes, cancellationToken);
        if (parsed.Error is { } failure)
        {
            var reason = JUnitParseRefusedException.From(failure).Reason;
            records.Add(Json(w =>
            {
                w.WriteString("t", "rejected");
                w.WriteString("code", failure.Code);
                w.WriteString("reason", reason);
            }));

            return new ParseRunSucceeded(records, 0, 0, records.Count);
        }

        using var reader = parsed.Value.OpenReader();
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case System.Xml.XmlNodeType.Element:
                    var attributes = new List<(string, string)>();
                    var empty = reader.IsEmptyElement;
                    while (reader.MoveToNextAttribute())
                    {
                        attributes.Add((reader.Name, reader.Value));
                    }

                    reader.MoveToElement();
                    records.Add(Json(w =>
                    {
                        w.WriteString("t", "xml");
                        w.WriteString("e", "start");
                        w.WriteString("n", reader.Name);
                        w.WriteStartArray("a");
                        foreach (var (key, value) in attributes)
                        {
                            w.WriteStartArray();
                            w.WriteStringValue(key);
                            w.WriteStringValue(value);
                            w.WriteEndArray();
                        }

                        w.WriteEndArray();
                    }));
                    if (empty)
                    {
                        records.Add(End());
                    }

                    break;
                case System.Xml.XmlNodeType.EndElement:
                    records.Add(End());
                    break;
                case System.Xml.XmlNodeType.Text:
                case System.Xml.XmlNodeType.CDATA:
                case System.Xml.XmlNodeType.Whitespace:
                case System.Xml.XmlNodeType.SignificantWhitespace:
                    var text = reader.Value;
                    records.Add(Json(w =>
                    {
                        w.WriteString("t", "xml");
                        w.WriteString("e", "text");
                        w.WriteString("v", text);
                    }));
                    break;
            }
        }

        return new ParseRunSucceeded(records, 0, 0, records.Count);
    }

    private static ParseRecord End() => Json(w =>
    {
        w.WriteString("t", "xml");
        w.WriteString("e", "end");
    });

    private static ParseRecord Json(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return new ParseRecord(stream.ToArray());
    }
}
