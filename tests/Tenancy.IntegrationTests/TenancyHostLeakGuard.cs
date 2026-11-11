using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Xunit;

[assembly: AssemblyFixture(typeof(Tenancy.IntegrationTests.TenancyHostLeakGuard))]

namespace Tenancy.IntegrationTests;

/// <summary>
/// Fails the run when an API host that was started is still running after every test class finished.
/// </summary>
/// <remarks>
/// A started factory keeps its entry-point task pending until it is disposed, and that task roots the whole host,
/// so an undisposed host stays in memory for the rest of the process.
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
                && (type.DeclaringType ?? type).Name is not (nameof(TenancyApiFactory) or nameof(TenancyHostPool) or nameof(TenancyHostLeakGuard)))
            .Take(3)
            .Select(Describe);
        return string.Join(" < ", frames);
    }

    // An async method runs in a nested state machine named <Method>d__N; report the method that was written.
    private static string Describe(MethodBase? method)
    {
        var type = method!.DeclaringType!;
        if (type.DeclaringType is { } owner && type.Name.StartsWith('<'))
        {
            return $"{owner.Name}.{type.Name[1..type.Name.IndexOf('>')]}";
        }

        return $"{type.Name}.{method.Name}";
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
