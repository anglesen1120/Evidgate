using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Evidgate.Modules.Identity.Login.Linking;

/// <summary>
/// Composition of the explicit second-provider link (02.3.1-T2.2, Q4): the staged-link cookie, the one writer
/// of the identity row and the seam the login callback completes a link through.
/// </summary>
/// <remarks>
/// <para>
/// Every piece is a singleton because everything it depends on already is — the app-role
/// <see cref="Npgsql.NpgsqlDataSource"/>, the session pepper (<c>SessionSecrets</c>), the Data Protection
/// provider and <see cref="TimeProvider"/> — and none of them holds per-request state: the store opens a
/// command per call for the pepper's HMAC candidates, the cookie is stateless, and the store's one statement
/// is self-contained.
/// </para>
/// <para>
/// The request's user is not resolved here any more (02.3.2-T1, D8): the routes and the callback authenticate
/// through the deployment's session request authenticator, and <c>login_link_identity</c> resolves the target
/// account itself from the presented cookie's hashes.
/// </para>
/// <para>
/// <see cref="ILinkCompletion"/> is registered here with <c>TryAdd</c>, so a test substitutes the seam by
/// registering its own implementation afterwards, exactly like <c>ILoginCompletion</c>.
/// </para>
/// </remarks>
internal static class LinkingServiceCollectionExtensions
{
    /// <summary>Registers the linking services.</summary>
    /// <param name="services">The host's collection, after <c>AddFederatedLogin</c> registered the cookie and options.</param>
    public static IServiceCollection AddIdentityLinking(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<LinkConfirmCookie>();
        services.TryAddSingleton<ILinkIdentityStore, NpgsqlLinkIdentityStore>();
        services.TryAddSingleton<ILinkCompletion, LinkCompletion>();

        return services;
    }
}
