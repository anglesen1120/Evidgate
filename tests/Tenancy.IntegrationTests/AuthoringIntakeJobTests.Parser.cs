using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application.Intake;
using Evidgate.Modules.Authoring.Infrastructure.Intake.Jobs;
using Evidgate.Modules.Evidence.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class AuthoringIntakeJobTests
{
    /// <summary>
    /// A parser that ignores its deadline holds a thread the job cannot kill. The attempt gives up at the deadline and
    /// writes <c>extraction_timeout</c>, but the tenant's capacity stays charged until the parser really exits: with two
    /// parsers still blocked, a third extraction must not start.
    /// </summary>
    [Fact]
    public async Task AbandonedParsersKeepTheirSlotsUntilTheyExit()
    {
        var world = await SeedWorldAsync();
        var bytes = AuthoringExtractionFixtures.Docx("Stuck");
        var first = await SeedDocumentAsync(world, bytes);
        var second = await SeedDocumentAsync(world, bytes);
        var third = await SeedDocumentAsync(world, bytes);
        await using var api = TenancyApiFactory.GrantAll(fixture.AppConnectionString(world.Database));
        var store = ActivatorUtilities.CreateInstance<IntakeExtractionStore>(api.Services);
        var clock = new FakeTimeProvider();
        using var parsing = new BlockedParsing(2);
        var job = CreateJob(api.Services, (_, _, _) => Task.FromResult<byte[]?>(bytes), clock, parsing);

        var attempts = Task.WhenAll(job.RunAsync(first, world.Org, Token), job.RunAsync(second, world.Org, Token));
        (await Task.Run(() => parsing.Entered.Wait(TimeSpan.FromSeconds(30), Token), Token)).ShouldBeTrue();
        clock.Advance(IntakeCaps.Default.ExtractionTimeout + TimeSpan.FromSeconds(1));
        await attempts;

        foreach (var intake in new[] { first, second })
        {
            (await StateAsync(world, intake)).ShouldBe("rejected");
            (await ColumnAsync<string>(world, intake, "failure_code")).ShouldBe(IntakeFailureCode.ExtractionTimeout);
        }

        // Both parsers are still running: both slots stay charged, so the third waits and stays received.
        (await SlotCountAsync(world)).ShouldBe(2);
        await Should.ThrowAsync<TaskDeferredException>(() => store.ClaimAsync(world.Org, third, Token));
        (await StateAsync(world, third)).ShouldBe("received");

        // Positive control: once the parsers exit, the slots come back and the same claim goes through.
        parsing.Open();
        await job.WhenSlotsReleasedAsync();
        (await SlotCountAsync(world)).ShouldBe(0);
        (await store.ClaimAsync(world.Org, third, Token)).ShouldNotBeNull();
    }

    /// <summary>A parser whose first call blocks until <see cref="Open"/>, standing in for a stalled PDF/OOXML read.</summary>
    private sealed class BlockedParsing(int parsers) : IParsing, IDisposable
    {
        private readonly ManualResetEventSlim _gate = new(false);

        public CountdownEvent Entered { get; } = new(parsers);

        public void Open() => _gate.Set();

        public MediaSniffVerdict SniffMediaType(string declaredMediaType, ReadOnlySpan<byte> prefix)
        {
            Entered.Signal();
            _gate.Wait(TimeSpan.FromSeconds(60));

            return MediaSniffVerdict.Match;
        }

        public Result<ParsedXmlReport> ParseXmlReport(Stream content, long maxBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Result<JsonDocument> ParseJson(Stream content, long maxBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Result<IReadOnlyList<ArchiveEntry>> EnumerateArchive(Stream content, long maxBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Result<ArchiveContents> ReadArchiveEntries(
            Stream content,
            long maxBytes,
            ArchiveCaps caps,
            Func<string, bool> selectEntry,
            long maxEntryBytes,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Dispose()
        {
            _gate.Set();
            Entered.Dispose();
            _gate.Dispose();
        }
    }
}
