using Npgsql;
using Shouldly;

namespace Tenancy.IntegrationTests;

// Statements and seeds of the outbound-webhook schema cases (migration 0131). The tables live next to the
// evaluation tests because a delivery's event id is an evaluation id, so the seeding helpers are shared.
public sealed partial class EvaluationSchemaTests
{
    private const string EndpointInsertSql =
        """
        insert into public.webhook_endpoint
            (id, org_id, project_id, url, secret_ciphertext, secret_hint, event_types, created_by)
        values (@id, @org_id, @project_id, @url, @cipher, @hint, @events, @created_by)
        """;

    private const string DeliveryInsertSql =
        """
        insert into public.webhook_outbound_delivery (id, org_id, project_id, endpoint_id, event_type, event_id)
        values (@id, @org_id, @project_id, @endpoint_id, 'evaluation.created', @event_id)
        """;

    private static (string Name, object Value)[] EndpointParameters(
        Guid id,
        Guid org,
        Guid project,
        string url,
        Guid createdBy,
        string hint = "abcd",
        string[]? events = null,
        int cipherLength = 16) =>
    [
        ("id", id),
        ("org_id", org),
        ("project_id", project),
        ("url", url),
        ("cipher", new byte[cipherLength]),
        ("hint", hint),
        ("events", events ?? ["evaluation.created"]),
        ("created_by", createdBy),
    ];

    /// <summary>A membership id of <paramref name="org"/>: the creator an endpoint row must name.</summary>
    private Task<Guid> MemberAsync(RunCreationTests.World world, Guid org) =>
        _fixture.ScalarAsync<Guid>(world.Database, $"select id from public.membership where org_id = '{org:D}' limit 1");

    private static string FreshUrl() => "https://receiver-" + Guid.NewGuid().ToString("N")[..10] + ".example/hook";

    /// <summary>Inserts one endpoint as the app role in its own committed transaction.</summary>
    private async Task<Guid> InsertEndpointAsync(RunCreationTests.World world, Guid org, Guid project, string? url = null)
    {
        var id = Guid.CreateVersion7();
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);
        var member = await MemberAsync(world, org);
        (await scope.ExecuteAsync(EndpointInsertSql, EndpointParameters(id, org, project, url ?? FreshUrl(), member))).ShouldBe(1);
        await scope.CommitAsync();

        return id;
    }

    /// <summary>Inserts one queued delivery as the app role in its own committed transaction.</summary>
    private async Task<Guid> InsertDeliveryAsync(
        RunCreationTests.World world,
        Guid org,
        Guid project,
        Guid endpoint,
        Guid eventId)
    {
        var id = Guid.CreateVersion7();
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);
        (await scope.ExecuteAsync(DeliveryInsertSql, DeliveryParameters(id, org, project, endpoint, eventId))).ShouldBe(1);
        await scope.CommitAsync();

        return id;
    }

    private static (string Name, object Value)[] DeliveryParameters(Guid id, Guid org, Guid project, Guid endpoint, Guid eventId) =>
    [
        ("id", id),
        ("org_id", org),
        ("project_id", project),
        ("endpoint_id", endpoint),
        ("event_id", eventId),
    ];

    /// <summary>Runs one statement as the app role for <paramref name="org"/> and returns the server error.</summary>
    private async Task<PostgresException> CaptureAsAppAsync(
        RunCreationTests.World world,
        Guid org,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), org);

        return await scope.CaptureErrorAsync(sql, parameters);
    }

    /// <summary>Moves a delivery to <paramref name="state"/> as the app role, committed; the guard decides legality.</summary>
    private async Task SetDeliveryStateAsync(RunCreationTests.World world, Guid id, string state)
    {
        await using var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await scope.ExecuteAsync(
            "update public.webhook_outbound_delivery set state = @state where id = @id",
            ("state", state),
            ("id", id))).ShouldBe(1);
        await scope.CommitAsync();
    }

    /// <summary>A delivery driven queued, sending, dead_letter through the legal moves.</summary>
    private async Task<(Guid Endpoint, Guid Delivery, Guid Event)> SeedDeadLetterAsync(RunCreationTests.World world)
    {
        var evaluation = await SeedAsync(world);
        var endpoint = await InsertEndpointAsync(world, world.Org, world.Project);
        var delivery = await InsertDeliveryAsync(world, world.Org, world.Project, endpoint, evaluation.Id);
        await SetDeliveryStateAsync(world, delivery, "sending");
        await SetDeliveryStateAsync(world, delivery, "dead_letter");

        return (endpoint, delivery, evaluation.Id);
    }

    private async Task<string> DeliveryStateAsync(RunCreationTests.World world, Guid id) =>
        await _fixture.ScalarAsync<string>(
            world.Database,
            $"select state from public.webhook_outbound_delivery where id = '{id:D}'");
}
