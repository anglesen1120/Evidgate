using System.Text.Json.Serialization;
using Evidgate.Api.Endpoints;

namespace Evidgate.Api.Json;

/// <summary>
/// Source-generated JSON contract for the Api DTOs (00 §5.5 edge: source-gen +
/// <c>SnakeCaseLower</c>). Registered first in the resolver chain; the shared
/// envelope context is <c>Evidgate.BuildingBlocks.SharedKernel.Serialization.EvidgateJsonContext</c>.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(HealthReportDto))]
public sealed partial class ApiJsonContext : JsonSerializerContext;
