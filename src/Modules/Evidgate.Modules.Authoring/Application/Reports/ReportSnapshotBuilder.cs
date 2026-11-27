using System.Text;
using System.Text.Json.Nodes;
using Evidgate.BuildingBlocks.Canonicalization;

namespace Evidgate.Modules.Authoring.Application.Reports;

/// <summary>A built snapshot: the canonical payload, its digest and the versions it was read at.</summary>
/// <param name="CanonicalPayload">The RFC 8785 text of the payload; the only input of every renderer.</param>
/// <param name="ContentHash">The <c>sha256:…</c> digest of <paramref name="CanonicalPayload"/>.</param>
/// <param name="PinnedVersions">The requirement and case versions the sections were built from, as a JSON object.</param>
internal sealed record ReportSnapshot(string CanonicalPayload, string ContentHash, string PinnedVersions);

/// <summary>
/// Builds the whole snapshot from a <see cref="ReportSource"/>. It is a pure function of the source and the registered
/// builders: language, scope and selection are inside the hashed payload, so two requests that differ in any of them
/// never share a hash, and one request over the same source always does.
/// </summary>
internal static class ReportSnapshotBuilder
{
    /// <summary>Runs the selected builders and canonicalizes the result; throws <see cref="ReportBuildException"/> when it cannot publish.</summary>
    /// <param name="source">The source the job read.</param>
    /// <param name="registry">The registered section builders.</param>
    /// <param name="cancellationToken">Cancelled on shutdown and on lease loss; a cancelled build produces nothing.</param>
    internal static ReportSnapshot Build(ReportSource source, ReportSectionRegistry registry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(registry);

        var request = source.Request;
        var sections = new JsonArray();
        foreach (var id in request.Sections)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var builder = registry.Find(id)
                ?? throw new ReportBuildException(ReportBuildException.Failed, "a requested section has no builder");
            sections.Add(new JsonObject { ["id"] = id, ["data"] = builder.Build(source) });
        }

        var payload = new JsonObject
        {
            ["schema_version"] = TestReportLimits.SchemaVersion,
            ["language"] = request.Language,
            ["scope"] = new JsonObject
            {
                ["kind"] = request.ScopeKind,
                ["selected_ids"] = new JsonArray([.. request.SelectedIds.Select(id => (JsonNode)id.ToString("D"))]),
            },
            ["request"] = new JsonObject { ["strategy"] = request.Strategy, ["environment"] = request.Environment },
            ["sections"] = sections,
        };

        var canonical = JsonCanonicalizer.Canonicalize(payload.ToJsonString());
        if (StoredBytes(canonical) > TestReportLimits.MaxPayloadBytes)
        {
            throw new ReportBuildException(ReportBuildException.TooLarge, "the snapshot is larger than one report may be");
        }

        return new ReportSnapshot(canonical, CanonicalHash.Compute(canonical), Pins(source));
    }

    // Ids and numbers only: the pin says which version a display value came from, never the value itself.
    private static string Pins(ReportSource source)
    {
        var requirements = new JsonArray([.. source.Requirements.Select(requirement => (JsonNode)new JsonObject
        {
            ["id"] = requirement.Id.ToString("D"),
            ["version_id"] = requirement.VersionId.ToString("D"),
            ["version_no"] = requirement.VersionNo,
            ["content_hash"] = requirement.ContentHash,
        })]);
        var cases = new JsonArray([.. source.Cases.Select(testCase => (JsonNode)new JsonObject
        {
            ["id"] = testCase.Id.ToString("D"),
            ["version_no"] = testCase.VersionNo,
            ["content_hash"] = testCase.ContentHash,
        })]);

        return new JsonObject { ["requirements"] = requirements, ["cases"] = cases }.ToJsonString();
    }

    /// <summary>
    /// The UTF-8 size of the payload as the table's size check measures it, <c>octet_length(payload::text)</c>: the
    /// canonical text plus the one space PostgreSQL writes after every <c>:</c> and <c>,</c> outside a string.
    /// </summary>
    /// <param name="canonical">The canonical payload text.</param>
    internal static long StoredBytes(string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);

        long spaces = 0;
        var inString = false;
        for (var i = 0; i < canonical.Length; i++)
        {
            var c = canonical[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }
            }
            else if (c == '"')
            {
                inString = true;
            }
            else if (c is ',' or ':')
            {
                spaces++;
            }
        }

        return Encoding.UTF8.GetByteCount(canonical) + spaces;
    }
}
