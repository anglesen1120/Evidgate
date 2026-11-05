namespace Evidgate.Modules.Evidence.Contracts;

/// <summary>
/// The closed list of <c>redaction_rule.rule_type</c>: the field classes a project may declare for the reporter to strip
/// before it captures anything. It lives here because the same four names travel in the run-create configuration
/// document's <c>redactionRule</c> entries, so the writer of that document and the module's own validation must not hold
/// two copies. A value outside this list is never stored and never executed: the rules are declarative data, not code.
/// </summary>
public static class RedactionRuleTypes
{
    /// <summary>An HTTP request header name.</summary>
    public const string Header = "header";

    /// <summary>A cookie name.</summary>
    public const string Cookie = "cookie";

    /// <summary>A form field name.</summary>
    public const string FormField = "form_field";

    /// <summary>A query-string parameter name.</summary>
    public const string QueryParam = "query_param";

    /// <summary>The four rule types, in contract order.</summary>
    public static readonly IReadOnlyList<string> All = [Header, Cookie, FormField, QueryParam];

    /// <summary>True when <paramref name="ruleType"/> is one of the four contract types.</summary>
    public static bool IsKnown(string? ruleType) => ruleType is not null && All.Contains(ruleType, StringComparer.Ordinal);
}

/// <summary>
/// One declared redaction rule of a project: what class of field and which name (or <c>*</c>/<c>?</c> glob over it) the
/// reporter strips before capture. The server never applies it; it stores and serves the declaration.
/// </summary>
/// <param name="RuleType">One of <see cref="RedactionRuleTypes.All"/>.</param>
/// <param name="NamePattern">The exact field name, or a <c>*</c>/<c>?</c> glob over it.</param>
public sealed record RedactionRule(string RuleType, string NamePattern);

/// <summary>
/// The project's effective capture options of one artifact kind: whether capture is on, and the rules declared for it.
/// </summary>
/// <param name="Kind">One of the eight contract kinds.</param>
/// <param name="CaptureEnabled">
/// The effective switch: the project row's value when it has one, the kind's static default when it has none, and
/// <c>false</c> for a kind with neither — the same three cases the upload route's <c>kind_disabled</c> gate resolves.
/// </param>
/// <param name="RedactionRules">The kind's declared rules, ordered by type and name; empty when none is declared.</param>
public sealed record CaptureOptionsKind(
    string Kind,
    bool CaptureEnabled,
    IReadOnlyList<RedactionRule> RedactionRules);

/// <summary>
/// A project's capture options as another module reads them, one entry per contract kind. A cross-module service call,
/// <b>not</b> a Mediator query: it runs inside the caller's ambient tenant transaction, never opens, commits or rolls
/// back one, and issues a bounded number of statements whatever the number of kinds.
/// <para>
/// The run-create path is the intended consumer: it will embed the options in the adapter protocol v1 configuration
/// document so the reporter applies the redaction client-side. That wiring is not part of this surface yet — nothing
/// calls it outside tests. Nothing here decides whether a run is uncontrolled — that is the adapter's declared
/// capability.
/// </para>
/// </summary>
public interface ICaptureOptionsReader
{
    /// <summary>
    /// Every contract kind with its effective capture switch and declared rules, in contract order. A project without a
    /// policy row for a kind answers that kind's static default; a project without any row answers every kind's default.
    /// </summary>
    /// <param name="orgId">The caller's trusted tenant id, so the read is tenant-exact beyond row level security.</param>
    /// <param name="projectId">The project whose options are read; another tenant's project yields no row and therefore defaults.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<IReadOnlyList<CaptureOptionsKind>> GetAsync(
        Guid orgId,
        Guid projectId,
        CancellationToken cancellationToken);
}
