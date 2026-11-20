using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Replays a stubbed runner's hand-made record list into the sink the request named, so a test that replaces the child
/// still drives the real mapper. A request without a sink keeps the buffered list, which is what the artifact path
/// reads; an empty list is left alone.
/// </summary>
internal static class ParseRunnerSinkFeed
{
    /// <summary>Delivers the answer's records to the request's sink and clears them from the answer.</summary>
    /// <param name="request">The run the handler started.</param>
    /// <param name="result">The answer the stub holds.</param>
    /// <returns>The answer the handler should receive: the same one when it has nothing to deliver.</returns>
    public static ParseRunResult Feed(ParseRunRequest request, ParseRunResult result)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Sink is not { } sink || result is not ParseRunSucceeded { Records.Count: > 0 } succeeded)
        {
            return result;
        }

        foreach (var record in succeeded.Records)
        {
            sink.Accept(record.Utf8Json.Span);
        }

        return succeeded with { Records = Array.Empty<ParseRecord>() };
    }
}
