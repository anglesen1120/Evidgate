using System.Text.Json;

namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>
/// The body of <c>POST /v1/projects/{p}/runs</c> (05.2.1 §5). The wire names are <c>manifest_id</c>, <c>release</c> and
/// <c>components</c> (the host's <c>SnakeCaseLower</c> policy). <c>release</c> is syntactically validated but resolves
/// to 422 <c>release_unresolvable</c> at M1 (AC10); <c>components</c> is an optional JSON object.
/// </summary>
/// <param name="ManifestId">The verified manifest the run is created from.</param>
/// <param name="Release">Optional release name; never resolvable at M1.</param>
/// <param name="Components">Optional free-form component map (build identifiers); never personal data.</param>
public sealed record CreateRunRequest(Guid ManifestId, string? Release, JsonElement? Components);

/// <summary>The 201 body of <c>POST /v1/projects/{p}/runs</c> (05.2.1 §5): the new run and its state, always <c>queued</c>.</summary>
/// <param name="RunId">The created run.</param>
/// <param name="State">Its state; <c>queued</c> because the producer slots commit in the create transaction (T13).</param>
public sealed record RunCreated(Guid RunId, string State);
