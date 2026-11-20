using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using FluentValidation;
using Mediator;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application.Commands;

/// <summary>
/// The one builder of a node's <c>content</c> snapshot (§4: <c>{kind, title, position, parent_id}</c>) as the
/// <see cref="JsonElement"/> an <see cref="EntityChange"/> carries, plus the <c>for update</c> read the two single-node
/// writes share.
/// </summary>
internal static class NodeContent
{
    /// <summary>The content object of a node, NFC-normalised exactly as the writer will validate it.</summary>
    /// <param name="kind">A valid kind.</param>
    /// <param name="title">The title.</param>
    /// <param name="position">The sibling position.</param>
    /// <param name="parentId">The parent, or null for a root.</param>
    internal static JsonElement Of(string kind, string title, int position, Guid? parentId)
    {
        using var document = JsonDocument.Parse(
            AuthoringContent.FeatureNode(kind, title, position, parentId).ToJsonString());

        return document.RootElement.Clone();
    }
}
