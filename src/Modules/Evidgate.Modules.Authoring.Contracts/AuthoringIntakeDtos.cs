using System.Text.Json;

namespace Evidgate.Modules.Authoring.Contracts;

/// <summary>
/// The body of <c>POST /v1/projects/{p}/authoring/intakes</c> (<c>contracts/schemas/authoring-generation-intake.v1.json</c>):
/// which members apply depends on <paramref name="Kind"/>. The handler judges the body from the raw document; this record
/// is the shape the OpenAPI document and the SDK describe.
/// </summary>
/// <param name="Kind">One of <c>text</c>, <c>docx</c>, <c>pdf</c>, <c>image</c>.</param>
/// <param name="FeatureNodeId">The feature node the drafts attach to; required except for an image, which refuses it.</param>
/// <param name="ArtifactId">The uploaded artifact of a document or image intake.</param>
/// <param name="MediaType">The declared media type, compared with the stored artifact's.</param>
/// <param name="Bytes">The declared byte count, compared with the stored artifact's and the cap.</param>
/// <param name="Text">The inline requirement of a text intake.</param>
/// <param name="TargetLanguage"><c>en</c> or <c>vi</c>; accepted for the generation request that follows and not stored on the intake.</param>
public sealed record CreateIntakeRequest(
    string? Kind,
    Guid? FeatureNodeId,
    Guid? ArtifactId,
    string? MediaType,
    long? Bytes,
    string? Text,
    string? TargetLanguage);

/// <summary>
/// The answer of <c>POST /v1/projects/{p}/authoring/intakes</c>: the new row's id and where it stands. A text or image
/// intake is already <c>ready</c>; a document waits in <c>received</c> for its extraction job.
/// </summary>
/// <param name="IntakeId">The new intake.</param>
/// <param name="State">One of <c>received</c>, <c>ready</c>.</param>
/// <param name="Caps">The limits the intake was created under.</param>
/// <param name="Version">The row version.</param>
public sealed record CreateIntakeResponse(Guid IntakeId, string State, JsonElement Caps, int Version);

/// <summary>
/// One intake as <c>GET /v1/projects/{p}/authoring/intakes/{i}</c> reports it. The extracted text is never returned:
/// only its hash and its length.
/// </summary>
/// <param name="IntakeId">The intake.</param>
/// <param name="Kind">One of <c>text</c>, <c>docx</c>, <c>pdf</c>, <c>image</c>.</param>
/// <param name="State">One of <c>received</c>, <c>extracting</c>, <c>ready</c>, <c>rejected</c>.</param>
/// <param name="EtaClass"><c>short</c> or <c>long</c> while the intake waits; absent once it is settled.</param>
/// <param name="FailureCode">The closed extraction failure code of a rejected intake.</param>
/// <param name="ExtractedTextHash"><c>sha256:&lt;hex&gt;</c> of the extracted text of a ready intake.</param>
/// <param name="ExtractedCharCount">Code points of the extracted text.</param>
/// <param name="ArtifactId">The stored artifact of a document or image intake.</param>
/// <param name="Caps">The limits the intake was created under.</param>
/// <param name="Version">The row version.</param>
public sealed record IntakeStatusResponse(
    Guid IntakeId,
    string Kind,
    string State,
    string? EtaClass,
    string? FailureCode,
    string? ExtractedTextHash,
    int? ExtractedCharCount,
    Guid? ArtifactId,
    JsonElement Caps,
    int Version);
