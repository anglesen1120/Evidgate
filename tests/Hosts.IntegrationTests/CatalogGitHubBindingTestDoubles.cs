using System.Security.Cryptography;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;

namespace Hosts.IntegrationTests;

/// <summary>
/// The caller every binding request in this suite runs as: a member the test seeded, authenticated without the
/// session credential the production authenticators read, so the routes' own tenant, ownership and write behaviour is
/// what the tests judge.
/// </summary>
internal sealed class SeededMemberAuthenticator(Guid userId) : ICallerAuthenticator
{
    /// <inheritdoc />
    public ValueTask<AuthenticatedCaller?> AuthenticateAsync(
        ICallerRequest request,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<AuthenticatedCaller?>(new AuthenticatedCaller(RequestPrincipal.Member(userId)));
}

/// <summary>
/// Whether the seeded member holds <c>project:write</c> is 02.2.1's question, proven elsewhere; the binding routes are
/// judged here for their tenant, GitHub and write behaviour, so the gate grants and every caller reaches them.
/// </summary>
internal sealed class GrantAllPermissionEvaluator : IPermissionEvaluator
{
    /// <inheritdoc />
    public ValueTask<PermissionDecision> EvaluateAsync(
        string permission,
        IRequestPrincipal principal,
        IRequestTenantContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(PermissionDecision.Granted);
}

/// <summary>
/// A GitHub App the host can configure: any numeric App id and a real RSA private key, because the options validator
/// and the JWT signer read both. The key is generated once per run, never stored, and only ever signs a request the
/// test's handler answers.
/// </summary>
internal static class CatalogGitHubApp
{
    /// <summary>A numeric App id; GitHub App ids are numeric.</summary>
    internal const string AppId = "12345";

    private static readonly Lazy<string> LazyPrivateKey = new(() =>
    {
        using var rsa = RSA.Create();
        rsa.KeySize = 2048;
        return rsa.ExportPkcs8PrivateKeyPem();
    });

    /// <summary>PKCS#8 PEM the GitHub client signs with.</summary>
    internal static string PrivateKey => LazyPrivateKey.Value;

    /// <summary>
    /// The configuration a booting host needs to consider the App configured. The secret name is the configuration
    /// spelling the host's secret provider resolves (<c>EVIDGATE__GITHUB__PRIVATEKEY</c>), so no provider is replaced.
    /// </summary>
    internal static IReadOnlyDictionary<string, string?> Settings(bool configured) =>
        configured
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:GITHUB:AppId"] = AppId,
                ["EVIDGATE:GITHUB:PRIVATEKEY"] = PrivateKey,
            }
            : new Dictionary<string, string?>(StringComparer.Ordinal);
}
