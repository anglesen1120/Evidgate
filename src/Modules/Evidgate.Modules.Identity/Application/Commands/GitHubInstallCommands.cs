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
/// caller's org and user, and answers the GitHub install URL carrying the org and the nonce. Nothing is bound.
/// </summary>
internal sealed record StartGitHubInstallCommand : ICommand<Result<GitHubInstallStartResponse>>;

/// <summary>The callback inputs: the installation to verify and the one-time state the server issued.</summary>
/// <param name="InstallationId">Numeric installation id from the callback query.</param>
/// <param name="Code">The OAuth authorization code, used once and discarded.</param>
/// <param name="State">The state value the install URL carried; only its hash exists in the database.</param>
internal sealed record GitHubInstallCallback(long InstallationId, string? Code, string? State);

/// <summary>
/// Consumes the one-time state of an install flow: the handler validates the <c>&lt;org&gt;.&lt;nonce&gt;</c>
/// envelope under the binding permission, so a caller without it is refused before anything is parsed or consumed.
/// </summary>
/// <param name="State">The untrusted <c>state</c> query value, validated inside the authorized unit.</param>
internal sealed record ConsumeGitHubInstallStateCommand(string? State) : ICommand<Result<bool>>;

/// <summary>Persists an installation already verified outside the database transaction.</summary>
/// <param name="Installation">The installation the App and the OAuth user both vouch for.</param>
/// <param name="StateHash">The digest of the state whose consumption authorised this insert.</param>
internal sealed record CompleteGitHubInstallCommand(GitHubAppInstallation Installation, byte[] StateHash)
    : ICommand<Result<GitHubInstallationDto>>;

/// <summary><c>DELETE /v1/github/installations/{i}</c>: marks the row deleted; the App itself stays installed at GitHub.</summary>
/// <param name="InstallationId">The row id the view route returns as <c>id</c>.</param>
/// <param name="IdempotencyKey">The <c>Idempotency-Key</c> the replay of a disconnect answers from.</param>
internal sealed record DisconnectGitHubInstallCommand(Guid InstallationId, string IdempotencyKey)
    : ICommand<Result>, IIdempotentCommand;

/// <summary>Handler of <see cref="StartGitHubInstallCommand"/>.</summary>
[Permission(Permissions.CiBinding.Write)]
[AuditedAction("github_installation.start")]
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
        if (!await states
            .AddAsync(hash, orgId, userId, clock.GetUtcNow() + StateLifetime, cancellationToken)
            .ConfigureAwait(false))
        {
            return Result.Failure<GitHubInstallStartResponse>(GitHubInstallationErrors.TooManyFlows);
        }

        // The org rides inside the state so the browser callback, which carries no tenant header of its own,
        // can be resolved to this org and the filter can still prove the session user's membership of it.
        var url = string.Concat(
            configuration.OAuthBaseUrl.TrimEnd('/'),
            "/apps/",
            configuration.AppSlug,
            "/installations/new?state=",
            orgId.ToString("D", CultureInfo.InvariantCulture),
            ".",
            nonce);

        return Result.Success(new GitHubInstallStartResponse(url));
    }

    [LoggerMessage(
        EventId = 2431,
        Level = LogLevel.Warning,
        Message = "The GitHub App slug, client id or app id is absent; the install start reports github_not_configured.")]
    private partial void LogNotConfigured();
}

/// <summary>
/// Handler of <see cref="ConsumeGitHubInstallStateCommand"/>: one conditional update, in its own transaction, so
/// the nonce is spent even when the GitHub checks that follow refuse the callback.
/// </summary>
[Permission(Permissions.CiBinding.Write)]
[AuditedAction("github_installation.state_consume")]
internal sealed class ConsumeGitHubInstallStateCommandHandler(
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IGitHubInstallStateStore states)
    : ICommandHandler<ConsumeGitHubInstallStateCommand, Result<bool>>
{
    /// <inheritdoc />
    public async ValueTask<Result<bool>> Handle(
        ConsumeGitHubInstallStateCommand command,
        CancellationToken cancellationToken)
    {
        var orgId = scopes.Require().OrgId;

        // Nothing is parsed before this point: the [Permission] stage above already refused a caller without
        // ci_binding:write, so a malformed state answers with the denial it deserves, not with a 400 that would
        // tell an unauthorized caller which states are well-formed.
        if (principals.Current?.UserId is not { } userId)
        {
            return Result.Failure<bool>(GitHubInstallationErrors.StateInvalid);
        }

        // The state must name the org the request resolved to: the filter took that org from this same state, so a
        // mismatch means a header or a scope the callback must not honour. Shape, org and nonce in one parse.
        if (!GitHubInstallStateEnvelope.TryParse(command.State, out var stateOrg, out var nonce)
            || stateOrg != orgId)
        {
            return Result.Failure<bool>(GitHubInstallationErrors.StateInvalid);
        }

        // Only a spent nonce is a success, so only it commits and audits; a refusal consumed nothing, and rolling
        // back an empty unit loses nothing. The checks that follow run after this commit and cannot undo it.
        var consumed = await states
            .ConsumeAsync(GitHubInstallStateEnvelope.Digest(nonce), orgId, userId, cancellationToken)
            .ConfigureAwait(false);

        return consumed
            ? Result.Success(true)
            : Result.Failure<bool>(GitHubInstallationErrors.StateInvalid);
    }
}

/// <summary>
/// Handler of <see cref="CompleteGitHubInstallCommand"/>: writes the verified installation and its audit row in one
/// transaction, and re-reads the consumed nonce so the insert stays inside the binding consumption proved.
/// </summary>
[Permission(Permissions.CiBinding.Write)]
[AuditedAction("github_installation.create")]
internal sealed class CompleteGitHubInstallCommandHandler(
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IGitHubInstallStateStore states,
    IGitHubInstallationStore installations,
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

        if (!await states.ExistsUsedAsync(command.StateHash, orgId, userId, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<GitHubInstallationDto>(GitHubInstallationErrors.StateInvalid);
        }

        var row = new GitHubInstallationRow(
            Guid.CreateVersion7(),
            command.Installation.Id,
            command.Installation.AccountLogin,
            command.Installation.AccountId,
            command.Installation.SuspendedAt is null ? "active" : "suspended",
            command.Installation.Permissions,
            command.Installation.SuspendedAt);

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
