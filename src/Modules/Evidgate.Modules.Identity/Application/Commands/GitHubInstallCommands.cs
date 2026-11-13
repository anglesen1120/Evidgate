using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.GitHub;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Infrastructure.GitHub;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Application.Commands;

/// <summary>
/// <c>POST /v1/github/installations/start</c>: draws a single-use install nonce, stores only its SHA-256 bound to the
/// caller's org and user, and answers the GitHub install URL carrying the nonce. Nothing is bound by this call.
/// </summary>
internal sealed record StartGitHubInstallCommand : ICommand<Result<GitHubInstallStartResponse>>;

/// <summary>
/// <c>GET /v1/github/installations/callback</c>: consumes the nonce, verifies the installation with the App JWT,
/// exchanges the OAuth code and binds the row only when the user reaches the installation and administers its account.
/// </summary>
/// <param name="InstallationId">Numeric installation id from the callback query.</param>
/// <param name="Code">The OAuth authorization code, used once and discarded.</param>
/// <param name="State">The nonce this server issued; only its hash exists in the database.</param>
internal sealed record CompleteGitHubInstallCommand(long InstallationId, string? Code, string? State)
    : ICommand<Result<GitHubInstallationDto>>;

/// <summary><c>DELETE /v1/github/installations/{i}</c>: marks the row deleted; the App itself stays installed at GitHub.</summary>
/// <param name="InstallationId">The row id the view route returns as <c>id</c>.</param>
/// <param name="IdempotencyKey">The <c>Idempotency-Key</c> the replay of a disconnect answers from.</param>
internal sealed record DisconnectGitHubInstallCommand(Guid InstallationId, string IdempotencyKey)
    : ICommand<Result>, IIdempotentCommand;

/// <summary>Handler of <see cref="StartGitHubInstallCommand"/>.</summary>
[Permission(Permissions.CiBinding.Write)]
internal sealed partial class StartGitHubInstallCommandHandler(
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IGitHubInstallationStore installations,
    IGitHubInstallStateStore states,
    IOptions<GitHubOptions> options,
    TimeProvider clock,
    ILogger<StartGitHubInstallCommandHandler> logger)
    : ICommandHandler<StartGitHubInstallCommand, Result<GitHubInstallStartResponse>>
{
    /// <summary>The nonce lives ten minutes, matching the plan's TTL.</summary>
    internal static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    private const int NonceHexLength = 64;

    /// <inheritdoc />
    public async ValueTask<Result<GitHubInstallStartResponse>> Handle(
        StartGitHubInstallCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(StartGitHubInstallCommand));

        var configuration = options.Value;
        if (string.IsNullOrWhiteSpace(configuration.ClientId)
            || string.IsNullOrWhiteSpace(configuration.AppSlug)
            || string.IsNullOrWhiteSpace(configuration.AppId))
        {
            LogNotConfigured();

            return Result.Failure<GitHubInstallStartResponse>(GitHubInstallationErrors.NotConfigured);
        }

        var orgId = scopes.Require().OrgId;
        if (principals.Current?.UserId is not { } userId)
        {
            return Result.Failure<GitHubInstallStartResponse>(
                Error.Denied(ApiErrorCode.NoMembership, "the install flow is started by an active member of the org"));
        }

        if (await installations.FindCurrentAsync(orgId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return Result.Failure<GitHubInstallStartResponse>(GitHubInstallationErrors.AlreadyInstalled);
        }

        var nonce = RandomNumberGenerator.GetHexString(NonceHexLength, lowercase: true);
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(nonce));
        await states
            .AddAsync(hash, orgId, userId, clock.GetUtcNow() + StateLifetime, cancellationToken)
            .ConfigureAwait(false);

        var url = string.Concat(
            configuration.OAuthBaseUrl.TrimEnd('/'),
            "/apps/",
            configuration.AppSlug,
            "/installations/new?state=",
            nonce);

        return Result.Success(new GitHubInstallStartResponse(url));
    }

    [LoggerMessage(
        EventId = 2431,
        Level = LogLevel.Warning,
        Message = "The GitHub App slug, client id or app id is absent; the install start reports github_not_configured.")]
    private partial void LogNotConfigured();
}

/// <summary>Handler of <see cref="CompleteGitHubInstallCommand"/>; every check fails closed and the nonce is consumed first.</summary>
[Permission(Permissions.CiBinding.Write)]
[AuditedAction("github_installation.create")]
internal sealed class CompleteGitHubInstallCommandHandler(
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IGitHubInstallStateStore states,
    IGitHubInstallationStore installations,
    IGitHubOAuthClient oauth,
    IGitHubAppInstallations appInstallations,
    IAuditTrail audit)
    : ICommandHandler<CompleteGitHubInstallCommand, Result<GitHubInstallationDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<GitHubInstallationDto>> Handle(
        CompleteGitHubInstallCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(CompleteGitHubInstallCommand));

        var orgId = scopes.Require().OrgId;
        if (principals.Current?.UserId is not { } userId)
        {
            return Result.Failure<GitHubInstallationDto>(
                Error.Denied(ApiErrorCode.NoMembership, "the install flow is completed by an active member of the org"));
        }

        if (command.InstallationId <= 0
            || string.IsNullOrWhiteSpace(command.Code)
            || command.Code.Length > 512
            || string.IsNullOrWhiteSpace(command.State)
            || command.State.Length > 256)
        {
            return Result.Failure<GitHubInstallationDto>(GitHubInstallationErrors.StateInvalid);
        }

        // The nonce is consumed before any GitHub call: a replay, an expired state or another org's state never
        // reaches the network, and a failed ownership check cannot be retried with the same nonce.
        var stateHash = SHA256.HashData(Encoding.ASCII.GetBytes(command.State));
        if (!await states.ConsumeAsync(stateHash, orgId, userId, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<GitHubInstallationDto>(GitHubInstallationErrors.StateInvalid);
        }

        var installation = await appInstallations.GetAsync(command.InstallationId, cancellationToken).ConfigureAwait(false);
        if (!installation.IsSuccess)
        {
            return installation.Error!.Code == GitHubErrorCodes.NotFound
                ? Result.Failure<GitHubInstallationDto>(GitHubInstallationErrors.NotOwned)
                : Result.Failure<GitHubInstallationDto>(installation.Error!);
        }

        var verified = await VerifyOwnerAsync(command, installation.Value, cancellationToken).ConfigureAwait(false);
        if (!verified.IsSuccess)
        {
            return Result.Failure<GitHubInstallationDto>(verified.Error!);
        }

        var row = new GitHubInstallationRow(
            Guid.CreateVersion7(),
            installation.Value.Id,
            installation.Value.AccountLogin,
            installation.Value.AccountId,
            "active",
            installation.Value.Permissions);

        var created = await installations
            .CreateAsync(orgId, row, userId, cancellationToken)
            .ConfigureAwait(false);
        if (!created.IsSuccess)
        {
            return Result.Failure<GitHubInstallationDto>(created.Error!);
        }

        audit.Describe(new AuditObject("github_installation", row.Id.ToString("D")));

        return Result.Success(ToDto(row));
    }

    private async Task<Result> VerifyOwnerAsync(
        CompleteGitHubInstallCommand command,
        GitHubAppInstallation installation,
        CancellationToken cancellationToken)
    {
        var token = await oauth.ExchangeCodeAsync(command.Code!, cancellationToken).ConfigureAwait(false);
        if (!token.IsSuccess)
        {
            return token.Error!.Code == "github_oauth_refused"
                ? Result.Failure(GitHubInstallationErrors.CodeRefused)
                : Result.Failure(token.Error!);
        }

        var user = await oauth.GetUserAsync(token.Value, cancellationToken).ConfigureAwait(false);
        if (!user.IsSuccess)
        {
            return Result.Failure(user.Error!);
        }

        var reachable = await oauth.ListInstallationsAsync(token.Value, cancellationToken).ConfigureAwait(false);
        if (!reachable.IsSuccess)
        {
            return Result.Failure(reachable.Error!);
        }

        var match = reachable.Value.FirstOrDefault(entry => entry.Id == installation.Id);
        if (match is null)
        {
            return Result.Failure(GitHubInstallationErrors.NotOwned);
        }

        var administers = await oauth.AdministersAsync(token.Value, user.Value, match, cancellationToken).ConfigureAwait(false);
        if (!administers.IsSuccess)
        {
            return Result.Failure(administers.Error!);
        }

        return administers.Value ? Result.Success() : Result.Failure(GitHubInstallationErrors.NotOwned);
    }

    internal static GitHubInstallationDto ToDto(GitHubInstallationRow row) =>
        new(row.Id, row.InstallationId, row.AccountLogin, row.State, row.Permissions);
}

/// <summary>Handler of <see cref="DisconnectGitHubInstallCommand"/>.</summary>
[Permission(Permissions.CiBinding.Write)]
[AuditedAction("github_installation.delete")]
internal sealed class DisconnectGitHubInstallCommandHandler(
    ITenantScopeAccessor scopes,
    IGitHubInstallationStore installations,
    GitHubInstallationTokenSource tokens,
    IAuditTrail audit)
    : ICommandHandler<DisconnectGitHubInstallCommand, Result>
{
    /// <inheritdoc />
    public async ValueTask<Result> Handle(DisconnectGitHubInstallCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(DisconnectGitHubInstallCommand));

        var orgId = scopes.Require().OrgId;
        var installationId = await installations
            .DisconnectAsync(orgId, command.InstallationId, cancellationToken)
            .ConfigureAwait(false);

        if (installationId is null)
        {
            return Result.Failure(Error.NotFound("no github installation with that id is visible to this tenant"));
        }

        // Cached tokens outlive the row by up to an hour, so the disconnect flush is what stops them.
        tokens.InvalidateAll(installationId.Value);
        audit.Describe(new AuditObject("github_installation", command.InstallationId.ToString("D", CultureInfo.InvariantCulture)));

        return Result.Success(StatusCodes.Status204NoContent);
    }
}
