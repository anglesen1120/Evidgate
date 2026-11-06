using System.Data.Common;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Application;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The report row as a verdict, not only a record: once an attempt has committed a refusal for an (artifact, parser
/// release) key, a later attempt that parses the same report may not commit the events it mapped, because the refusal is
/// immutable and the two would contradict each other. The redelivery is refused whole, exactly as a refusal leaves no
/// event behind.
/// </summary>
public sealed partial class EvidenceRawReportParseTests
{
    [Fact]
    public async Task Refusal_Then_A_Parsed_Redelivery_Is_Refused_As_An_Outcome_Conflict()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, MalformedBody);

        (await ParseAsync(world, artifactId, ProducerKind)).Status.ShouldBe(ParseRawReportOutcome.Malformed);

        // The same artifact re-uploaded with bytes this parser release can map: the first attempt's verdict stands.
        await StoreAsync(world, artifactId, MappedBody);
        var redelivery = await ParseAsync(world, artifactId, ProducerKind);

        redelivery.Status.ShouldBe(ParseRawReportOutcome.Skipped);
        redelivery.Reason.ShouldBe("report_outcome_conflict");
        redelivery.Events.ShouldBe(0);
        (await RawReportCountAsync(world, artifactId)).ShouldBe(1);
        Text(await RawReportAsync(world, artifactId), "outcome").ShouldBe("malformed");
        await AssertNothingWrittenAsync(world, artifactId, 1);

        // Positive control: only the stored verdict differs from the artifact below, which parses and writes its event.
        var fresh = await SeedArtifactAsync(world, MappedBody);
        var parsed = await ParseAsync(world, fresh, ProducerKind);
        parsed.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        parsed.Events.ShouldBe(1);
    }

    [Fact]
    public async Task Refusal_Then_An_Unmapped_Redelivery_Is_Refused_As_An_Outcome_Conflict()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, MalformedBody);

        (await ParseAsync(world, artifactId, ProducerKind)).Status.ShouldBe(ParseRawReportOutcome.Malformed);

        // A report that maps nothing takes the same path: its own row and trigger are the only writes, so the stored
        // refusal decides.
        await StoreAsync(world, artifactId, UnmappedBody);
        var redelivery = await ParseAsync(world, artifactId, ProducerKind);

        redelivery.Status.ShouldBe(ParseRawReportOutcome.Skipped);
        redelivery.Reason.ShouldBe("report_outcome_conflict");
        redelivery.Events.ShouldBe(0);
        (await RawReportCountAsync(world, artifactId)).ShouldBe(1);
        Text(await RawReportAsync(world, artifactId), "outcome").ShouldBe("malformed");
        await AssertNothingWrittenAsync(world, artifactId, 1);

        // Positive control: a report that maps nothing commits its own row when no verdict is stored yet.
        var fresh = await SeedArtifactAsync(world, UnmappedBody);
        var unmapped = await ParseAsync(world, fresh, ProducerKind);
        unmapped.Status.ShouldBe(ParseRawReportOutcome.Parsed);
        unmapped.Events.ShouldBe(0);
    }

    [Fact]
    public async Task A_Stored_Refusal_Rolls_The_Events_This_Attempt_Already_Inserted_Back()
    {
        var world = await StartAsync();
        await using var _ = world;
        await SeedPlanAsync(world);
        var artifactId = await SeedArtifactAsync(world, MalformedBody);
        (await ParseAsync(world, artifactId, ProducerKind)).Status.ShouldBe(ParseRawReportOutcome.Malformed);
        await StoreAsync(world, artifactId, MappedBody);
        var recording = new RecordingRawReportDb(world.Host.Services.GetRequiredService<ITenantDb>());

        var outcome = await ParseWithDbAsync(world, artifactId, recording);

        outcome.Status.ShouldBe(ParseRawReportOutcome.Skipped);
        outcome.Reason.ShouldBe("report_outcome_conflict");
        recording.EventRowsAtReportInsert.ShouldBe(
            1,
            "the event set is written before the row decides, so the rollback must be of real rows");
        (await CountAsync(world, "event", artifactId)).ShouldBe(0);
        await AssertNothingWrittenAsync(world, artifactId, 1);
    }

    /// <summary>
    /// <see cref="ITenantDb"/> that observes the transaction from inside: how many event rows of the attempt were already
    /// written when the report's own row was inserted.
    /// </summary>
    private sealed class RecordingRawReportDb(ITenantDb inner) : ITenantDb
    {
        public long EventRowsAtReportInsert { get; private set; }

        public DbConnection Connection => inner.Connection;

        public DbTransaction Transaction => inner.Transaction;

        public Task<IReadOnlyList<T>> QueryAsync<T>(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default) =>
            inner.QueryAsync<T>(sql, parameters, cancellationToken);

        public Task<T?> QuerySingleOrDefaultAsync<T>(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default) =>
            inner.QuerySingleOrDefaultAsync<T>(sql, parameters, cancellationToken);

        public Task<T?> ScalarAsync<T>(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default) =>
            inner.ScalarAsync<T>(sql, parameters, cancellationToken);

        public async Task<int> ExecuteAsync(
            string sql,
            object? parameters = null,
            CancellationToken cancellationToken = default)
        {
            if (sql.Contains("insert into public.raw_report", StringComparison.Ordinal))
            {
                EventRowsAtReportInsert = await inner.ScalarAsync<long>(
                    "select count(*) from public.event where artifact_ids = array[@artifact_id::uuid]",
                    parameters,
                    cancellationToken);
            }

            return await inner.ExecuteAsync(sql, parameters, cancellationToken);
        }
    }
}
