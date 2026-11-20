using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Orchestrator.Application.Manifests;
using Evidgate.Modules.Orchestrator.Contracts;
using Evidgate.Modules.Orchestrator.Infrastructure.Persistence;
using Evidgate.Modules.Policy.Contracts;
using Mediator;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Evidgate.Modules.Orchestrator.Application.Runs;

internal sealed partial class CreateRunCommandHandler
{
    /// <summary>The optional legacy <c>release</c> name: a string of 1..<see cref="MaxReleaseRunes"/> code points, or a 422.</summary>
    private static (string? Release, Error? Refusal) ParseReleaseName(JsonElement body)
    {
        string? release = null;
        if (body.TryGetProperty("release", out var releaseElement))
        {
            if (releaseElement.ValueKind != JsonValueKind.String)
            {
                return (null, Error.Invalid("release must be a string", [new ErrorDetail("/release", InvalidValue)]));
            }

            release = releaseElement.GetString();
            var runes = release!.EnumerateRunes().Count();
            if (runes is < 1 or > MaxReleaseRunes)
            {
                return (null, Error.Invalid(
                    $"release must be 1..{MaxReleaseRunes} characters",
                    [new ErrorDetail("/release", InvalidValue)]));
            }
        }

        return (release, null);
    }

    /// <summary>The optional <c>components</c> object: shape, serialised size and storable numbers, or a 422.</summary>
    private static (JsonElement? Components, Error? Refusal) ParseComponents(JsonElement body)
    {
        JsonElement? components = null;
        if (body.TryGetProperty("components", out var componentsElement))
        {
            if (componentsElement.ValueKind != JsonValueKind.Object)
            {
                return (null, Error.Invalid("components must be a JSON object", [new ErrorDetail("/components", InvalidValue)]));
            }

            if (Encoding.UTF8.GetByteCount(componentsElement.GetRawText()) > MaxComponentsBytes)
            {
                return (null, Error.Invalid(
                    $"components must be at most {MaxComponentsBytes} bytes serialised",
                    [new ErrorDetail("/components", InvalidValue)]));
            }

            // A number the jsonb column cannot hold is refused here, before any write: a literal like `1e9999999` is
            // a few bytes but Postgres refuses the cast with 22003, which would escape the tenant transaction as a 500.
            if (HasUnstorableNumber(componentsElement))
            {
                return (null, Error.Invalid(
                    "components carries a number that cannot be stored",
                    [new ErrorDetail("/components", InvalidValue)]));
            }

            components = componentsElement;
        }

        return (components, null);
    }

    /// <summary>True when any number anywhere inside <paramref name="element"/> has an exponent jsonb cannot hold.</summary>
    private static bool HasUnstorableNumber(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => NumberOverflows(element.GetRawText()),
        JsonValueKind.Array => element.EnumerateArray().Any(HasUnstorableNumber),
        JsonValueKind.Object => element.EnumerateObject().Any(member => HasUnstorableNumber(member.Value)),
        _ => false,
    };

    /// <summary>
    /// True when the JSON number token <paramref name="raw"/> (already accepted by <see cref="JsonDocument.Parse"/>)
    /// carries a decimal exponent outside <see cref="MaxComponentNumberExponent"/>, or one too large to parse. A token
    /// with no exponent, or a small one, is representable: the 8 KiB <c>components</c> cap bounds its digit count far
    /// below jsonb's 131072-digit limit.
    /// </summary>
    private static bool NumberOverflows(string raw)
    {
        var marker = raw.AsSpan().IndexOfAny('e', 'E');
        if (marker < 0)
        {
            return false;
        }

        return !long.TryParse(raw.AsSpan(marker + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var exponent)
               || exponent < -MaxComponentNumberExponent
               || exponent > MaxComponentNumberExponent;
    }

    private static Result<RunCreated> BodyInvalid(string message, IReadOnlyList<ErrorDetail> details) =>
        Result.Failure<RunCreated>(Error.Invalid(message, details));
}
