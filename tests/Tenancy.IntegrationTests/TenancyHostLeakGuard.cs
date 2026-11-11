using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit;

[assembly: AssemblyFixture(typeof(Tenancy.IntegrationTests.TenancyHostLeakGuard))]

namespace Tenancy.IntegrationTests;

/// <summary>
/// Fails the run when an API host that was started is still running after every test class finished.
/// </summary>
/// <remarks>
/// A started <see cref="TenancyApiFactory"/> keeps its entry-point task pending until the factory is disposed,
/// and that pending task roots the whole host (service provider, options, meter provider, data source). An
/// undisposed host therefore stays in memory for the rest of the process, however early the test dropped it.
/// </remarks>
public sealed class TenancyHostLeakGuard : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<TenancyApiFactory, string> Running = new(ReferenceEqualityComparer.Instance);

    /// <summary>The first frames of the creating call that are outside this harness, to name the leaking test.</summary>
    internal static string Site()
    {
        var frames = new StackTrace(skipFrames: 1, fNeedFileInfo: false).GetFrames()
            .Select(frame => frame.GetMethod())
            .Where(method => method?.DeclaringType is { Namespace: nameof(Tenancy) + ".IntegrationTests" } type
                && type.Name is not (nameof(TenancyApiFactory) or nameof(TenancyHostPool) or nameof(TenancyHostLeakGuard)))
            .Take(3)
            .Select(method => $"{method!.DeclaringType!.Name}.{method.Name}");
        return string.Join(" < ", frames);
    }

    internal static void Started(TenancyApiFactory factory, string site) => Running[factory] = site;

    internal static void Stopped(TenancyApiFactory factory) => Running.TryRemove(factory, out _);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Running.IsEmpty)
        {
            return ValueTask.CompletedTask;
        }

        var sites = Running.Values
            .GroupBy(site => site, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .Select(group => $"  {group.Count()} x {group.Key}");
        throw new InvalidOperationException(
            $"{Running.Count} API host(s) were started and never disposed:{Environment.NewLine}{string.Join(Environment.NewLine, sites)}");
    }
}
