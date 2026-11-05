using System.Diagnostics.CodeAnalysis;

namespace Evidgate.GoldenHarness;

/// <summary>
/// The seam a producer's mapper plugs into: the harness resolves a fixture's
/// <c>parser_id</c> here and calls the registered in-process entry point, which is the
/// future JUnit mapper's entry point, never a production dependency-injection registration.
/// A miss is a normal outcome, because a fixture may name a parser that has not landed yet.
/// </summary>
public interface IParserRegistry
{
    /// <summary>Resolves one parser id to its in-process entry point.</summary>
    bool TryGet(string parserId, [NotNullWhen(true)] out ParseFixture? parse);
}

/// <summary>
/// The in-memory parser table of the console harness. Registration is explicit and a
/// duplicate id is refused rather than silently overridden, so one producer cannot shadow
/// another producer's parser and the fixture's <c>parser_id</c> keeps one meaning.
/// </summary>
public sealed class ParserRegistry : IParserRegistry
{
    private readonly Dictionary<string, ParseFixture> _parsers = new(StringComparer.Ordinal);

    public ParserRegistry Register(string parserId, ParseFixture parse)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parserId);
        ArgumentNullException.ThrowIfNull(parse);

        if (!_parsers.TryAdd(parserId, parse))
        {
            throw new InvalidOperationException($"parser id '{parserId}' is already registered");
        }

        return this;
    }

    public bool TryGet(string parserId, [NotNullWhen(true)] out ParseFixture? parse)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parserId);
        return _parsers.TryGetValue(parserId, out parse);
    }
}

/// <summary>Parsers throw this when malformed input prevents producing a document, not for implementation errors.</summary>
public sealed class ParseRefusedException(string message) : Exception(message);
