using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Evidgate.Api;

/// <summary>
/// Keeps the request-start log of ASP.NET Core (category <see cref="Category"/>) at Warning or above for every
/// provider, whatever the Logging configuration says. That line is composed by the framework before any
/// middleware of ours runs and carries the full query, which on the login callbacks holds an authorization code
/// and a one-time state, so the only safe place to stop it is the level.
/// </summary>
/// <remarks>
/// <para>
/// A filter rule is picked by <c>LoggerRuleSelector</c>: rules of the provider (by full type name or alias) beat
/// provider-less ones whatever their category, then the longest matching category pattern wins, and a tie goes
/// to the last rule. One floor rule per provider name found in the rules, plus one provider-less rule, all with
/// the pattern <c>Category*Category</c>, is therefore beyond reach: a pattern that matches the category has at
/// most one wildcard, a prefix and a suffix of at most the category's length each, so it cannot be longer, and
/// the floor is appended last so it wins the tie. Nothing the operator configured is edited or deleted: a
/// provider Default or a parent category keeps its level for every other category.
/// </para>
/// <para>
/// The floor is applied as a post-configuration of <see cref="LoggerFilterOptions"/>, so it holds again after
/// every configuration reload. A provider that has no rule of its own, and a provider added after the host was
/// configured, fall under the provider-less floor. Rules carrying a <c>Filter</c> delegate can only be set in
/// code (none exists in src today) and are treated like the others: the floor rule outranks them as well.
/// </para>
/// </remarks>
public static class RequestLogFloor
{
    /// <summary>The category of the framework's request-start log.</summary>
    public const string Category = "Microsoft.AspNetCore.Hosting.Diagnostics";

    internal const string FloorPattern = Category + "*" + Category;

    /// <summary>Appends the floor rules to <paramref name="options"/> (idempotent).</summary>
    /// <param name="options">The filter options being built.</param>
    public static void Apply(LoggerFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var providers = options.Rules.Select(rule => rule.ProviderName).Where(name => name is not null).Distinct(StringComparer.Ordinal).ToList();
        providers.Add(null);
        foreach (var provider in providers)
        {
            for (var index = options.Rules.Count - 1; index >= 0; index--)
            {
                if (options.Rules[index].CategoryName == FloorPattern && options.Rules[index].ProviderName == provider)
                {
                    options.Rules.RemoveAt(index);
                }
            }

            options.Rules.Add(new LoggerFilterRule(provider, FloorPattern, LogLevel.Warning, null));
        }
    }

    /// <summary>
    /// The configuration key shapes of rules the floor outranks: a rule below Warning that can apply to
    /// <see cref="Category"/> and is not the provider-less Default every host carries. Names, never values.
    /// </summary>
    /// <param name="options">The filter options after <see cref="Apply"/> or before it.</param>
    public static IReadOnlyList<string> OverriddenKeyShapes(LoggerFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var shapes = new List<string>();
        foreach (var rule in options.Rules)
        {
            var belowWarning = (rule.LogLevel ?? LogLevel.Trace) < LogLevel.Warning;
            var genericDefault = rule.ProviderName is null && rule.CategoryName is null;
            if (rule.CategoryName == FloorPattern || !belowWarning || genericDefault || !Matches(rule.CategoryName))
            {
                continue;
            }

            var shape = rule.ProviderName is null
                ? $"Logging:LogLevel:{rule.CategoryName ?? "Default"}"
                : $"Logging:{rule.ProviderName}:LogLevel:{rule.CategoryName ?? "Default"}";
            if (!shapes.Contains(shape, StringComparer.Ordinal))
            {
                shapes.Add(shape);
            }
        }

        return shapes;
    }

    /// <summary>The selector's category test: a pattern without wildcard is a case-insensitive prefix, one wildcard splits prefix and suffix.</summary>
    private static bool Matches(string? pattern)
    {
        if (pattern is null)
        {
            return true;
        }

        var wildcard = pattern.IndexOf('*', StringComparison.Ordinal);
        if (wildcard < 0)
        {
            return Category.StartsWith(pattern, StringComparison.OrdinalIgnoreCase);
        }

        if (pattern.IndexOf('*', wildcard + 1) >= 0)
        {
            return false;
        }

        return Category.AsSpan().StartsWith(pattern.AsSpan(0, wildcard), StringComparison.OrdinalIgnoreCase)
            && Category.AsSpan().EndsWith(pattern.AsSpan(wildcard + 1), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Tells the operator, once at start and again after a configuration reload, which Logging keys the
/// <see cref="RequestLogFloor"/> overrode. Key shapes only: a configured value is never logged.
/// </summary>
public sealed class RequestLogFloorNotice(IOptionsMonitor<LoggerFilterOptions> options, ILogger<RequestLogFloorNotice> logger)
    : IHostedService, IDisposable
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Report(options.CurrentValue);
        _subscription = options.OnChange(Report);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _subscription?.Dispose();

    private static readonly Action<ILogger, string, string, Exception?> LogHeld = LoggerMessage.Define<string, string>(
        LogLevel.Warning,
        new EventId(1, "RequestLogFloorHeld"),
        "The log level of category {Category} is held at Warning; these Logging keys cannot lower it: {Keys}");

    private void Report(LoggerFilterOptions current)
    {
        var shapes = RequestLogFloor.OverriddenKeyShapes(current);
        if (shapes.Count > 0)
        {
            LogHeld(logger, RequestLogFloor.Category, string.Join(", ", shapes), null);
        }
    }
}
