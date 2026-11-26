using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

// Migration 0131 through a real PostgreSQL: the endpoint and outbound-delivery tables, their guards, grants,
// tenant isolation and the own-org replay function.
public sealed partial class EvaluationSchemaTests
{
    [Fact]
    public async Task WebhookTables_Migration_AppliesBoth_WithForcedRlsAndTenantPolicy()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        foreach (var table in new[] { "webhook_endpoint", "webhook_outbound_delivery" })
        {
            (await _fixture.TableExistsAsync(world.Database, table)).ShouldBeTrue();
            (await _fixture.RowSecurityAsync(world.Database, table)).ShouldBe((true, true));
            (await _fixture.PoliciesAsync(world.Database, table))
                .ShouldContain(policy => policy.StartsWith(table + "_app_tenant:evidgate_app:", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task WebhookTables_Grants_AppHasNoDelete_OtherRolesNothing_ReplayIsAppOnly()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        foreach (var table in new[] { "webhook_endpoint", "webhook_outbound_delivery" })
        {
            (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", table, "select")).ShouldBeTrue();
            (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", table, "delete")).ShouldBeFalse();
            (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_operator", table, "select")).ShouldBeFalse();
            (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_worker_sched", table, "select")).ShouldBeFalse();
        }

        const string signature = "evidgate.replay_webhook_delivery(uuid, uuid)";
        (await _fixture.ScalarAsync<bool>(world.Database, $"select has_function_privilege('evidgate_app', '{signature}', 'execute')"))
            .ShouldBeTrue();
        (await _fixture.ScalarAsync<bool>(world.Database, $"select has_function_privilege('evidgate_operator', '{signature}', 'execute')"))
            .ShouldBeFalse();
        (await _fixture.ScalarAsync<bool>(world.Database, $"select has_function_privilege('evidgate_worker_sched', '{signature}', 'execute')"))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task WebhookEndpoint_CrossTenantProject_RefusedByCompositeKey()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var otherOrg = await CreateSecondTenantAsync(world, "wh-other-" + Guid.NewGuid().ToString("N")[..8]);
        var otherProject = await _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.project where org_id = '{otherOrg:D}' limit 1");

        (await InsertEndpointAsync(world, world.Org, world.Project)).ShouldNotBe(Guid.Empty);

        var error = await CaptureAsAppAsync(
            world,
            world.Org,
            EndpointInsertSql,
            EndpointParameters(Guid.CreateVersion7(), world.Org, otherProject, FreshUrl(), await MemberAsync(world, world.Org)));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("webhook_endpoint_project_fk");
    }

    [Fact]
    public async Task WebhookEndpoint_CreatorOutsideTheOrg_RefusedByMembershipKey()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var otherOrg = await CreateSecondTenantAsync(world, "wh-member-" + Guid.NewGuid().ToString("N")[..8]);

        var error = await CaptureAsAppAsync(
            world,
            world.Org,
            EndpointInsertSql,
            EndpointParameters(Guid.CreateVersion7(), world.Org, world.Project, FreshUrl(), await MemberAsync(world, otherOrg)));

        error.SqlState.ShouldBe("23503");
        error.ConstraintName.ShouldBe("webhook_endpoint_created_by_fk");
    }

    [Fact]
    public async Task WebhookEndpoint_LiveUrl_IsUnique_AndRegistrableAgainAfterDelete()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var url = FreshUrl();
        var first = await InsertEndpointAsync(world, world.Org, world.Project, url);

        var duplicate = await CaptureAsAppAsync(
            world,
            world.Org,
            EndpointInsertSql,
            EndpointParameters(Guid.CreateVersion7(), world.Org, world.Project, url, await MemberAsync(world, world.Org)));
        duplicate.SqlState.ShouldBe("23505");
        duplicate.ConstraintName.ShouldBe("webhook_endpoint_live_url_key");

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync(
                "update public.webhook_endpoint set state = 'deleted', deleted_at = now() where id = @id",
                ("id", first))).ShouldBe(1);
            await scope.CommitAsync();
        }

        (await InsertEndpointAsync(world, world.Org, world.Project, url)).ShouldNotBe(first);
    }

    [Fact]
    public async Task WebhookEndpoint_StateMoves_FollowTheLegalGraph_AndDeletedIsTerminal()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var id = await InsertEndpointAsync(world, world.Org, world.Project);

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ExecuteAsync("update public.webhook_endpoint set state = 'disabled' where id = @id", ("id", id))).ShouldBe(1);
            (await scope.ExecuteAsync("update public.webhook_endpoint set state = 'active' where id = @id", ("id", id))).ShouldBe(1);
            (await scope.ExecuteAsync(
                "update public.webhook_endpoint set state = 'deleted', deleted_at = now() where id = @id",
                ("id", id))).ShouldBe(1);
            await scope.CommitAsync();
        }

        var revived = await CaptureAsAppAsync(
            world,
            world.Org,
            "update public.webhook_endpoint set state = 'active', deleted_at = null where id = @id",
            ("id", id));
        revived.SqlState.ShouldBe("23514");
        revived.MessageText.ShouldContain("webhook_endpoint_state_invalid");

        (await CaptureAsAppAsync(world, world.Org, "delete from public.webhook_endpoint")).SqlState.ShouldBe("42501");
        (await CaptureOwnerErrorAsync(world, "delete from public.webhook_endpoint where id = @id", ("id", id)))
            .SqlState.ShouldBe("23514");
    }

    [Theory]
    [InlineData("http://receiver.example/hook", "abcd", "evaluation.created", 16, "webhook_endpoint_url_check")]
    [InlineData("https://receiver.example/hook", "abc", "evaluation.created", 16, "webhook_endpoint_secret_hint_check")]
    [InlineData("https://receiver.example/hook", "abcd", "run.finished", 16, "webhook_endpoint_event_types_check")]
    [InlineData("https://receiver.example/hook", "abcd", "evaluation.created", 0, "webhook_endpoint_secret_ciphertext_check")]
    public async Task WebhookEndpoint_BadShape_RefusedByNamedCheck(
        string url,
        string hint,
        string eventType,
        int cipherLength,
        string constraint)
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);

        var error = await CaptureAsAppAsync(
            world,
            world.Org,
            EndpointInsertSql,
            EndpointParameters(
                Guid.CreateVersion7(),
                world.Org,
                world.Project,
                url,
                await MemberAsync(world, world.Org),
                hint,
                [eventType],
                cipherLength));

        error.SqlState.ShouldBe("23514");
        error.ConstraintName.ShouldBe(constraint);
    }

    [Fact]
    public async Task WebhookDelivery_Keys_OneRowPerEndpointAndEvent_AndEventMustBeAnEvaluation()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var evaluation = await SeedAsync(world);
        var endpoint = await InsertEndpointAsync(world, world.Org, world.Project);
        await InsertDeliveryAsync(world, world.Org, world.Project, endpoint, evaluation.Id);

        var duplicate = await CaptureAsAppAsync(
            world,
            world.Org,
            DeliveryInsertSql,
            DeliveryParameters(Guid.CreateVersion7(), world.Org, world.Project, endpoint, evaluation.Id));
        duplicate.SqlState.ShouldBe("23505");
        duplicate.ConstraintName.ShouldBe("webhook_outbound_delivery_event_key");

        var notAnEvaluation = await CaptureAsAppAsync(
            world,
            world.Org,
            DeliveryInsertSql,
            DeliveryParameters(Guid.CreateVersion7(), world.Org, world.Project, endpoint, Guid.CreateVersion7()));
        notAnEvaluation.SqlState.ShouldBe("23503");
        notAnEvaluation.ConstraintName.ShouldBe("webhook_outbound_delivery_evaluation_fk");
    }

    [Fact]
    public async Task WebhookRows_AnotherTenant_SeesNothing_AndCannotWriteOrReference()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var otherOrg = await CreateSecondTenantAsync(world, "wh-iso-" + Guid.NewGuid().ToString("N")[..8]);
        var otherProject = await _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.project where org_id = '{otherOrg:D}' limit 1");
        var evaluation = await SeedAsync(world);
        var endpoint = await InsertEndpointAsync(world, world.Org, world.Project);
        await InsertDeliveryAsync(world, world.Org, world.Project, endpoint, evaluation.Id);

        (await ScalarAsOrgAsync<long>(world, world.Org, "select count(*) from public.webhook_endpoint")).ShouldBe(1);
        (await ScalarAsOrgAsync<long>(world, world.Org, "select count(*) from public.webhook_outbound_delivery")).ShouldBe(1);
        (await ScalarAsOrgAsync<long>(world, otherOrg, "select count(*) from public.webhook_endpoint")).ShouldBe(0);
        (await ScalarAsOrgAsync<long>(world, otherOrg, "select count(*) from public.webhook_outbound_delivery")).ShouldBe(0);

        // A row for the other org written from this tenant's scope fails the policy's WITH CHECK.
        (await CaptureAsAppAsync(
                world,
                world.Org,
                EndpointInsertSql,
                EndpointParameters(Guid.CreateVersion7(), otherOrg, otherProject, FreshUrl(), await MemberAsync(world, otherOrg))))
            .SqlState.ShouldBe("42501");

        // The other tenant cannot hang a delivery on this tenant's endpoint: the composite key names its own org.
        var crossed = await CaptureAsAppAsync(
            world,
            otherOrg,
            DeliveryInsertSql,
            DeliveryParameters(Guid.CreateVersion7(), otherOrg, otherProject, endpoint, evaluation.Id));
        crossed.SqlState.ShouldBe("23503");
    }

    [Fact]
    public async Task WebhookDelivery_StateMoves_FollowTheLegalGraph()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var evaluation = await SeedAsync(world);
        var endpoint = await InsertEndpointAsync(world, world.Org, world.Project);
        var delivery = await InsertDeliveryAsync(world, world.Org, world.Project, endpoint, evaluation.Id);
        const string move = "update public.webhook_outbound_delivery set state = @state where id = @id";

        var skipped = await CaptureAsAppAsync(world, world.Org, move, ("state", "delivered"), ("id", delivery));
        skipped.SqlState.ShouldBe("23514");
        skipped.MessageText.ShouldContain("webhook_outbound_delivery_state_invalid");

        await SetDeliveryStateAsync(world, delivery, "sending");
        await SetDeliveryStateAsync(world, delivery, "retrying");
        await SetDeliveryStateAsync(world, delivery, "sending");
        await SetDeliveryStateAsync(world, delivery, "delivered");

        (await CaptureAsAppAsync(world, world.Org, move, ("state", "sending"), ("id", delivery))).SqlState.ShouldBe("23514");
        (await CaptureAsAppAsync(world, world.Org, "delete from public.webhook_outbound_delivery")).SqlState.ShouldBe("42501");
        (await CaptureOwnerErrorAsync(world, "delete from public.webhook_outbound_delivery where id = @id", ("id", delivery)))
            .SqlState.ShouldBe("23514");
    }

    [Fact]
    public async Task WebhookDelivery_DeadLetter_AppRoleCannotRequeue()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var seeded = await SeedDeadLetterAsync(world);

        var error = await CaptureAsAppAsync(
            world,
            world.Org,
            "update public.webhook_outbound_delivery set state = 'queued' where id = @id",
            ("id", seeded.Delivery));

        error.SqlState.ShouldBe("23514");
        error.MessageText.ShouldContain("replay_webhook_delivery");
        (await DeliveryStateAsync(world, seeded.Delivery)).ShouldBe("dead_letter");
    }

    [Fact]
    public async Task Retention_TiedToEvaluationLifetime()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var evaluation = await SeedAsync(world);
        var endpoint = await InsertEndpointAsync(world, world.Org, world.Project);
        var delivery = await InsertDeliveryAsync(world, world.Org, world.Project, endpoint, evaluation.Id);

        // The key to evaluation is NO ACTION: the row cannot outlive its evaluation and nothing cascades.
        (await _fixture.ScalarAsync<string>(
            world.Database,
            """
            select confdeltype::text || ':' || confrelid::regclass::text
              from pg_catalog.pg_constraint
             where conname = 'webhook_outbound_delivery_evaluation_fk'
            """)).ShouldBe("a:evaluation");

        // No expiry column and no delete path: only the evaluation's own lifetime bounds the delivery.
        (await _fixture.ScalarAsync<long>(
            world.Database,
            """
            select count(*)
              from information_schema.columns
             where table_schema = 'public' and table_name = 'webhook_outbound_delivery'
               and column_name ~ '(expire|expiry|retain|retention|purge|ttl)'
            """)).ShouldBe(0);
        (await _fixture.HasTablePrivilegeAsync(world.Database, "evidgate_app", "webhook_outbound_delivery", "delete"))
            .ShouldBeFalse();

        (await CaptureOwnerErrorAsync(world, "delete from public.evaluation where id = @id", ("id", evaluation.Id)))
            .SqlState.ShouldBe("23514");
        (await DeliveryStateAsync(world, delivery)).ShouldBe("queued");
    }

    [Fact]
    public async Task Replay_OwnOrg_RequeuesDeadLetterAndAuditsOnce()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var seeded = await SeedDeadLetterAsync(world);
        var actor = Guid.CreateVersion7();

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await scope.ScalarAsync<Guid>(
                "select delivery_id from evidgate.replay_webhook_delivery(@event, @actor)",
                ("event", seeded.Event),
                ("actor", actor))).ShouldBe(seeded.Delivery);
            await scope.CommitAsync();
        }

        (await DeliveryStateAsync(world, seeded.Delivery)).ShouldBe("queued");
        (await ScalarAsOrgAsync<long>(
            world,
            world.Org,
            $"select count(*) from public.audit_log where action = 'webhook_delivery.replay' and object_id = '{seeded.Delivery:D}' and actor_id = '{actor:D}'"))
            .ShouldBe(1);

        // Nothing is left in dead_letter, so a second replay changes and audits nothing.
        await using var again = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org);
        (await again.ScalarAsync<long>(
            "select count(*) from evidgate.replay_webhook_delivery(@event, @actor)",
            ("event", seeded.Event),
            ("actor", actor))).ShouldBe(0);
    }

    [Fact]
    public async Task Replay_OtherOrgOrNoTenantOrDisabledEndpoint_ChangesNothing()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        var otherOrg = await CreateSecondTenantAsync(world, "wh-replay-" + Guid.NewGuid().ToString("N")[..8]);
        var seeded = await SeedDeadLetterAsync(world);
        var actor = Guid.CreateVersion7();
        const string replay = "select count(*) from evidgate.replay_webhook_delivery(@event, @actor)";

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), otherOrg))
        {
            (await scope.ScalarAsync<long>(replay, ("event", seeded.Event), ("actor", actor))).ShouldBe(0);
            await scope.CommitAsync();
        }

        await using (var unscoped = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database)))
        {
            var refused = await unscoped.CaptureErrorAsync(replay, ("event", seeded.Event), ("actor", actor));
            refused.SqlState.ShouldBe("42501");
        }

        await using (var scope = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            await scope.ExecuteAsync("update public.webhook_endpoint set state = 'disabled' where id = @id", ("id", seeded.Endpoint));
            (await scope.ScalarAsync<long>(replay, ("event", seeded.Event), ("actor", actor))).ShouldBe(0);
            await scope.CommitAsync();
        }

        (await DeliveryStateAsync(world, seeded.Delivery)).ShouldBe("dead_letter");
        (await ScalarAsOrgAsync<long>(
            world,
            world.Org,
            "select count(*) from public.audit_log where action = 'webhook_delivery.replay'"))
            .ShouldBe(0);
    }
}
