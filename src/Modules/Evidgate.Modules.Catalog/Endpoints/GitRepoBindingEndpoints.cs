using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Catalog.Application;
using Evidgate.Modules.Catalog.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Catalog.Endpoints;

/// <summary>
/// The repository-binding routes, mapped into the catalog group. The delegate binds and answers in <see cref="Result"/>
/// terms only: authorization is the pipeline, the tenant is the request's, and the body is read raw so an unknown
/// member — including any attempt to name the webhook secret — is refused by name.
/// </summary>
internal static class GitRepoBindingEndpoints
{
    /// <summary>Two numeric ids: 4 KiB is far above any legal body.</summary>
    internal const int MaxBodyBytes = 4 * 1024;

    /// <summary>The only members a registration body may carry.</summary>
    private static readonly string[] RegisterMembers = ["installation_id", "repository_id"];

    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Maps the binding routes into the given <c>/v1</c> group and returns it.</summary>
    /// <param name="group">The catalog group.</param>
    public static RouteGroupBuilder MapGitRepoBindingEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/projects/{p}/git-repo-bindings", RegisterAsync)
            .Accepts<RegisterGitRepoBindingRequest>("application/json")
            .Produces<GitRepoBindingDto>(StatusCodes.Status201Created)
            .WithSummary("Register a repository binding after the customer installs the GitHub App (03.4.1)");

        return group;
    }

    private static async Task<Result<GitRepoBindingDto>> RegisterAsync(
        Guid p,
        HttpContext context,
        GitRepoBindingRegistration registration,
        CancellationToken cancellationToken)
    {
        var (body, readError) = await CatalogRequestRules
            .ReadJsonAsync(context, MaxBodyBytes, cancellationToken)
            .ConfigureAwait(false);
        if (readError is not null)
        {
            return Result.Failure<GitRepoBindingDto>(readError);
        }

        if (CatalogRequestRules.UnknownMember(body, RegisterMembers, "a repository binding") is { } memberError)
        {
            return Result.Failure<GitRepoBindingDto>(memberError);
        }

        RegisterGitRepoBindingRequest request;
        try
        {
            request = body.Deserialize<RegisterGitRepoBindingRequest>(RequestJson)
                ?? new RegisterGitRepoBindingRequest(0, 0);
        }
        catch (JsonException exception)
        {
            return Result.Failure<GitRepoBindingDto>(InvalidBody(exception));
        }

        return await registration
            .RegisterAsync(
                p,
                request.InstallationId,
                request.RepositoryId,
                context.Request.Headers["Idempotency-Key"].ToString(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static Error InvalidBody(JsonException exception) =>
        Error.Invalid(
            "the body does not match the repository-binding shape",
            [new ErrorDetail(Domain.TextRules.Brief(exception.Path ?? string.Empty, 128), "invalid")]);
}
