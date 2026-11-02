using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.DevLogin;

/// <summary>
/// Configuration of the development-only sign-in (<c>EVIDGATE:DevLogin</c>): the switch that allows
/// <c>POST /v1/dev/session</c> to exist at all and the shared secret a developer presents to it.
/// </summary>
/// <remarks>
/// <para>
/// Both values are off/absent in every deployment that is not a developer's machine, and the route is not
/// mapped unless all three conditions hold — Development host, <see cref="Enabled"/>, and a configured
/// <see cref="Secret"/>. A deployment that sets the flag outside Development therefore still answers 404:
/// the host environment is the first condition, never the configuration.
/// </para>
/// <para>
/// <see cref="Secret"/> has an internal setter, so the configuration binder skips it; it is resolved through
/// <see cref="ISecretProvider"/> (<c>SecretNames.DevLoginSecret</c>) by
/// <see cref="DevLoginOptionsSecretPostConfigure"/>, the same shape as the session and CI key material. No
/// value is ever logged, echoed or put in an exception message.
/// </para>
/// </remarks>
internal sealed class DevLoginOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "EVIDGATE:DevLogin";

    /// <summary>
    /// Whether the development sign-in may be mapped. Default false: enabling it is an explicit act, and the
    /// host must still be Development for the route to exist.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Shared secret the route compares against the request body. Resolved through <see cref="ISecretProvider"/>
    /// and never bound from configuration; absent leaves the route unmapped rather than open.
    /// </summary>
    public string? Secret { get; internal set; }
}

/// <summary>
/// Resolves <see cref="DevLoginOptions.Secret"/> through <see cref="ISecretProvider"/> after the binder has run,
/// following <c>SessionOptionsSecretPostConfigure</c> and <c>CiAuthOptionsSecretPostConfigure</c>.
/// </summary>
internal sealed class DevLoginOptionsSecretPostConfigure : IPostConfigureOptions<DevLoginOptions>
{
    private readonly ISecretProvider _secrets;

    /// <summary>Binds the secret provider the shared secret is resolved from.</summary>
    /// <param name="secrets">The one secret seam of the platform.</param>
    public DevLoginOptionsSecretPostConfigure(ISecretProvider secrets) => _secrets = secrets;

    /// <inheritdoc />
    public void PostConfigure(string? name, DevLoginOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Secret = _secrets.GetSecret(SecretNames.DevLoginSecret);
    }
}
