using System.Globalization;
using System.Text.Json.Nodes;
using Evidgate.BuildingBlocks.Canonicalization;
using Evidgate.BuildingBlocks.Persistence.Audit;

namespace Evidgate.Modules.Authoring.Application.Commands;

/// <summary>
/// What a requirement write tells the audit trail. Only ids, version numbers and content digests travel: a title, a
/// body or a reason is customer text and never goes into a row that cannot be edited afterwards. The version pin is
/// <c>previous_id</c> (the head version the write moved from or hid) and <c>version_no</c> (the number it moved to, or
/// the head number a delete hid); the version id of that number is the requirement's own history row.
/// </summary>
internal static class RequirementAudit
{
    /// <summary>A new requirement has no previous head; its first version supplies the digest and number.</summary>
    /// <param name="projectId">The project of the requirement.</param>
    /// <param name="created">The first head the write produced.</param>
    internal static AuditObject Created(Guid projectId, RequirementHead created) =>
        new(
            "requirement",
            created.RequirementId.ToString("D"),
            AfterHash: Digest(created.ContentHash),
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["version_no"] = created.VersionNo.ToString(CultureInfo.InvariantCulture),
            },
            ProjectId: projectId);

    /// <summary>A head-moving write (edit or restore): the previous head is the pin it moved from.</summary>
    /// <param name="projectId">The project of the requirement.</param>
    /// <param name="requirementId">The requirement.</param>
    /// <param name="previous">The head before the write.</param>
    /// <param name="written">The head the write produced.</param>
    internal static AuditObject Appended(Guid projectId, Guid requirementId, RequirementRow previous, RequirementHead written) =>
        new(
            "requirement",
            requirementId.ToString("D"),
            BeforeHash: Digest(previous.ContentHash),
            AfterHash: Digest(written.ContentHash),
            Metadata: Pin(previous.HeadVersionId, written.VersionNo),
            ProjectId: projectId);

    /// <summary>A soft delete: the head it hid is the pin.</summary>
    /// <param name="projectId">The project of the requirement.</param>
    /// <param name="requirementId">The requirement.</param>
    /// <param name="hidden">The head the delete hid.</param>
    internal static AuditObject Hidden(Guid projectId, Guid requirementId, RequirementHead hidden) =>
        new(
            "requirement",
            requirementId.ToString("D"),
            BeforeHash: Digest(hidden.ContentHash),
            Metadata: Pin(hidden.VersionId, hidden.VersionNo),
            ProjectId: projectId);

    /// <summary>A link that was created.</summary>
    /// <param name="projectId">The project of the requirement.</param>
    /// <param name="link">The link as stored.</param>
    internal static AuditObject Linked(Guid projectId, RequirementLinkRow link) =>
        Association(projectId, link.LinkId, link.RequirementId, link.VersionId, link.VersionNo, link.ChecklistItemId, link.ManualTestCaseId, removed: false);

    /// <summary>A link that was removed, as it looked when it went.</summary>
    /// <param name="projectId">The project of the requirement.</param>
    /// <param name="requirementId">The requirement from the route.</param>
    /// <param name="link">The deleted row.</param>
    internal static AuditObject Unlinked(Guid projectId, Guid requirementId, RequirementLinkRemoved link) =>
        Association(projectId, link.LinkId, requirementId, link.VersionId, link.VersionNo, link.ChecklistItemId, link.ManualTestCaseId, removed: true);

    // The digest is of the whole association (requirement, pinned version, target) so a row can be checked against
    // it; the readable metadata names the requirement, the pinned number and the target column and id.
    private static AuditObject Association(
        Guid projectId,
        Guid linkId,
        Guid requirementId,
        Guid versionId,
        int versionNo,
        Guid? checklistItemId,
        Guid? manualTestCaseId,
        bool removed)
    {
        var digest = Digest(CanonicalHash.Compute(new JsonObject
        {
            ["requirement_id"] = requirementId.ToString("D"),
            ["requirement_version_id"] = versionId.ToString("D"),
            ["checklist_item_id"] = checklistItemId?.ToString("D"),
            ["manual_test_case_id"] = manualTestCaseId?.ToString("D"),
        }.ToJsonString()));
        var metadata = Pin(requirementId, versionNo);
        metadata["fields"] = checklistItemId is { } item
            ? "checklist_item_id:" + item.ToString("D")
            : "manual_test_case_id:" + manualTestCaseId!.Value.ToString("D");

        return new AuditObject(
            "requirement_link",
            linkId.ToString("D"),
            BeforeHash: removed ? digest : null,
            AfterHash: removed ? null : digest,
            Metadata: metadata,
            ProjectId: projectId);
    }

    private static Dictionary<string, string> Pin(Guid previousId, int versionNo) => new(StringComparer.Ordinal)
    {
        ["previous_id"] = previousId.ToString("D"),
        ["version_no"] = versionNo.ToString(CultureInfo.InvariantCulture),
    };

    private static byte[] Digest(string contentHash) => Convert.FromHexString(contentHash[CanonicalHash.Prefix.Length..]);
}
