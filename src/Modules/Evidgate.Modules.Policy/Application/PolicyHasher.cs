using Evidgate.BuildingBlocks.Canonicalization;

namespace Evidgate.Modules.Policy.Application;

/// <summary>
/// The sole producer of <c>policy_revision.hash</c> (07.1.1-T2.1, finding T1-SEC-S2): the
/// <c>sha256:&lt;64 lower-case hex&gt;</c> content address of a policy body's RFC 8785 canonical form (00 §7).
/// </summary>
internal interface IPolicyHasher
{
    /// <summary>
    /// The <c>sha256:&lt;64 lower-case hex&gt;</c> digest of the RFC 8785 canonical form of the JSON text
    /// <paramref name="body"/> (<see cref="CanonicalHash.Compute(string)"/>).
    /// </summary>
    /// <exception cref="JsonCanonicalizationException">The body cannot be canonicalised.</exception>
    string Hash(string body);
}

/// <summary>
/// Delegates to <see cref="CanonicalHash"/> (01.3.2-T4), the same digest the CLI's <c>canonicalHash</c> returns
/// for one document (01.3.2 AC5). Stateless, so one instance serves every request.
/// </summary>
internal sealed class PolicyHasher : IPolicyHasher
{
    /// <inheritdoc />
    public string Hash(string body) => CanonicalHash.Compute(body);
}
