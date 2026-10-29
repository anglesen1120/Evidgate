using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Healing;
using Evidgate.Modules.Healing.Application;
using Evidgate.Modules.Healing.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 08.5.1-T1 against a real PostgreSQL: the provider-adapter gateway records one <c>healing_llm_call</c> row per attempt
/// inside the caller's tenant transaction, fails closed when the declared schema refuses the answer (AC1), aborts on the
/// per-call time budget without retrying beyond the bounded policy (AC2), and attributes an authoring generation to its
/// project alone (AC7). The provider itself is a stub — no network, no key — except the timeout case, which drives the
/// module's own typed client through a <see cref="HttpMessageHandler"/> that never completes.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class HealingLlmGatewayTests
{
    /// <summary>The schema the prompt claims to answer with: an object holding an array of strings.</summary>
    private const string ChecklistSchema = """
        {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","required":["checklist"],
         "properties":{"checklist":{"type":"array","items":{"type":"string"}}},"additionalProperties":false}
        """;

    /// <summary>The prompt body the tests hand over; the gateway must never copy it into a row.</summary>
    private const string Payload = "Design the checkout checklist for the shop.";

    private const string Model = "healing-test-model";

    private const string PromptVersion = "v1";

    private readonly PostgresTenancyFixture _fixture;

    public HealingLlmGatewayTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------ AC2: the time budget

    /// <summary>
    /// AC2: a provider that never answers is aborted by the time budget and recorded <c>timeout</c>, on one attempt and no
    /// more than the bounded policy allows.
    /// </summary>
    [Fact]
    public async Task InvokeHealingLlm_Timeout_Aborts()
    {
        var handler = new HangingHandler();

        await using var world = await StartAsync(services =>
        {
            // Tiny budgets so the case costs milliseconds instead of a minute; the composition under test is the module's.
            services.Configure<HealingLlmOptions>(options =>
            {
                options.AttemptTimeout = TimeSpan.FromMilliseconds(100);
                options.MaxTransportRetries = 1;
            });

            // The HTTP adapter is not the default (O4 is open), so this case wires it as the provider on purpose.
            services.Replace(ServiceDescriptor.Singleton<IHealingLlmProvider>(provider =>
                provider.GetRequiredService<HttpHealingLlmProvider>()));

            services
                .AddHttpClient<HttpHealingLlmProvider>()
                .ConfigureHttpClient(client => client.BaseAddress = new Uri("http://healing.invalid/"))
                .ConfigurePrimaryHttpMessageHandler(() => handler);
        });

        var invocation = await InvokeAsync(world, AuthoringRequest(world.Project));

        invocation.Result.Status.ShouldBe(HealingLlmStatuses.Timeout);
        invocation.Result.Content.ShouldBeNull();

        var row = (await ReadCallsAsync(world)).ShouldHaveSingleItem();
        row.Status.ShouldBe(HealingLlmStatuses.Timeout);
        row.Attempt.ShouldBe(1);
        row.ProjectId.ShouldBe(world.Project);
        row.Purpose.ShouldBe(HealingLlmPurposes.AuthoringGeneration);
        row.IncidentId.ShouldBeNull();
        row.CandidateId.ShouldBeNull();
        invocation.CallId.ShouldBe(row.Id);

        // AC2: no retry beyond the bounded resilience policy — one attempt plus at most MaxTransportRetries.
        handler.Calls.ShouldBeGreaterThanOrEqualTo(1);
        handler.Calls.ShouldBeLessThanOrEqualTo(2);
    }

    // ------------------------------------------------------------------ AC1: the declared schema

    /// <summary>
    /// AC1: an answer the declared schema refuses is retried one fresh call, refused again, and the call fails closed —
    /// two rows, attempt 1 and 2, both <c>llm_schema_violation</c>, and no content reaches the caller.
    /// </summary>
    [Fact]
    public async Task InvokeHealingLlm_SchemaViolation_FailsClosedAfterOneRetry()
    {
        // 'checklist' is a string here; the declared schema requires an array of strings.
        var provider = new StubProvider(_ => new HealingLlmResult(
            HealingLlmStatuses.Ok,
            """{"checklist":"not-an-array"}""",
            12,
            0.5m));

        await using var world = await StartAsync(services => services.Replace(
            ServiceDescriptor.Singleton<IHealingLlmProvider>(provider)));

        var invocation = await InvokeAsync(world, AuthoringRequest(world.Project, ChecklistSchema));

        invocation.Result.Status.ShouldBe(HealingLlmStatuses.SchemaViolation);
        invocation.Result.Content.ShouldBeNull();
        provider.Calls.ShouldBe(2);

        var rows = await ReadCallsAsync(world);
        rows.Count.ShouldBe(2);
        rows[0].Attempt.ShouldBe(1);
        rows[1].Attempt.ShouldBe(2);
        rows.ShouldAllBe(row => row.Status == HealingLlmStatuses.SchemaViolation);
        rows.ShouldAllBe(row => row.ProjectId == world.Project && row.Purpose == HealingLlmPurposes.AuthoringGeneration);
        rows.ShouldAllBe(row => row.IncidentId == null && row.CandidateId == null);

        // The refused attempts were still paid for, so both rows carry what the provider reported.
        rows.ShouldAllBe(row => row.Tokens == 12 && row.Cost == 0.5m);
        invocation.CallId.ShouldBe(rows[^1].Id);
    }

    /// <summary>AC1/AC4: a schema-valid answer is accepted and recorded once, as <c>ok</c>.</summary>
    [Fact]
    public async Task InvokeHealingLlm_ValidSchema_RecordsCall()
    {
        var provider = new StubProvider(_ => new HealingLlmResult(
            HealingLlmStatuses.Ok,
            """{"checklist":["open the cart","pay"]}""",
            34,
            1.25m));

        await using var world = await StartAsync(services => services.Replace(
            ServiceDescriptor.Singleton<IHealingLlmProvider>(provider)));

        var invocation = await InvokeAsync(world, AuthoringRequest(world.Project, ChecklistSchema));

        invocation.Result.Status.ShouldBe(HealingLlmStatuses.Ok);
        invocation.Result.Content.ShouldBe("""{"checklist":["open the cart","pay"]}""");
        provider.Calls.ShouldBe(1);

        var row = (await ReadCallsAsync(world)).ShouldHaveSingleItem();
        row.Status.ShouldBe(HealingLlmStatuses.Ok);
        row.Attempt.ShouldBe(1);
        row.Tokens.ShouldBe(34);
        row.Cost.ShouldBe(1.25m);
        row.Model.ShouldBe(Model);
        row.PromptVersion.ShouldBe(PromptVersion);
        invocation.CallId.ShouldBe(row.Id);
    }

    /// <summary>
    /// A declaration that is not a JSON Schema document is refused before the provider is called and before any row
    /// exists: the failure is the caller's, and it must cost neither a token nor an unexplained row.
    /// </summary>
    [Fact]
    public async Task InvokeHealingLlm_MalformedSchema_RefusedBeforeProviderCall()
    {
        var provider = new StubProvider(_ => new HealingLlmResult(HealingLlmStatuses.Ok, """{"checklist":[]}""", 1, 0m));

        await using var world = await StartAsync(services => services.Replace(
            ServiceDescriptor.Singleton<IHealingLlmProvider>(provider)));

        var refusal = await Should.ThrowAsync<ArgumentException>(
            () => InvokeAsync(world, AuthoringRequest(world.Project, """{"type":"object",""")));

        refusal.Message.ShouldContain("response schema", Case.Sensitive);
        provider.Calls.ShouldBe(0);
        (await ReadCallsAsync(world)).ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ AC7: attribution

    /// <summary>
    /// AC7: an authoring generation is recorded with <c>purpose = 'authoring_generation'</c>, its project set and no
    /// incident or candidate, and the returned row id is what a generation stores as its <c>ai_run_id</c>. An
    /// incident-scoped purpose naming no incident is refused before the INSERT, with a message naming what is missing.
    /// </summary>
    [Fact]
    public async Task InvokeHealingLlm_AuthoringGeneration_RecordsProjectOnlyRow()
    {
        await using var world = await StartAsync();

        // The table's CHECK is mirrored in managed code: the caller learns what is missing, and nothing is written.
        var refusal = await Should.ThrowAsync<ArgumentException>(
            () => InvokeAsync(world, TriageRequest(world.Project, incidentId: null)));

        refusal.Message.ShouldContain("must name its incident", Case.Sensitive);
        (await ReadCallsAsync(world)).ShouldBeEmpty();

        // The registered default is the mock (O4 is open), so this drives the module's own composition.
        var invocation = await InvokeAsync(world, AuthoringRequest(world.Project));

        invocation.Result.Status.ShouldBe(HealingLlmStatuses.Ok);
        invocation.Result.Content.ShouldNotBeNull();

        var row = (await ReadCallsAsync(world)).ShouldHaveSingleItem();
        row.Id.ShouldBe(invocation.CallId);
        row.OrgId.ShouldBe(world.Org);
        row.Purpose.ShouldBe(HealingLlmPurposes.AuthoringGeneration);
        row.ProjectId.ShouldBe(world.Project);
        row.IncidentId.ShouldBeNull();
        row.CandidateId.ShouldBeNull();
        row.Model.ShouldBe(Model);
        row.PromptVersion.ShouldBe(PromptVersion);
        row.Status.ShouldBe(HealingLlmStatuses.Ok);
        row.Attempt.ShouldBe(1);
        row.Tokens.ShouldBe(Payload.Length / 4);
        row.Cost.ShouldBe(0m);
    }

    /// <summary>AC4: an incident-scoped purpose records its incident, and its candidate once one is named.</summary>
    [Fact]
    public async Task InvokeHealingLlm_IncidentPurpose_RecordsIncidentAndCandidate()
    {
        await using var world = await StartAsync();
        var incident = Guid.CreateVersion7();
        var candidate = Guid.CreateVersion7();

        var request = new HealingLlmRequest(
            world.Project,
            HealingLlmPurposes.CandidateGeneration,
            incident,
            candidate,
            Model,
            PromptVersion,
            Payload,
            ResponseSchema: null);

        var invocation = await InvokeAsync(world, request);

        invocation.Result.Status.ShouldBe(HealingLlmStatuses.Ok);

        var row = (await ReadCallsAsync(world)).ShouldHaveSingleItem();
        row.Purpose.ShouldBe(HealingLlmPurposes.CandidateGeneration);
        row.ProjectId.ShouldBe(world.Project);
        row.IncidentId.ShouldBe(incident);
        row.CandidateId.ShouldBe(candidate);
    }

    // ------------------------------------------------------------------ the world

    private sealed record World(
        Guid Org,
        Guid Project,
        NpgsqlDataSource DataSource,
        ServiceProvider Provider) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await DataSource.DisposeAsync();
        }
    }

    private async Task<World> StartAsync(Action<IServiceCollection>? configure = null)
    {
        var database = await _fixture.CreateDatabaseAsync();
        var org = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();

        await using (var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            await seed.ExecuteAsync(
                "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
                ("id", org),
                ("name", "healing-llm"),
                ("slug", "healing-llm"));
            await seed.ExecuteAsync(
                "insert into public.project (id, org_id, name, slug) values (@id, @org_id, @name, @slug)",
                ("id", project),
                ("org_id", org),
                ("name", "checkout"),
                ("slug", "checkout"));
            await seed.CommitAsync();
        }

        var dataSource = NpgsqlDataSource.Create(_fixture.AppConnectionString(database));
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddEvidgateTenancy();

        // The module's own composition: the default provider, the gateway and the typed HTTP client with its pipeline.
        services.AddHealingModule(new ConfigurationBuilder().Build());

        configure?.Invoke(services);

        return new World(org, project, dataSource, services.BuildServiceProvider());
    }

    /// <summary>
    /// Invokes the gateway inside a committed-to tenant scope on the app role, which is exactly the caller's situation:
    /// the recorder writes inside this transaction through the module's Dapper path.
    /// </summary>
    private static async Task<HealingLlmInvocation> InvokeAsync(World world, HealingLlmRequest request)
    {
        await using var services = world.Provider.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(
            world.Org,
            world.Project,
            readOnlyTransaction: false,
            TestContext.Current.CancellationToken);

        var gateway = services.ServiceProvider.GetRequiredService<HealingLlmGateway>();
        var invocation = await gateway.InvokeAsync(request, TestContext.Current.CancellationToken);

        // The caller commits: every recorded attempt rides the caller's tenant transaction (00 §5.5 C3).
        await scope.CommitAsync(TestContext.Current.CancellationToken);

        return invocation;
    }

    /// <summary>The rows this org recorded, in attempt order, read through the same tenant path.</summary>
    private static async Task<IReadOnlyList<HealingLlmCallRow>> ReadCallsAsync(World world)
    {
        await using var services = world.Provider.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(
            world.Org,
            world.Project,
            readOnlyTransaction: true,
            TestContext.Current.CancellationToken);

        var db = services.ServiceProvider.GetRequiredService<ITenantDb>();
        return await db.QueryAsync<HealingLlmCallRow>(
            """
            select id, org_id as "OrgId", project_id as "ProjectId", incident_id as "IncidentId",
                   candidate_id as "CandidateId", purpose, model, prompt_version as "PromptVersion",
                   tokens, cost, status, attempt
              from public.healing_llm_call
             order by attempt
            """,
            null,
            TestContext.Current.CancellationToken);
    }

    private static HealingLlmRequest AuthoringRequest(Guid project, string? responseSchema = null) =>
        new(project, HealingLlmPurposes.AuthoringGeneration, null, null, Model, PromptVersion, Payload, responseSchema);

    private static HealingLlmRequest TriageRequest(Guid project, Guid? incidentId) =>
        new(project, HealingLlmPurposes.TriageHypothesis, incidentId, null, Model, PromptVersion, Payload, null);

    /// <summary>A provider that answers every attempt the same way and counts the attempts.</summary>
    private sealed class StubProvider(Func<HealingLlmRequest, HealingLlmResult> answer) : IHealingLlmProvider
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        public Task<HealingLlmResult> InvokeAsync(HealingLlmRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref _calls);
            return Task.FromResult(answer(request));
        }
    }

    /// <summary>
    /// A handler that never answers: it waits until the pipeline's time budget cancels it, and counts how many attempts
    /// reached it — which is how AC2's "no retry beyond the bounded policy" is measured.
    /// </summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _calls);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage();
        }
    }
}

/// <summary>One <c>healing_llm_call</c> row, as the recorder wrote it.</summary>
/// <param name="Id">The row id (the value a generation stores as <c>ai_run_id</c>).</param>
/// <param name="OrgId">The tenant the RLS scope resolved to.</param>
/// <param name="ProjectId">The project the call was attributed to.</param>
/// <param name="IncidentId">The incident, or null for an authoring generation.</param>
/// <param name="CandidateId">The candidate, when the call names one.</param>
/// <param name="Purpose">One of <see cref="HealingLlmPurposes"/>.</param>
/// <param name="Model">The model the adapter was asked for.</param>
/// <param name="PromptVersion">The prompt version the call used.</param>
/// <param name="Tokens">Tokens this attempt spent.</param>
/// <param name="Cost">Cost this attempt incurred.</param>
/// <param name="Status">One of <see cref="HealingLlmStatuses"/>.</param>
/// <param name="Attempt">The 1-based attempt number.</param>
internal sealed record HealingLlmCallRow(
    Guid Id,
    Guid OrgId,
    Guid ProjectId,
    Guid? IncidentId,
    Guid? CandidateId,
    string Purpose,
    string Model,
    string PromptVersion,
    int Tokens,
    decimal Cost,
    string Status,
    int Attempt);
