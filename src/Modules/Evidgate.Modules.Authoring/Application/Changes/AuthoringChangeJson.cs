using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;

namespace Evidgate.Modules.Authoring.Application.Changes;

/// <summary>
/// The JSON shapes the queue owns on the wire: the server-stamped <c>affected_versions</c> object and the payload a
/// stored row echoes back. Both are read back as typed values, never spliced into SQL text.
/// </summary>
internal static class AuthoringChangeJson
{
    /// <summary>
    /// The stamped item-to-version map, written in id order so the stored text is deterministic for one set. The
    /// keys are the item ids in "D" form — the trigger's <c>?|</c> probe compares against the same spelling.
    /// </summary>
    /// <param name="versions">One pair per affected item, already distinct and already read.</param>
    internal static string Versions(IReadOnlyList<(Guid ItemId, int Version)> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);

        var document = new JsonObject();

        foreach (var (itemId, version) in versions.OrderBy(pair => pair.ItemId))
        {
            document[itemId.ToString("D")] = version;
        }

        return document.ToJsonString();
    }

    /// <summary>
    /// The stamped map of a stored row, as a dictionary. A row whose text cannot be parsed is a bug in the writer,
    /// not input, so the caller's read throws rather than pretending the change has no affected items.
    /// </summary>
    /// <param name="text">The stored <c>affected_versions::text</c>.</param>
    internal static Dictionary<Guid, int> ParseVersions(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var versions = new Dictionary<Guid, int>();

        using var document = JsonDocument.Parse(text);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            versions[Guid.Parse(property.Name)] = property.Value.GetInt32();
        }

        return versions;
    }

    /// <summary>The stored payload as a detached <see cref="JsonElement"/> the DTO can serialize.</summary>
    /// <param name="text">The stored <c>payload::text</c>.</param>
    internal static JsonElement ParsePayload(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>One stored row as the read routes' list item, with the selection's <c>stale</c> flag when it has one.</summary>
    /// <param name="row">The Dapper row.</param>
    /// <param name="stale">True when an affected item moved, or null for a filtered page.</param>
    internal static AuthoringChangeListItem Item(AuthoringChangeSql.ChangeRow row, bool? stale)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new AuthoringChangeListItem(
            row.ChangeId,
            row.TargetKind,
            row.TargetId,
            row.ChangeType,
            ParsePayload(row.Payload),
            row.State,
            row.Source,
            row.BaseVersion,
            row.AuthorMembershipId,
            row.Reason,
            row.CreatedAt,
            row.AppliedAt,
            row.AppliedBatchId,
            row.DiscardedAt,
            stale);
    }

    /// <summary>One stored row as the detail route's body, whose <c>ETag</c> is the row's lifecycle version.</summary>
    /// <param name="row">The Dapper row.</param>
    internal static AuthoringChangeView View(AuthoringChangeSql.ChangeRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new AuthoringChangeView(
            row.ChangeId,
            row.TargetKind,
            row.TargetId,
            row.ChangeType,
            ParsePayload(row.Payload),
            row.State,
            row.Source,
            row.BaseVersion,
            row.AuthorMembershipId,
            row.Reason,
            row.CreatedAt,
            row.AppliedAt,
            row.AppliedBatchId,
            row.DiscardedAt,
            row.Version);
    }

    /// <summary>A change's own lifecycle version as the detail route's <c>ETag</c> value, without the quotes.</summary>
    /// <param name="version">The stored <c>version</c>.</param>
    internal static string Etag(int version) => version.ToString(CultureInfo.InvariantCulture);
}
