namespace Isolation.Tests;

/// <summary>
/// The one resolver the app role may not execute: the inner link writer, which the audited link definer calls as
/// its owner so the link and the audit rows it owes commit together. Every other resolver is the app role's own
/// entry point, so both directions of the grant are checked.
/// </summary>
public sealed partial class ResolverTests
{
    /// <summary>The resolver registrations whose EXECUTE the app role must NOT hold.</summary>
    private static readonly HashSet<string> OwnerOnlyResolvers = new(StringComparer.Ordinal)
    {
        "evidgate_resolve.login_link_identity",
    };

    /// <summary>The failure for an owner-only registration that names no documented resolver, or null.</summary>
    private static string? OwnerOnlyRegistrationFailure(string[] documented)
    {
        var unknown = OwnerOnlyResolvers.Except(documented, StringComparer.Ordinal).ToArray();

        return unknown.Length == 0
            ? null
            : $"the owner-only set names [{string.Join(", ", unknown)}], which is not a documented resolver — "
              + "an owner-only exception must name a real resolver";
    }

    /// <summary>The failure for a resolver whose app grant is the reverse of its registration, or null.</summary>
    private static string? AppGrantFailure(ResolverFunction resolver, string key) =>
        resolver.AppCanExecute == OwnerOnlyResolvers.Contains(key)
            ? $"{resolver.Signature}: evidgate_app {(resolver.AppCanExecute ? "executes" : "cannot execute")} it, "
              + "which is backwards: an ordinary resolver is the pre-tenant path's way in, and only the inner link "
              + "writer stays behind its audited definer"
            : null;
}
