using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using Fx = Tenancy.IntegrationTests.AuthoringExtractionFixtures;
using static Tenancy.IntegrationTests.RegistryApi;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The document intake end to end over public seams only: the HTTP create and read routes, the outbox row the create
/// leaves, the handler the task registry resolves for that row, and plain reads of the tables. The object store is a
/// real SeaweedFS container, so the extraction reads the bytes it would read in production.
/// </summary>
[Trait("Category", "Docker")]
public sealed class AuthoringIntakeChainTests(PostgresTenancyFixture fixture, SeaweedFsStore store)
    : IClassFixture<SeaweedFsStore>
{
    private const string ExtractTask = "authoring_intake_extract";

    private const string DocxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DocxIntake_EnqueuesOneTask_ThenTheRegistryHandlerMakesItReadyWithoutExposingText()
    {
        await using var world = await StartAsync();
        var artifact = await StoredArtifactAsync(world, Fx.Docx("A requirement for the checkout screen"));

        var created = await world.PostAsync(new { kind = "docx", feature_node_id = world.Node, artifact_id = artifact });

        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        created.Text("state").ShouldBe("received");
        var id = created.Id("intake_id");
        (await OutboxCountAsync(world, id)).ShouldBe(1);
        (await AuditCountAsync(world, id)).ShouldBe(1);

        await RunHandlerAsync(world, id);

        var view = await ReadAsync(world, id);
        view.Status.ShouldBe(HttpStatusCode.OK);
        view.Text("state").ShouldBe("ready");
        FailureCode(view).ShouldBeNull();
        view.Body.GetProperty("extracted_char_count").GetInt32().ShouldBe(
            await fixture.ScalarAsync<int>(
                world.Database, $"select char_length(extracted_text) from public.authoring_intake where id = '{id}'"));
        view.Body.TryGetProperty("extracted_text", out _).ShouldBeFalse();
        view.Body.GetRawText().ShouldNotContain("checkout screen");
    }

    [Fact]
    public async Task RejectedDocxIntake_ShownOverHttpWithItsFailureCode_AndNoText()
    {
        await using var world = await StartAsync();
        var noDocumentPart = Fx.Zip(("hello.txt", "no word document here"u8.ToArray()));
        var artifact = await StoredArtifactAsync(world, noDocumentPart);
        var created = await world.PostAsync(new { kind = "docx", feature_node_id = world.Node, artifact_id = artifact });
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var id = created.Id("intake_id");

        await RunHandlerAsync(world, id);

        var view = await ReadAsync(world, id);
        view.Status.ShouldBe(HttpStatusCode.OK);
        view.Text("state").ShouldBe("rejected");
        FailureCode(view).ShouldBe("extraction_failed");
        view.Body.TryGetProperty("extracted_text", out _).ShouldBeFalse();
        (!view.Body.TryGetProperty("eta_class", out var eta) || eta.ValueKind == JsonValueKind.Null)
            .ShouldBeTrue("a finished intake has no queue position");
    }

    [Fact]
    public async Task ReplayedCreateAndRedeliveredTask_LeaveOneIntakeOneTaskOneAuditRow()
    {
        await using var world = await StartAsync();
        var artifact = await StoredArtifactAsync(world, Fx.Docx("Review navigation"));
        var body = new { kind = "docx", feature_node_id = world.Node, artifact_id = artifact };
        var key = NewKey();
        var created = await world.PostAsync(body, key);
        created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
        var id = created.Id("intake_id");

        var replay = await world.PostAsync(body, key);

        replay.Status.ShouldBe(HttpStatusCode.Created);
        JsonElement.DeepEquals(created.Body, replay.Body).ShouldBeTrue();
        await RunHandlerAsync(world, id);
        var finished = await ReadAsync(world, id);
        await RunHandlerAsync(world, id);

        JsonElement.DeepEquals(finished.Body, (await ReadAsync(world, id)).Body).ShouldBeTrue(
            "a redelivered task finds the intake decided and writes nothing");
        finished.Text("state").ShouldBe("ready");
        (await IntakeCountAsync(world)).ShouldBe(1);
        (await OutboxCountAsync(world, id)).ShouldBe(1);
        (await AuditCountAsync(world, id)).ShouldBe(1);
    }

    [Fact]
    public async Task ConcurrentCreatesForOneArtifact_YieldOneIntakeOneTaskOneAuditRowAndOneConflict()
    {
        await using var world = await StartAsync();

        for (var round = 0; round < 6; round++)
        {
            var artifact = await world.ArtifactAsync("requirement_document", DocxType, 123);
            using var gate = new ManualResetEventSlim(false);
            var body = new { kind = "docx", feature_node_id = world.Node, artifact_id = artifact };

            async Task<ApiAnswer> PostWhenReleasedAsync()
            {
                await Task.Yield();
                gate.Wait();
                return await world.PostAsync(body);
            }

            var first = PostWhenReleasedAsync();
            var second = PostWhenReleasedAsync();
            gate.Set();
            var answers = await Task.WhenAll(first, second);

            answers.Count(answer => answer.Status == HttpStatusCode.Created).ShouldBe(1);
            var loser = answers.Single(answer => answer.Status != HttpStatusCode.Created);
            loser.Status.ShouldBe(HttpStatusCode.Conflict, loser.ErrorMessage);
            loser.ErrorCode.ShouldBe("idempotency_conflict");

            var id = answers.Single(answer => answer.Status == HttpStatusCode.Created).Id("intake_id");
            (await fixture.ScalarAsync<int>(
                world.Database,
                $"select count(*)::int from public.authoring_intake where org_id = '{world.Org}' and artifact_id = '{artifact}'"))
                .ShouldBe(1);
            (await OutboxCountAsync(world, id)).ShouldBe(1);
            (await AuditCountAsync(world, id)).ShouldBe(1);
        }

        (await fixture.ScalarAsync<int>(
            world.Database,
            $"select count(*)::int from public.audit_log where org_id = '{world.Org}' and action = 'authoring.intake.create'"))
            .ShouldBe(6, "a refused duplicate leaves no audit row of its own");
    }

    [Fact]
    public async Task EtaClass_IsShortWithFiveUnfinishedIntakesAhead_AndLongWithSix()
    {
        await using var world = await StartAsync();
        var ids = new List<Guid>();

        for (var index = 0; index < 7; index++)
        {
            var artifact = await world.ArtifactAsync("requirement_document", DocxType, 123);
            var created = await world.PostAsync(new { kind = "docx", feature_node_id = world.Node, artifact_id = artifact });
            created.Status.ShouldBe(HttpStatusCode.Created, created.ErrorMessage);
            ids.Add(created.Id("intake_id"));
        }

        (await ReadAsync(world, ids[0])).Text("eta_class").ShouldBe("short");
        (await ReadAsync(world, ids[5])).Text("eta_class").ShouldBe("short", "five intakes ahead is below the bound");
        (await ReadAsync(world, ids[6])).Text("eta_class").ShouldBe("long", "six intakes ahead reaches the bound");
    }

    private async Task<IntakeApiWorld> StartAsync()
    {
        var database = await fixture.SharedDatabaseAsync();
        var api = TenancyApiFactory.RealResolver(fixture.AppConnectionString(database), store.Configuration());
        var client = api.CreateClient();
        var user = Guid.CreateVersion7();
        var org = await CreateOrgAsync(client, user, Unique.Slug("chain"));
        var project = await CreateProjectAsync(client, user, org, Unique.Slug("docs"));
        var node = await client.SendAsync(
            HttpMethod.Post,
            $"/v1/projects/{project}/authoring/nodes",
            TenancyApiFactory.Resolved(user: user, orgHeader: org),
            new { kind = "module", title = "Specifications" },
            NewKey());
        node.Status.ShouldBe(HttpStatusCode.Created, node.ErrorMessage);

        return new IntakeApiWorld(api, client, fixture, database, user, org, project, node.Id("node_id"));
    }

    /// <summary>A ready requirement-document artifact whose bytes the store really holds at its object key.</summary>
    private async Task<Guid> StoredArtifactAsync(IntakeApiWorld world, byte[] bytes)
    {
        var artifact = await world.ArtifactAsync("requirement_document", DocxType, bytes.LongLength);
        var key = await fixture.ScalarAsync<string>(
            world.Database, $"select object_key from public.artifact where id = '{artifact}'");
        await store.PutAsync(key, bytes);
        return artifact;
    }

    /// <summary>Runs the handler the registry resolves for the task the create enqueued, with that row's own payload.</summary>
    private async Task RunHandlerAsync(IntakeApiWorld world, Guid intake)
    {
        var payload = await fixture.ScalarAsync<string>(
            world.Database,
            $"select payload::text from public.outbox where org_id = '{world.Org}' and task_type = '{ExtractTask}' and aggregate_id = '{intake}'");
        var registry = world.Api.Services.GetRequiredService<ITaskHandlerRegistry>();

        registry.TryGet(ExtractTask, out var invoker).ShouldBeTrue();
        invoker!.OpensOwnTransactions.ShouldBeTrue();
        await invoker.InvokeWithoutScopeAsync(payload, world.Org, Token);
    }

    private static Task<ApiAnswer> ReadAsync(IntakeApiWorld world, Guid intake) =>
        world.Client.SendAsync(HttpMethod.Get, $"{world.Path}/{intake}", world.As);

    private static string? FailureCode(ApiAnswer answer) =>
        answer.Body.TryGetProperty("failure_code", out var code) && code.ValueKind == JsonValueKind.String
            ? code.GetString()
            : null;

    private Task<int> OutboxCountAsync(IntakeApiWorld world, Guid intake) =>
        fixture.ScalarAsync<int>(
            world.Database,
            $"select count(*)::int from public.outbox where org_id = '{world.Org}' and task_type = '{ExtractTask}' and aggregate_id = '{intake}'");

    private Task<int> AuditCountAsync(IntakeApiWorld world, Guid intake) =>
        fixture.ScalarAsync<int>(
            world.Database,
            $"select count(*)::int from public.audit_log where org_id = '{world.Org}' and object_id = '{intake}' and action = 'authoring.intake.create'");

    private Task<int> IntakeCountAsync(IntakeApiWorld world) =>
        fixture.ScalarAsync<int>(
            world.Database, $"select count(*)::int from public.authoring_intake where org_id = '{world.Org}'");
}
