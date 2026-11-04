using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class AiSettingsHistorySafetyTests(PostgresTenancyFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_StaleTagWithOlderOrCurrentContent_Is412BeforeNoOp(bool matchesCurrent)
    {
        await using var world = await AiSettingsCalls.StartAsync(fixture);
        var older = Body("Older guidance");
        var current = Body("Current guidance");
        (await AiSettingsCalls.SaveAsync(world.Client, world.As, world, older, "\"0\""))
            .Status.ShouldBe(HttpStatusCode.Created);
        (await AiSettingsCalls.SaveAsync(world.Client, world.As, world, current, "\"1\""))
            .Status.ShouldBe(HttpStatusCode.Created);
        var auditBefore = await AiSettingsCalls.AuditCountAsync(fixture, world);
        var idempotencyBefore = await AiSettingsCalls.IdempotencyCountAsync(fixture, world);
        var storedBefore = await AiSettingsCalls.StoredAsync(fixture, world, 2);
        var body = matchesCurrent ? current : older;

        var stale = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, body, "\"1\"");

        stale.Status.ShouldBe(HttpStatusCode.PreconditionFailed, stale.ErrorMessage);
        stale.ErrorCode.ShouldBe("precondition_failed");
        AiSettingsCalls.Details(stale).ShouldContain(("version", "2"));
        (await AiSettingsCalls.SettingsCountAsync(fixture, world)).ShouldBe(2);
        (await AiSettingsCalls.StoredAsync(fixture, world, 2)).ContentHash.ShouldBe(storedBefore.ContentHash);
        (await AiSettingsCalls.AuditCountAsync(fixture, world)).ShouldBe(auditBefore);
        (await AiSettingsCalls.IdempotencyCountAsync(fixture, world)).ShouldBe(idempotencyBefore);

        var fresh = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, body, "\"2\"");
        fresh.Status.ShouldBe(matchesCurrent ? HttpStatusCode.OK : HttpStatusCode.Created);
        fresh.Body.GetProperty("version_no").GetInt32().ShouldBe(matchesCurrent ? 2 : 3);
    }

    [Fact]
    public async Task Save_ReusedKeyWithDifferentDocument_IsConflictWithoutWrites()
    {
        await using var world = await AiSettingsCalls.StartAsync(fixture);
        var key = RegistryApi.NewKey();
        var first = Body("Original guidance");
        var created = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, first, "\"0\"", key);
        created.Status.ShouldBe(HttpStatusCode.Created);
        var idempotencyBefore = await AiSettingsCalls.IdempotencyCountAsync(fixture, world);

        var conflict = await AiSettingsCalls.SaveAsync(
            world.Client, world.As, world, Body("Changed guidance"), "\"0\"", key);

        conflict.Status.ShouldBe(HttpStatusCode.Conflict, conflict.ErrorMessage);
        conflict.ErrorCode.ShouldBe("idempotency_conflict");
        (await AiSettingsCalls.SettingsCountAsync(fixture, world)).ShouldBe(1);
        (await AiSettingsCalls.AuditCountAsync(fixture, world)).ShouldBe(1);
        (await AiSettingsCalls.IdempotencyCountAsync(fixture, world)).ShouldBe(idempotencyBefore);
        var replay = await AiSettingsCalls.SaveAsync(world.Client, world.As, world, first, "\"0\"", key);
        replay.Status.ShouldBe(created.Status);
        replay.Text("content_hash").ShouldBe(created.Text("content_hash"));
    }

    [Fact]
    public async Task Reads_ExistingForeignVersions_Are404WithOwnPositiveControls()
    {
        await using var own = await AiSettingsCalls.StartAsync(fixture);
        await using var foreign = await AiSettingsCalls.StartAsync(fixture);
        (await AiSettingsCalls.SaveAsync(own.Client, own.As, own, Body("Own guidance"), "\"0\""))
            .Status.ShouldBe(HttpStatusCode.Created);
        (await AiSettingsCalls.SaveAsync(foreign.Client, foreign.As, foreign, Body("Foreign guidance"), "\"0\""))
            .Status.ShouldBe(HttpStatusCode.Created);

        foreach (var url in new[] { foreign.Settings, foreign.Versions, foreign.VersionUrl(1), foreign.Templates })
        {
            (await AiSettingsCalls.GetAsync(foreign.Client, foreign.As, url)).Status.ShouldBe(HttpStatusCode.OK);
            var hidden = await AiSettingsCalls.GetAsync(own.Client, own.As, url);
            hidden.Status.ShouldBe(HttpStatusCode.NotFound, url);
            hidden.ErrorCode.ShouldBe("not_found");
            hidden.Body.GetRawText().ShouldNotContain("Foreign guidance");
        }

        var sibling = await RegistryApi.CreateProjectAsync(own.Client, own.User, own.Org, Unique.Slug("sibling"));
        var siblingRoot = $"/v1/projects/{sibling}/ai-settings";
        var siblingHead = await AiSettingsCalls.GetAsync(own.Client, own.As, siblingRoot);
        siblingHead.Status.ShouldBe(HttpStatusCode.OK);
        siblingHead.Body.GetProperty("version_no").GetInt32().ShouldBe(0);
        (await AiSettingsCalls.GetAsync(own.Client, own.As, siblingRoot + "/versions/1"))
            .Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("qa_engineer")]
    [InlineData("viewer")]
    [InlineData("release_approver")]
    public async Task ReadOnlyMember_AllFourReadsSucceed_SaveIsForbidden(string role)
    {
        await using var world = await AiSettingsCalls.StartAsync(fixture);
        (await AiSettingsCalls.SaveAsync(world.Client, world.As, world, Body("Visible guidance"), "\"0\""))
            .Status.ShouldBe(HttpStatusCode.Created);
        var member = await AiSettingsCalls.AddMemberAsync(fixture, world, role);
        var caller = world.CallerFor(member);

        foreach (var url in new[] { world.Settings, world.Versions, world.VersionUrl(1), world.Templates })
        {
            (await AiSettingsCalls.GetAsync(world.Client, caller, url)).Status.ShouldBe(HttpStatusCode.OK, url);
        }

        var refused = await AiSettingsCalls.SaveAsync(world.Client, caller, world, Body("Changed guidance"), "\"1\"");
        refused.Status.ShouldBe(HttpStatusCode.Forbidden);
        refused.ErrorCode.ShouldBe("forbidden");
        (await AiSettingsCalls.SettingsCountAsync(fixture, world)).ShouldBe(1);
        (await AiSettingsCalls.SaveAsync(world.Client, world.As, world, Body("Changed guidance"), "\"1\""))
            .Status.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task ServicePrincipal_WithMemberPermissionClaims_CannotReadOrSave()
    {
        await using var world = await AiSettingsCalls.StartAsync(fixture);
        (await AiSettingsCalls.SaveAsync(world.Client, world.As, world, Body("Visible guidance"), "\"0\""))
            .Status.ShouldBe(HttpStatusCode.Created);
        var service = TenancyApiFactory.Resolved(
            serviceOrg: world.Org, serviceProject: world.Project, servicePermissions: ["authoring:read", "project:write"]);

        foreach (var url in new[] { world.Settings, world.Versions, world.VersionUrl(1), world.Templates })
        {
            (await AiSettingsCalls.GetAsync(world.Client, world.As, url)).Status.ShouldBe(HttpStatusCode.OK);
            (await AiSettingsCalls.GetAsync(world.Client, service, url)).Status.ShouldBe(HttpStatusCode.Forbidden);
        }

        (await AiSettingsCalls.SaveAsync(world.Client, service, world, Body("Changed guidance"), "\"1\""))
            .Status.ShouldBe(HttpStatusCode.Forbidden);
        (await AiSettingsCalls.SettingsCountAsync(fixture, world)).ShouldBe(1);
    }

    [Fact]
    public async Task AppReads_TenantScopeCannotSelectForeignRows()
    {
        await using var own = await AiSettingsCalls.StartAsync(fixture);
        await using var foreign = await AiSettingsCalls.StartAsync(fixture);
        (await AiSettingsCalls.SaveAsync(own.Client, own.As, own, Body("Own guidance"), "\"0\""))
            .Status.ShouldBe(HttpStatusCode.Created);
        (await AiSettingsCalls.SaveAsync(foreign.Client, foreign.As, foreign, Body("Foreign guidance"), "\"0\""))
            .Status.ShouldBe(HttpStatusCode.Created);
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString(own.Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await SetScopeAsync(connection, transaction, own.Org);
        await using var command = new NpgsqlCommand(
            "select count(*) from public.project_ai_settings where org_id = @org", connection, transaction);
        command.Parameters.AddWithValue("org", own.Org);
        Convert.ToInt64(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken),
            CultureInfo.InvariantCulture).ShouldBe(1);
        command.Parameters["org"].Value = foreign.Org;
        Convert.ToInt64(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken),
            CultureInfo.InvariantCulture).ShouldBe(0);
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("truncate public.project_ai_settings")]
    [InlineData("update public.project_ai_settings set description = description where org_id = @org")]
    [InlineData("delete from public.project_ai_settings where org_id = @org")]
    [InlineData("insert into public.project_ai_settings select * from public.project_ai_settings where org_id = @org on conflict (id) do update set description = excluded.description")]
    public async Task AppMutation_AppendOnlyBypass_IsDenied(string sql)
    {
        await using var world = await AiSettingsCalls.StartAsync(fixture);
        (await AiSettingsCalls.SaveAsync(world.Client, world.As, world, Body("Preserved guidance"), "\"0\""))
            .Status.ShouldBe(HttpStatusCode.Created);
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString(world.Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await SetScopeAsync(connection, transaction, world.Org);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        if (sql.Contains("@org", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("org", world.Org);
        }

        var exception = await Should.ThrowAsync<PostgresException>(async () =>
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        exception.SqlState.ShouldBe("42501");
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        (await AiSettingsCalls.StoredAsync(fixture, world, 1)).Document["description"]?.GetValue<string>()
            .ShouldBe("Preserved guidance");
    }

    [Fact]
    public async Task OwnerMutation_ConflictUpdateIsGuarded_DeleteStillWorks()
    {
        await using var world = await AiSettingsCalls.StartAsync(fixture);
        (await AiSettingsCalls.SaveAsync(world.Client, world.As, world, Body("Preserved guidance"), "\"0\""))
            .Status.ShouldBe(HttpStatusCode.Created);
        await using var connection = new NpgsqlConnection(fixture.OwnerConnectionString(world.Database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "insert into public.project_ai_settings select * from public.project_ai_settings where org_id = @org on conflict (id) do update set description = excluded.description", connection);
        command.Parameters.AddWithValue("org", world.Org);
        var exception = await Should.ThrowAsync<PostgresException>(async () =>
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        exception.SqlState.ShouldBe("23514");
        (await AiSettingsCalls.StoredAsync(fixture, world, 1)).Document["description"]?.GetValue<string>()
            .ShouldBe("Preserved guidance");
        command.CommandText = "delete from public.project_ai_settings where org_id = @org";
        (await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        (await AiSettingsCalls.SettingsCountAsync(fixture, world)).ShouldBe(0);
    }

    private static async Task SetScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid org)
    {
        await using var command = new NpgsqlCommand("select set_config('evidgate.org_id', @org, true)", connection, transaction);
        command.Parameters.AddWithValue("org", org.ToString("D"));
        await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private static JsonObject Body(string description)
    {
        var document = AiSettingsCalls.Document();
        document["description"] = description;
        return AiSettingsCalls.Body(document);
    }
}
