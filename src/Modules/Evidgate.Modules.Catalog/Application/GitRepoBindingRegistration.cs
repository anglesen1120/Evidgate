using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Catalog.Application.Commands;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Catalog.Infrastructure.GitHub;
using Mediator;

namespace Evidgate.Modules.Catalog.Application;

/// <summary>
/// The registration flow of <c>POST /v1/projects/{p}/git-repo-bindings</c>: two separately committed units with the
/// GitHub verification between them, so no transaction or row lock is held while GitHub is called.
/// </summary>
/// <remarks>
/// The first unit proves ownership under the route's permission, so a caller without <c>project:write</c> — or one
/// naming another org's installation — is refused before any outbound call. It also reports a retry whose key already
/// has a stored answer, and such a retry skips the verification entirely. Otherwise the verification reads the
/// repository through the org's live installation; nothing is written unless GitHub listed it. The second unit writes
/// the binding and its audit row together and re-checks the ownership the first unit proved.
/// </remarks>
/// <param name="mediator">Sends the two commands, each opening its own tenant transaction.</param>
/// <param name="github">The typed App client; the repository read mints a repository-scoped installation token.</param>
internal sealed class GitRepoBindingRegistration(
    IMediator mediator,
    GitHubClient github)
{
    /// <summary>Runs the flow and answers the created binding, or the refusal to render.</summary>
    /// <param name="projectId">The project from the route.</param>
    /// <param name="installationId">The installation the caller names.</param>
    /// <param name="repositoryId">The repository the caller names.</param>
    /// <param name="idempotencyKey">The client's <c>Idempotency-Key</c>: identifies the retry before the verification
    /// and is the only request member the write unit's fingerprint covers.</param>
    /// <param name="cancellationToken">Cancels the flow.</param>
    public async ValueTask<Result<GitRepoBindingDto>> RegisterAsync(
        Guid projectId,
        long installationId,
        long repositoryId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var prepared = await mediator
            .Send(new PrepareGitRepoBindingCommand(projectId, installationId, idempotencyKey), cancellationToken)
            .ConfigureAwait(false);
        if (!prepared.IsSuccess)
        {
            return Result.Failure<GitRepoBindingDto>(prepared.Error!);
        }

        // The key already has a stored answer, so the retry goes straight to the write unit, which replays it: a branch
        // renamed since, or a GitHub outage, must not turn an accepted request into a conflict or a failure.
        string? defaultBranch = null;
        if (!prepared.Value)
        {
            // No transaction is open here, so the read must not resolve the org's installation from the database: it
            // uses the id the first unit already proved instead, which also pins the answer to that exact installation.
            var repository = await github
                .GetRepositoryAsync(installationId, repositoryId, cancellationToken)
                .ConfigureAwait(false);
            if (!repository.IsSuccess)
            {
                return Result.Failure<GitRepoBindingDto>(GitRepoBindingErrors.FromVerification(repository.Error!));
            }

            defaultBranch = repository.Value.DefaultBranch;
        }

        return await mediator
            .Send(
                new RegisterGitRepoBindingCommand(
                    projectId,
                    installationId,
                    repositoryId,
                    defaultBranch,
                    idempotencyKey),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
