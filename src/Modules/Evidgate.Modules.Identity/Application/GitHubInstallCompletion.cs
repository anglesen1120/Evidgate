using System.Security.Cryptography;
using System.Text;
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
    /// <summary>The longest state value the callback accepts.</summary>
    internal const int MaxStateLength = 256;

    /// <summary>The longest nonce the state may carry; the start route writes 64 hex characters.</summary>
    internal const int MaxNonceLength = 128;

    /// <summary>The longest authorization code the callback accepts.</summary>
    internal const int MaxCodeLength = 512;

    /// <summary>Runs the callback: consume, verify outside the database, then insert.</summary>
    /// <param name="callback">The installation, code and state the browser carried.</param>
    /// <param name="cancellationToken">Cancels the callback.</param>
    /// <returns>The bound installation, or the 00 §9.2 error to answer.</returns>
    public async ValueTask<Result<GitHubInstallationDto>> CompleteAsync(
        GitHubInstallCallback callback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);

        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(GitHubInstallCompletion));

        // The state is `<org>.<nonce>` and only the nonce was hashed at start, so the envelope is validated here and
        // the digest is taken over the nonce alone — the same bytes the start route consumed from.
        if (callback.State is not { Length: > 0 and <= MaxStateLength } state
            || NonceOf(state) is not { } nonce)
        {
            return Result.Failure<GitHubInstallationDto>(GitHubInstallationErrors.StateInvalid);
        }

        var stateHash = SHA256.HashData(Encoding.ASCII.GetBytes(nonce));

        // Unit one: a refusal here consumes nothing, and a success here is committed before anything else runs.
        var consumed = await mediator.Send(new ConsumeGitHubInstallStateCommand(stateHash), cancellationToken)
            .ConfigureAwait(false);
        if (!consumed.IsSuccess)
        {
            return Result.Failure<GitHubInstallationDto>(consumed.Error!);
        }

        if (!consumed.Value)
        {
            return Result.Failure<GitHubInstallationDto>(GitHubInstallationErrors.StateInvalid);
        }

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
            // Best effort: the user token authorized one callback and is not needed again.
            await RevokeAsync(token.Value, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// The nonce of an <c>&lt;org&gt;.&lt;nonce&gt;</c> state, or null when the envelope is not the shape the start
    /// route wrote. The org prefix is only checked for shape: the tenant resolution already proved the caller's
    /// membership of the org it names.
    /// </summary>
    /// <param name="state">The untrusted <c>state</c> query value.</param>
    private static string? NonceOf(string state) =>
        state.Length > 37
        && state[36] == '.'
        && Guid.TryParseExact(state[..36], "D", out _)
        && state[37..] is { Length: > 0 and <= MaxNonceLength } nonce
            ? nonce
            : null;

    /// <summary>Revokes the callback's user token; a refusal is logged and never changes the answer.</summary>
    private async Task RevokeAsync(string userToken, CancellationToken cancellationToken)
    {
        try
        {
            await oauth.RevokeUserTokenAsync(userToken, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
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
