using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.SharedKernel.Api;

namespace Evidgate.BuildingBlocks.SharedKernel.Serialization;

/// <summary>
/// Source-generated JSON contract for the shared wire DTOs (00 §5.5 edge:
/// <c>System.Text.Json</c> source generation with <c>SnakeCaseLower</c>). The
/// hosts insert this context first in the resolver chain, so the envelope is
/// serialized by generated code rather than reflection.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ErrorEnvelope))]
public sealed partial class EvidgateJsonContext : JsonSerializerContext;
