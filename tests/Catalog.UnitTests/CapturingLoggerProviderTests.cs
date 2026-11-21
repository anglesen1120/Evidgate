using Evidgate.Testing;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Catalog.UnitTests;

/// <summary>
/// The shared capture helper is what the redaction tests trust to see every part a log sink could persist, so
/// each part gets its own fact: a part the helper silently dropped would make every "no secret was logged"
/// assertion built on it pass for the wrong reason.
/// </summary>
public sealed class CapturingLoggerProviderTests
{
    private const string Category = "Evidgate.Tests.CaptureProbe";

    [Fact]
    public void FormattedMessage_IsCaptured()
    {
        var (logger, provider) = Create();

        Write(logger, LogLevel.Information, "message-marker");

        provider.Entries.Single().Message.ShouldBe("rendered message-marker");
    }

    [Fact]
    public void StatePair_IsCaptured_AsKeyEqualsValue()
    {
        var (logger, provider) = Create();

        Write(logger, LogLevel.Information, "state-marker");

        provider.Entries.Single().State.ShouldContain("Value=state-marker");
    }

    [Fact]
    public void ScopePair_IsCaptured_WhileTheScopeIsOpen()
    {
        var (logger, provider) = Create();

        using (logger.BeginScope(new Dictionary<string, object?> { ["ScopeKey"] = "scope-marker" }))
        {
            Write(logger, LogLevel.Information, "inside");
        }

        Write(logger, LogLevel.Information, "outside");

        provider.Entries[0].Scopes.ShouldContain("ScopeKey=scope-marker");
        provider.Entries[1].Scopes.ShouldBeEmpty();
    }

    [Fact]
    public void ExceptionText_IsCaptured_WithTypeMessageAndInnerException()
    {
        var (logger, provider) = Create();
        var failure = new InvalidOperationException("outer-marker", new ArgumentException("inner-marker"));

        Write(logger, LogLevel.Error, "failed", failure);

        var text = provider.Entries.Single().Exception;
        text.ShouldNotBeNull();
        text.ShouldContain(nameof(InvalidOperationException));
        text.ShouldContain("outer-marker");
        text.ShouldContain("inner-marker");
    }

    [Fact]
    public void CategoryLevelAndEventId_AreKept()
    {
        var (logger, provider) = Create();

        Write(logger, LogLevel.Warning, "x", eventId: new EventId(42, "Probe"));

        var entry = provider.Entries.Single();
        entry.Category.ShouldBe(Category);
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.EventId.Id.ShouldBe(42);
    }

    [Fact]
    public void Texts_CoverEveryPart_AndClearEmptiesTheCapture()
    {
        var (logger, provider) = Create();

        using (logger.BeginScope(new Dictionary<string, object?> { ["ScopeKey"] = "scope-marker" }))
        {
            Write(logger, LogLevel.Error, "state-marker", new InvalidOperationException("exception-marker"));
        }

        var texts = provider.Entries.Single().Texts.ToList();
        texts.Any(text => text.Contains("rendered state-marker", StringComparison.Ordinal)).ShouldBeTrue();
        texts.ShouldContain("Value=state-marker");
        texts.Any(text => text.Contains("scope-marker", StringComparison.Ordinal)).ShouldBeTrue();
        texts.Any(text => text.Contains("exception-marker", StringComparison.Ordinal)).ShouldBeTrue();

        provider.Clear();

        provider.Entries.Count.ShouldBe(0);
    }

    // Structured state by hand (key, value and the template), so no source-generated logger is needed.
    private static void Write(
        ILogger logger,
        LogLevel level,
        string value,
        Exception? exception = null,
        EventId eventId = default)
    {
        var state = new List<KeyValuePair<string, object?>>
        {
            new("Value", value),
            new("{OriginalFormat}", "rendered {Value}"),
        };
        logger.Log(level, eventId, state, exception, (_, _) => "rendered " + value);
    }

    private static (ILogger Logger, CapturingLoggerProvider Provider) Create()
    {
        var provider = new CapturingLoggerProvider();
        var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(provider));
        return (factory.CreateLogger(Category), provider);
    }
}
