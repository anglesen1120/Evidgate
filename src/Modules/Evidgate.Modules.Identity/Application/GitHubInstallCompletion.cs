using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.SharedKernel.GitHub;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Application.Commands;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Infrastructure.GitHub;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Evidgate.Modules.Identity.Application;

/// <summary>
/// The install callback's orchestration: each step is its own database unit, and the GitHub calls run between them
/// with no transaction, connection or row lock held.
/// </summary>
/// <remarks>
/// The nonce is consumed and committed first, so a callback the GitHub checks later refuse has still spent it and
/// cannot be replayed. The verified installation is then written with its audit row in one unit, and that unit
/// re-reads the same consumed nonce so the insert cannot escape the org and user the consumption proved.
/// </remarks>
/// <param name="mediator">Sends the two commands, each opening its own tenant transaction.</param>
/// <param name="oauth">The OAuth user half: code exchange, <c>GET /user</c>, the installations list, the account check.</param>
/// <param name="installations">The App-authenticated <c>GET /app/installations/{id}</c> lookup.</param>
/// <param name="logger">Records a token revoke that GitHub refused; never a token.</param>
internal sealed partial class GitHubInstallCompletion(
    IMediator mediator,
    IGitHubOAuthClient oauth,
    IGitHubAppInstallations installations,
    ILogger<GitHubInstallCompletion> logger)
{
    /// <summary>The longest authorization code the callback accepts.</summary>
    internal const int MaxCodeLength = 512;

    /// <summary>Runs the callback: consume, verify outside the database, then insert.</summary>
    /// <param name="callback">The installation, code and state the browser carried.</param>
    /// <param name="cancellationToken">Cancels the callback.</param>
    /// <returns>The bound installation, or the error to answer.</returns>
    public async ValueTask<Result<GitHubInstallationDto>> CompleteAsync(
        GitHubInstallCallback callback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);

        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(GitHubInstallCompletion));

        // Unit one: the consume command owns the `<org>.<nonce>` envelope and its permission, so a caller without
        // ci_binding:write is refused before any parse and a malformed state is refused without consuming
        // anything. A success here is committed before anything else runs.
        var consumed = await mediator.Send(new ConsumeGitHubInstallStateCommand(callback.State), cancellationToken)
            .ConfigureAwait(false);
        if (!consumed.IsSuccess)
        {
            return Result.Failure<GitHubInstallationDto>(consumed.Error!);
        }

        if (!consumed.Value)
        {
            return Result.Failure<GitHubInstallationDto>(GitHubInstallationErrors.StateInvalid);
        }

        // Unit one accepted the envelope, so the parse cannot fail here: unit two re-reads the nonce's digest.
        if (!GitHubInstallStateEnvelope.TryParse(callback.State, out _, out var nonce))
        {
            return Result.Failure<GitHubInstallationDto>(GitHubInstallationErrors.StateInvalid);
        }

        var stateHash = GitHubInstallStateEnvelope.Digest(nonce);

        // No transaction is open here: the GitHub calls hold no connection and no row lock.
        var verified = await VerifyAsync(callback, cancellationToken).ConfigureAwait(false);
        if (!verified.IsSuccess)
        {
            return Result.Failure<GitHubInstallationDto>(verified.Error!);
        }

        // Unit two: the row and its audit entry together, gated on the nonce consumption above.
        return await mediator
            .Send(new CompleteGitHubInstallCommand(verified.Value, stateHash), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The App lookup, then the ownership check with the user token, which is revoked whatever the verdict.</summary>
    private async Task<Result<GitHubAppInstallation>> VerifyAsync(
        GitHubInstallCallback callback,
        CancellationToken cancellationToken)
    {
        if (callback.InstallationId <= 0
            || string.IsNullOrWhiteSpace(callback.Code)
            || callback.Code.Length > MaxCodeLength)
        {
            return Result.Failure<GitHubAppInstallation>(GitHubInstallationErrors.StateInvalid);
        }

        var installation = await installations
            .GetAsync(callback.InstallationId, cancellationToken)
            .ConfigureAwait(false);
        if (!installation.IsSuccess)
        {
            return installation.Error!.Code == GitHubErrorCodes.NotFound
                ? Result.Failure<GitHubAppInstallation>(GitHubInstallationErrors.NotOwned)
                : Result.Failure<GitHubAppInstallation>(installation.Error!);
        }

        var token = await oauth.ExchangeCodeAsync(callback.Code!, cancellationToken).ConfigureAwait(false);
        if (!token.IsSuccess)
        {
            return token.Error!.Code == GitHubInstallationErrorCodes.OAuthRefused
                ? Result.Failure<GitHubAppInstallation>(GitHubInstallationErrors.CodeRefused)
                : Result.Failure<GitHubAppInstallation>(token.Error!);
        }

        try
        {
            return await OwnershipAsync(token.Value, installation.Value, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // Best effort: the user token authorized one callback and is not needed again. A callback already
            // cancelled must still revoke, so the revoke carries no request token — the HTTP client's own timeout
            // bounds it, and a failure only logs.
            await RevokeAsync(token.Value, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>The two-part ownership rule: the user reaches the installation and administers its account.</summary>
    private async Task<Result<GitHubAppInstallation>> OwnershipAsync(
        string userToken,
        GitHubAppInstallation installation,
        CancellationToken cancellationToken)
    {
        var user = await oauth.GetUserAsync(userToken, cancellationToken).ConfigureAwait(false);
        if (!user.IsSuccess)
        {
            return Result.Failure<GitHubAppInstallation>(user.Error!);
        }

        var reachable = await oauth.ListInstallationsAsync(userToken, cancellationToken).ConfigureAwait(false);
        if (!reachable.IsSuccess)
        {
            return Result.Failure<GitHubAppInstallation>(reachable.Error!);
        }

        var match = reachable.Value.FirstOrDefault(entry => entry.Id == installation.Id);
        if (match is null
            || match.AccountId != installation.AccountId
            || !string.Equals(match.AccountLogin, installation.AccountLogin, StringComparison.OrdinalIgnoreCase))
        {
            // A list that disagrees with the App's own view of the same installation is not this user's.
            return Result.Failure<GitHubAppInstallation>(GitHubInstallationErrors.NotOwned);
        }

        var administers = await oauth
            .AdministersAsync(userToken, user.Value, match, cancellationToken)
            .ConfigureAwait(false);
        if (!administers.IsSuccess)
        {
            return Result.Failure<GitHubAppInstallation>(administers.Error!);
        }

        return administers.Value
            ? Result.Success(installation)
            : Result.Failure<GitHubAppInstallation>(GitHubInstallationErrors.NotOwned);
    }

    /// <summary>Revokes the callback's user token; a refusal is logged and never changes the answer.</summary>
    private async Task RevokeAsync(string userToken, CancellationToken cancellationToken)
    {
        try
        {
            await oauth.RevokeUserTokenAsync(userToken, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The client's own timeout: the token still expires on its own and must not fail a bound callback.
            LogRevokeFailed("Timeout");
        }
        catch (Exception exception)
        {
            LogRevokeFailed(exception.GetType().Name);
        }
    }

    [LoggerMessage(
        EventId = 2432,
        Level = LogLevel.Warning,
        Message = "The install callback could not revoke its OAuth user token ({Reason}); the token expires on its own.")]
    private partial void LogRevokeFailed(string reason);
}
