using System.Diagnostics.CodeAnalysis;

namespace Evidgate.BuildingBlocks.SharedKernel.Secrets;

/// <summary>
/// The one synchronous read of <see cref="ISecretProvider"/> (10.1.1-T2): options
/// post-configurers and the Migrator's configuration parsing are synchronous by
/// design, so every such caller resolves a secret through this helper instead of
/// repeating the wait.
/// </summary>
public static class SecretProviderSyncExtensions
{
    /// <summary>
    /// Resolves <paramref name="name"/> synchronously. The environment provider
    /// completes synchronously, so this never blocks in practice; a managed secret
    /// store (O3) must prefetch its values at boot.
    /// </summary>
    /// <param name="provider">The secret provider.</param>
    /// <param name="name">A secret name, as declared by <see cref="SecretNames"/>.</param>
    /// <returns>The value, or <see langword="null"/> when the secret is unset or empty/whitespace.</returns>
    [SuppressMessage(
        "Usage",
        "VSTHRD002:Avoid problematic synchronous waits",
        Justification = "Options materialisation is synchronous; the environment provider completes synchronously, and a managed secret store (O3) must prefetch.")]
    public static string? GetSecret(this ISecretProvider provider, string name)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var pending = provider.GetSecretAsync(name);

        return pending.IsCompletedSuccessfully ? pending.Result : pending.AsTask().GetAwaiter().GetResult();
    }
}
