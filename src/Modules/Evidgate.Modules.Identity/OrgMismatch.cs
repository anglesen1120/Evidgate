namespace Evidgate.Modules.Identity;

/// <summary>
/// The one answer for a URL org (<c>{o}</c>) that is not the request tenant: 404, the
/// same as a missing org (00 §9.2). The tenant comes from the authenticated principal
/// and is never taken from request input (00 §5.5 C2), so a mismatched path is not a
/// 403 — the caller must not be able to tell another org's id from a non-existent one
/// either (AC7).
/// </summary>
/// <remarks>
/// Declared in the module's root namespace on purpose: the membership commands
/// (<c>Application.Commands</c>) and queries (<c>Application.Queries</c>) both use it,
/// and C#'s enclosing-namespace lookup finds it from either without a using.
/// </remarks>
internal static class OrgMismatch
{
    /// <summary>The message of the 404 every membership route answers for a foreign <c>{o}</c>.</summary>
    internal const string Message = "no organization with that id is visible to this tenant";
}
