using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Microsoft.Extensions.Options;

namespace Evidgate.BuildingBlocks.SharedKernel.GitHub;

/// <summary>
/// Resolves the App private key and client secret through <see cref="ISecretProvider"/> after the binder has run. Both
/// properties have internal setters, so this is their only writer; nothing here logs a value or puts one in a message.
/// </summary>
public sealed class GitHubOptionsSecretPostConfigure : IPostConfigureOptions<GitHubOptions>
{
    private readonly ISecretProvider _secrets;

    /// <summary>Creates the post-configurer over the host's secret provider.</summary>
    /// <param name="secrets">The provider the two values are read from.</param>
    public GitHubOptionsSecretPostConfigure(ISecretProvider secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        _secrets = secrets;
    }

    /// <inheritdoc />
    public void PostConfigure(string? name, GitHubOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.PrivateKey = _secrets.GetSecret(SecretNames.GitHubAppPrivateKey);
        options.ClientSecret = _secrets.GetSecret(SecretNames.GitHubAppClientSecret);
    }
}
