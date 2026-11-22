using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class AuthoringVersionTests
{
    // ------------------------------------------------------------------ helpers

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static JsonElement NodeContent(string kind, string title, int position, Guid? parentId) => Json(
        "{\"kind\":\"" + kind + "\",\"title\":\"" + title + "\",\"position\":" + position.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ",\"parent_id\":" + (parentId is { } parent ? "\"" + parent.ToString("D") + "\"" : "null") + "}");

    private static JsonElement ItemContent(Guid nodeId, string title, int position, string state = ChecklistItemStateMachine.Active) => Json(
        "{\"feature_node_id\":\"" + nodeId.ToString("D") + "\",\"title\":\"" + title
        + "\",\"description\":null,\"priority\":\"high\",\"state\":\"" + state + "\",\"position\":"
        + position.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");

    private static JsonElement CaseContent(Guid itemId, string code, string title, string state = ManualTestCaseStateMachine.Draft) => Json(
        "{\"checklist_item_id\":\"" + itemId.ToString("D") + "\",\"code\":\"" + code + "\",\"title\":\"" + title
        + "\",\"description\":null,\"precondition\":null,"
        + "\"steps\":{\"steps\":[{\"no\":1,\"action\":\"Open the cart\",\"expected\":\"The cart is shown\"}]},"
        + "\"priority\":\"high\",\"state\":\"" + state + "\"}");

    private static JsonElement CaseWithSteps(Guid itemId, string code, string title, string stepsDocument) => Json(
        "{\"checklist_item_id\":\"" + itemId.ToString("D") + "\",\"code\":\"" + code + "\",\"title\":\"" + title
        + "\",\"description\":null,\"precondition\":null,\"steps\":" + stepsDocument
        + ",\"priority\":\"high\",\"state\":\"" + ManualTestCaseStateMachine.Draft + "\"}");

    private static async Task<Result<AuthoringAppendOutcome>> AppendAsync(
        World world,
        Guid batchId,
        IReadOnlyList<EntityChange> changes)
    {
        using var services = world.Provider.CreateScope();
        var factory = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await factory.BeginAsync(world.Org, world.Project, false, TestContext.Current.CancellationToken);
        var writer = services.ServiceProvider.GetRequiredService<AuthoringVersionWriter>();
        var result = await writer.AppendAsync(
            world.Org,
            world.Project,
            batchId,
            null,
            changes,
            null,
            TestContext.Current.CancellationToken);

        if (result.IsSuccess)
        {
            await scope.CommitAsync(TestContext.Current.CancellationToken);
        }

        return result;
    }

    private static async Task<Result<AuthoringMoveOutcome>> MoveAsync(
        World world,
        Guid caseId,
        Guid toItemId,
        string? reason)
    {
        using var services = world.Provider.CreateScope();
        var factory = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await factory.BeginAsync(world.Org, world.Project, false, TestContext.Current.CancellationToken);
        var writer = services.ServiceProvider.GetRequiredService<AuthoringVersionWriter>();
        var result = await writer.MoveManualTestCaseAsync(
            world.Org,
            world.Project,
            Guid.CreateVersion7(),
            null,
            caseId,
            toItemId,
            reason,
            null,
            TestContext.Current.CancellationToken);

        if (result.IsSuccess)
        {
            await scope.CommitAsync(TestContext.Current.CancellationToken);
        }

        return result;
    }

    private static async Task<Result<AuthoringVersionRef>> RestoreAsync(
        World world,
        AuthoringEntityKind kind,
        Guid entityId)
    {
        using var services = world.Provider.CreateScope();
        var factory = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await factory.BeginAsync(world.Org, world.Project, false, TestContext.Current.CancellationToken);
        var writer = services.ServiceProvider.GetRequiredService<AuthoringVersionWriter>();
        var result = await writer.RestoreAsync(
            world.Org,
            world.Project,
            Guid.CreateVersion7(),
            null,
            kind,
            entityId,
            expectedVersion: null,
            reason: "restore",
            fromVersionId: null,
            context: null,
            TestContext.Current.CancellationToken);

        if (result.IsSuccess)
        {
            await scope.CommitAsync(TestContext.Current.CancellationToken);
        }

        return result;
    }

    private static async Task<Result<AuthoringCode>> AllocateAsync(World world, bool commit)
    {
        using var services = world.Provider.CreateScope();
        var factory = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await factory.BeginAsync(world.Org, world.Project, false, TestContext.Current.CancellationToken);
        var allocator = services.ServiceProvider.GetRequiredService<AuthoringCodeAllocator>();
        var result = await allocator.AllocateAsync(world.Org, world.Project, TestContext.Current.CancellationToken);

        if (result.IsSuccess && commit)
        {
            await scope.CommitAsync(TestContext.Current.CancellationToken);
        }

        return result;
    }

    private static async Task<Result<AuthoringCode>> AllocateWithAsync(ServiceProvider provider, World world, bool commit)
    {
        using var services = provider.CreateScope();
        var factory = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await factory.BeginAsync(world.Org, world.Project, false, TestContext.Current.CancellationToken);
        var allocator = services.ServiceProvider.GetRequiredService<AuthoringCodeAllocator>();
        var result = await allocator.AllocateAsync(world.Org, world.Project, TestContext.Current.CancellationToken);

        if (result.IsSuccess && commit)
        {
            await scope.CommitAsync(TestContext.Current.CancellationToken);
        }

        return result;
    }
}
