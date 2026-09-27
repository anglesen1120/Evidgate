namespace Hosts.IntegrationTests;

/// <summary>
/// The explicit opt-out this suite needs (02.6.1-T1.4, AC5). The boot-time isolation
/// self-check defaults to <c>Required</c>: a process that cannot read the catalogue
/// refuses to start, because it cannot confirm that every non-exempt table is under
/// <c>FORCE ROW LEVEL SECURITY</c> with the policy its kind requires. Several tests
/// in this project boot a host on purpose against a closed database port (the error
/// envelope, shutdown, options validation, the operator-ingress gate), so they must
/// say so with this setting rather than the check being skipped silently — the
/// refusal and the passing boot are proven against real processes in
/// <see cref="IsolationSelfCheckBootTests"/>.
/// </summary>
internal static class IsolationSelfCheckEnvironment
{
    /// <summary>The configuration key in the environment-variable provider's spelling: <c>EVIDGATE__ISOLATION__SELFCHECK</c>.</summary>
    internal const string Variable = "EVIDGATE__ISOLATION__SELFCHECK";

    /// <summary>
    /// The same key as an <c>AddInMemoryCollection</c> key. The two spellings are not
    /// interchangeable: the environment-variable provider maps <c>__</c> to <c>:</c>,
    /// while an in-memory key is used verbatim — a <c>__</c> key silently binds
    /// nothing and leaves the check at its fail-closed default.
    /// </summary>
    internal const string InMemoryKey = "EVIDGATE:Isolation:SelfCheck";

    /// <summary>The opt-out value; the host announces it at Warning on every boot.</summary>
    internal const string Disabled = "Disabled";
}
