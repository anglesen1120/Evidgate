using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Application.Artifacts;
using Evidgate.Modules.Evidence.Application.Capture;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Evidgate.Modules.Evidence.Endpoints;

/// <summary>
/// The per-project capture-policy routes. The read answers every contract kind with its effective capture switch and the
/// redaction rules the project declared for it; the write overrides one kind. The write's body is a closed object read
/// here under the route's cap, which counts the bytes as they arrive; the shape rules live in the command's validator and
/// the named refusals in its handler, while authorization, the tenant transaction and the envelope stay the pipeline's and
/// the host's.
/// </summary>
public static class ArtifactKindPolicyEndpoints
{
    /// <summary>Maps the routes and returns the <c>/v1</c> group so the host can apply its filters.</summary>
    public static RouteGroupBuilder MapArtifactKindPolicyEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1").WithTags("artifacts");

        group.MapGet("/projects/{p:guid}/artifact-kind-policy", GetCaptureOptionsAsync)
            .Produces<CaptureOptionsView>(StatusCodes.Status200OK)
            .WithSummary("Read a project's capture switch and redaction rules per artifact kind");

        group.MapPut("/projects/{p:guid}/artifact-kind-policy/{kind}", SetArtifactKindPolicyAsync)
            .Produces<ArtifactKindPolicyView>(StatusCodes.Status200OK)
            .WithSummary("Override one artifact kind's capture policy for a project");

        return group;
    }

    /// <summary>
    /// <c>GET /v1/projects/{p}/artifact-kind-policy</c>: the project's options for every kind, mapped from the same read
    /// the contract reader will use, so a client and a future adapter caller never see two answers for one project.
    /// </summary>
    private static async Task<Result<CaptureOptionsView>> GetCaptureOptionsAsync(
        Guid p,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var options = await mediator.Send(new GetCaptureOptionsQuery(p), cancellationToken).ConfigureAwait(false);

        return options.IsSuccess
            ? Result.Success(CaptureOptionsView.From(options.Value))
            : Result.Failure<CaptureOptionsView>(options.Error!);
    }

    /// <summary>
    /// <c>PUT /v1/projects/{p}/artifact-kind-policy/{kind}</c>: reads the closed body, then dispatches the upsert.
    /// An unknown kind is the handler's 422 <c>kind_unknown</c> rather than a route constraint, so the refusal
    /// carries the 00 §9.2 code a client can act on.
    /// </summary>
    private static async Task<Result<ArtifactKindPolicyView>> SetArtifactKindPolicyAsync(
        Guid p,
        string kind,
        HttpContext context,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasJsonContentType())
        {
            return Result.Failure<ArtifactKindPolicyView>(new Error(
                ApiErrorCode.RequestInvalid,
                "the request body must be application/json",
                StatusCodes.Status415UnsupportedMediaType));
        }

        if (context.Request.ContentLength is { } declared && declared > CapturePolicyLimits.MaxBodyBytes)
        {
            return Result.Failure<ArtifactKindPolicyView>(CappedRequestBody.TooLarge);
        }

        // A chunked or length-less body declares nothing the check above could see, so the read itself bounds it:
        // the cap holds for the bytes that actually arrive, and no byte past it reaches the parser.
        ArtifactKindPolicyBody? body;
        var inner = context.Request.Body;
        try
        {
            context.Request.Body = new CappedBodyStream(inner, CapturePolicyLimits.MaxBodyBytes);
            body = await context.Request.ReadFromJsonAsync<ArtifactKindPolicyBody>(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BadHttpRequestException exception)
        {
            return Result.Failure<ArtifactKindPolicyView>(CappedRequestBody.Unreadable(exception));
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            return Result.Failure<ArtifactKindPolicyView>(Error.BadRequest("the request body is not valid JSON"));
        }
        finally
        {
            context.Request.Body = inner;
        }

        if (body is null)
        {
            return Result.Failure<ArtifactKindPolicyView>(Error.BadRequest("the request body is required"));
        }

        var ruleShape = CheckRedactionRuleShape(body.RedactionRules);
        if (ruleShape is not null)
        {
            return Result.Failure<ArtifactKindPolicyView>(ruleShape);
        }

        // A media type arrives as a token, so an entry the typed reader would have faulted on is the request's shape
        // refusal with the entry named, exactly as a null rule entry is: no statement runs either way.
        var mediaTypes = ReadMediaTypes(body.MediaTypes);
        if (!mediaTypes.IsSuccess)
        {
            return Result.Failure<ArtifactKindPolicyView>(mediaTypes.Error!);
        }

        return await mediator.Send(
            new SetArtifactKindPolicyOverride(
                p,
                kind,
                body.MaxBytes,
                mediaTypes.Value,
                body.DefaultSensitivity,
                body.CaptureEnabled,
                body.RejectOnSecret,
                body.RedactionRules?.Select(rule => new RedactionRule(rule.RuleType ?? string.Empty, rule.NamePattern ?? string.Empty)).ToArray()),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses a rule list the mapping would fault on or that is unbounded: a null element binds as a null in the array,
    /// and the validator's per-element rules would answer one failure per entry, so an over-long list would turn a
    /// bounded body into an unbounded refusal. No statement runs for either.
    /// </summary>
    private static Error? CheckRedactionRuleShape(RedactionRuleBody[]? rules)
    {
        if (rules is null)
        {
            return null;
        }

        if (rules.Length > CapturePolicyLimits.MaxRedactionRules)
        {
            return Error.Invalid(
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"a kind declares at most {CapturePolicyLimits.MaxRedactionRules} redaction rules"),
                [new ErrorDetail("/redaction_rules", "out_of_range")]);
        }

        for (var index = 0; index < rules.Length; index++)
        {
            if (rules[index] is null)
            {
                return Error.Invalid(
                    "every redaction rule must be an object with rule_type and name_pattern",
                    [new ErrorDetail($"/redaction_rules/{index}", "invalid_type")]);
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the media-type entries: a token that is not a string, and a string whose escapes spell no UTF-16 text (an
    /// unpaired surrogate), cannot become a text value, so each is refused with its path — the second would otherwise
    /// fail the jsonb cast inside the transaction.
    /// </summary>
    private static Result<string[]> ReadMediaTypes(JsonElement[]? entries)
    {
        if (entries is null)
        {
            return Result.Success(Array.Empty<string>());
        }

        var mediaTypes = new string[entries.Length];
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (entry.ValueKind is not JsonValueKind.String)
            {
                return Result.Failure<string[]>(Error.Invalid(
                    "every media type must be a string",
                    [new ErrorDetail($"/media_types/{index}", "invalid_type")]));
            }

            try
            {
                mediaTypes[index] = entry.GetString()!;
            }
            catch (InvalidOperationException)
            {
                return Result.Failure<string[]>(Error.Invalid(
                    "a media type must be a string of decodable characters",
                    [new ErrorDetail($"/media_types/{index}", "invalid_format")]));
            }
        }

        return Result.Success(mediaTypes);
    }
}

/// <summary>
/// Body of <c>PUT /v1/projects/{p}/artifact-kind-policy/{kind}</c>: the capture policy a project sets for one kind.
/// Every knob is required — the row is written as a whole, so a caller never leaves one to a stale value — and the
/// record is closed: a member outside this list is refused at binding.
/// </summary>
/// <param name="MaxBytes">The largest artifact of the kind the project accepts, greater than zero.</param>
/// <param name="MediaTypes">
/// The accepted media types as JSON tokens, between one and 64: each must be a string of 1–255 decodable characters
/// with no control or format character, and the route refuses the rest with the entry's path.
/// </param>
/// <param name="DefaultSensitivity"><c>normal</c> or <c>sensitive</c>.</param>
/// <param name="CaptureEnabled">False switches capture of the kind off for the project.</param>
/// <param name="RejectOnSecret">True makes the project reject an artifact whose scan flags a secret.</param>
/// <param name="RedactionRules">
/// Replaces the kind's declared rules when present; <c>[]</c> clears them and an omitted member leaves them as they are,
/// so a caller that only toggles the switch cannot delete a privacy control it never saw.
/// </param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ArtifactKindPolicyBody(
    [property: JsonRequired, JsonPropertyName("max_bytes")] long MaxBytes,
    [property: JsonRequired, JsonPropertyName("media_types")] JsonElement[] MediaTypes,
    [property: JsonRequired, JsonPropertyName("default_sensitivity")] string DefaultSensitivity,
    [property: JsonRequired, JsonPropertyName("capture_enabled")] bool CaptureEnabled,
    [property: JsonRequired, JsonPropertyName("reject_on_secret")] bool RejectOnSecret,
    [property: JsonPropertyName("redaction_rules")] RedactionRuleBody[]? RedactionRules = null);

/// <summary>One declared rule of the write body: the field class and the name or glob over it.</summary>
/// <param name="RuleType">One of <c>header</c>, <c>cookie</c>, <c>form_field</c>, <c>query_param</c>.</param>
/// <param name="NamePattern">The exact field name, or a <c>*</c>/<c>?</c> glob over it.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RedactionRuleBody(
    [property: JsonPropertyName("rule_type")] string? RuleType,
    [property: JsonPropertyName("name_pattern")] string? NamePattern);

/// <summary>
/// 200 body of the kind-policy override: the stored policy, including the kind the path named. It repeats the
/// request's members so a client can render what the server committed without a second read.
/// </summary>
/// <param name="Kind">The kind the row belongs to.</param>
/// <param name="MaxBytes">The stored size cap.</param>
/// <param name="MediaTypes">The stored media-type allowlist.</param>
/// <param name="DefaultSensitivity">The stored default sensitivity.</param>
/// <param name="CaptureEnabled">The stored capture switch.</param>
/// <param name="RejectOnSecret">The stored secret-rejection switch.</param>
/// <param name="RedactionRules">The kind's rules after the write, ordered by type and name.</param>
public sealed record ArtifactKindPolicyView(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("max_bytes")] long MaxBytes,
    [property: JsonPropertyName("media_types")] string[] MediaTypes,
    [property: JsonPropertyName("default_sensitivity")] string DefaultSensitivity,
    [property: JsonPropertyName("capture_enabled")] bool CaptureEnabled,
    [property: JsonPropertyName("reject_on_secret")] bool RejectOnSecret,
    [property: JsonPropertyName("redaction_rules")] IReadOnlyList<RedactionRuleView> RedactionRules);

/// <summary>
/// 200 body of <c>GET /v1/projects/{p}/artifact-kind-policy</c>: every contract kind with its effective capture switch
/// and declared rules, so a reporter can read the project's policy in one answer instead of one request per kind.
/// </summary>
/// <param name="Kinds">The eight kinds, in contract order.</param>
public sealed record CaptureOptionsView(
    [property: JsonPropertyName("kinds")] IReadOnlyList<CaptureOptionsKindView> Kinds)
{
    /// <summary>The answer for <paramref name="kinds"/>, the read's own order preserved.</summary>
    internal static CaptureOptionsView From(IReadOnlyList<CaptureOptionsKind> kinds) =>
        new([.. kinds.Select(kind => new CaptureOptionsKindView(
            kind.Kind,
            kind.CaptureEnabled,
            [.. kind.RedactionRules.Select(rule => new RedactionRuleView(rule.RuleType, rule.NamePattern))]))]);
}

/// <summary>One kind's capture options in the read's answer.</summary>
/// <param name="Kind">One of the eight contract kinds.</param>
/// <param name="CaptureEnabled">The effective switch: the project row, else the kind's default, else false.</param>
/// <param name="RedactionRules">The kind's declared rules, ordered by type and name.</param>
public sealed record CaptureOptionsKindView(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("capture_enabled")] bool CaptureEnabled,
    [property: JsonPropertyName("redaction_rules")] IReadOnlyList<RedactionRuleView> RedactionRules);

/// <summary>One declared rule in an answer.</summary>
/// <param name="RuleType">One of the four contract rule types.</param>
/// <param name="NamePattern">The declared name or glob.</param>
public sealed record RedactionRuleView(
    [property: JsonPropertyName("rule_type")] string RuleType,
    [property: JsonPropertyName("name_pattern")] string NamePattern);
