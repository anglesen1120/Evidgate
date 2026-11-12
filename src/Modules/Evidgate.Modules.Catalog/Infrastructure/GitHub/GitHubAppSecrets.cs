using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Catalog.Infrastructure.GitHub;

/// <summary>
/// Resolves the GitHub App private key through <see cref="ISecretProvider"/> after the binder has run,
/// following <c>GitHubLoginOptionsSecretPostConfigure</c>. The property has an internal setter, so this
/// is its only writer; nothing here logs the value or puts it in a message.
/// </summary>
internal sealed class GitHubOptionsSecretPostConfigure : IPostConfigureOptions<GitHubOptions>
{
    private readonly ISecretProvider _secrets;

    public GitHubOptionsSecretPostConfigure(ISecretProvider secrets) => _secrets = secrets;

    /// <inheritdoc />
    public void PostConfigure(string? name, GitHubOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.PrivateKey = _secrets.GetSecret(SecretNames.GitHubAppPrivateKey);
    }
}
