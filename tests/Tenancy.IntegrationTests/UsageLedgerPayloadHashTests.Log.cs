// The two things a conflict leaves behind besides "nothing was written": one Error log line per conflicting key that
// names the org, the key and both hashes but never a payload value, and one increment of the Evidgate conflicts counter.
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.Modules.Usage.Contracts;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// What a conflict reports: the operator needs to see which key claims which two payloads, the counter needs to move
/// once per conflicting key, and neither may carry the payload itself.
/// </summary>
public sealed partial class UsageLedgerPayloadHashTests
{
    [Fact]
    public async Task Conflict_LogsOrgKeyAndBothHashes_WithoutThePayload()
    {
        await using var world = await StartAsync();
        var key = Unique.Slug("logged");
        var stored = Entry(key, world.Project, quantity: 1.5m);
        (await RecordAsync(world, [stored])).Inserted.ShouldBe([key]);

        world.Log.Clear();

        // A distinctive unit and quantity: if either reached the log, the search below would find it.
        var incoming = stored with { Quantity = 9.5m, Unit = "furlong" };
        var attempt = await RecordAsync(world, [incoming]);

        attempt.Conflicts.ShouldBe([key]);

        var line = world.Log.Lines
            .Single(captured => captured.Text.Contains(key, StringComparison.Ordinal));

        line.Level.ShouldBe(LogLevel.Error);
        line.Text.ShouldContain(world.Org.ToString("D"));
        line.Text.ShouldContain(HashOf(stored), "the stored hash names the first payload");
        line.Text.ShouldContain(HashOf(incoming), "the incoming hash names the payload that was refused");
        line.Text.ShouldNotContain("furlong", "the payload's unit never reaches a log line");
        line.Text.ShouldNotContain("9.5", "the payload's quantity never reaches a log line");
    }

    /// <summary>
    /// The counter is the operator's signal that keys are being reused: one measurement per conflicting key of the batch,
    /// so a batch that reuses two keys moves it twice.
    /// </summary>
    [Fact]
    public async Task Conflict_CounterMovesOncePerConflictingKey()
    {
        await using var world = await StartAsync();
        var first = Unique.Slug("counted-a");
        var second = Unique.Slug("counted-b");

        (await RecordAsync(world, [Entry(first, world.Project), Entry(second, world.Project)]))
            .Inserted.ShouldBe([first, second]);

        using var listener = ConflictCounter(out var measured);

        var result = await RecordAsync(
            world,
            [
                Entry(first, world.Project, quantity: 9m),
                Entry(second, world.Project, quantity: 9m),
            ]);

        result.Conflicts.ShouldBe([first, second]);
        measured.ToArray().ShouldBe([1L, 1L], "one increment per conflicting key");
    }

    /// <summary>
    /// A log sink that keeps the level as well as the rendered line, so "an Error was logged" is assertable, not only
    /// "something was logged". The same technique as <see cref="IngestLogSink"/>; the level is the one addition.
    /// </summary>
    private sealed class ConflictLogSink : ILoggerProvider
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Text)> _lines = new();

        /// <summary>Every captured line since <see cref="Clear"/>, in order.</summary>
        internal IReadOnlyList<(LogLevel Level, string Text)> Lines => [.. _lines];

        /// <summary>Forgets every captured line.</summary>
        internal void Clear() => _lines.Clear();

        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName) => new Sink(categoryName, _lines);

        /// <inheritdoc />
        public void Dispose()
        {
            // Nothing to release.
        }

        private sealed class Sink(string category, ConcurrentQueue<(LogLevel Level, string Text)> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var pairs = state as IReadOnlyList<KeyValuePair<string, object?>>;

                lines.Enqueue((
                    logLevel,
                    pairs is null
                        ? $"{category}: {formatter(state, exception)}"
                        : $"{category}: {formatter(state, exception)} | "
                            + string.Join(" | ", pairs.Select(pair => $"{pair.Key}={pair.Value}"))));
            }
        }
    }
}
