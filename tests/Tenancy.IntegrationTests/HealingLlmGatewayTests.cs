using System.Collections.Concurrent;
using System.Net;
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
/// project alone (AC7). A provider report that breaks the ledger's own contract (a negative token count, a cost that does
/// not fit <c>numeric(12,6)</c>, a status outside the closed vocabulary) is recorded as <c>error</c> with nothing spent
/// instead of failing the INSERT, and the outcome reports the call's whole spend, not one attempt's. The provider itself
/// is a stub — no network, no key — except the cases that drive the module's own typed client through a
/// <see cref="HttpMessageHandler"/> (a hanging one, a server error, a redirect), where the handler also counts the
/// requests, which is how the bounded retry is measured without a socket.
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
    /// more than the transport retries the options allow — none by default, because the provider call is a POST.
    /// </summary>
    /// <param name="transportRetries">The <c>MaxTransportRetries</c> the case opts into.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task InvokeHealingLlm_Timeout_Aborts(int transportRetries)
    {
        // The default the pipeline reads: nothing is retried unless a host opts in, so the default budget is one call.
        new HealingLlmOptions().MaxTransportRetries.ShouldBe(0);

        var handler = new HangingHandler();

        await using var world = await StartAsync(services =>
        {
            // Tiny budgets so the case costs milliseconds instead of a minute; the composition under test is the module's.
            services.Configure<HealingLlmOptions>(options =>
            {
                options.AttemptTimeout = TimeSpan.FromMilliseconds(100);
                options.MaxTransportRetries = transportRetries;
            });

            UseHttpProvider(services, handler);
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

        // AC2: the retry strategy exists only when a host opts into it, so the handler is reached exactly once plus
        // MaxTransportRetries — the gateway, which sees one provider call, still records a single row.
        handler.Calls.ShouldBe(1 + transportRetries);
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

        // The refused attempts were still paid for, so both rows carry what the provider reported, and the outcome
        // reports the call's whole spend — not the last attempt's.
        rows.ShouldAllBe(row => row.Tokens == 12 && row.Cost == 0.5m);
        invocation.Result.Tokens.ShouldBe(24);
        invocation.Result.Cost.ShouldBe(1.0m);
        invocation.CallId.ShouldBe(rows[^1].Id);
    }

    /// <summary>
    /// AC1: a refused answer is retried with one fresh call, and an answer the schema accepts is the call's outcome: the
    /// ledger keeps both attempts in order (<c>llm_schema_violation</c>, then <c>ok</c>), the caller receives the second
    /// answer, the returned id is the accepted row's, and the outcome reports both attempts' spend.
    /// </summary>
    [Fact]
    public async Task InvokeHealingLlm_SchemaViolationThenValid_AcceptsTheRetry()
    {
        HealingLlmResult[] answers =
        [
            new(HealingLlmStatuses.Ok, """{"checklist":"not-an-array"}""", 12, 0.5m),
            new(HealingLlmStatuses.Ok, """{"checklist":["open the cart","pay"]}""", 12, 0.5m),
        ];
        var provider = new QueuedProvider(answers);

        await using var world = await StartAsync(services => services.Replace(
            ServiceDescriptor.Singleton<IHealingLlmProvider>(provider)));

        var invocation = await InvokeAsync(world, AuthoringRequest(world.Project, ChecklistSchema));

        invocation.Result.Status.ShouldBe(HealingLlmStatuses.Ok);
        invocation.Result.Content.ShouldBe("""{"checklist":["open the cart","pay"]}""");
        provider.Calls.ShouldBe(2);

        var rows = await ReadCallsAsync(world);
        rows.Count.ShouldBe(2);
        rows[0].Status.ShouldBe(HealingLlmStatuses.SchemaViolation);
        rows[0].Attempt.ShouldBe(1);
        rows[1].Status.ShouldBe(HealingLlmStatuses.Ok);
        rows[1].Attempt.ShouldBe(2);
        invocation.CallId.ShouldBe(rows[1].Id);

        // Both attempts were paid for, so the ledger keeps 12 + 12 tokens and 0.5 + 0.5 cost, and so does the outcome.
        rows.ShouldAllBe(row => row.Tokens == 12 && row.Cost == 0.5m);
        invocation.Result.Tokens.ShouldBe(24);
        invocation.Result.Cost.ShouldBe(1.0m);
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

    // ------------------------------------------------------------------ what a provider may report

    /// <summary>
    /// The gateway retries a schema violation and nothing else: a provider that reports <c>error</c> is asked once, and
    /// its failure is recorded as the attempt stands — no second call, and no spend the provider did not report.
    /// </summary>
    [Fact]
    public async Task InvokeHealingLlm_ProviderError_IsNotRetried()
    {
        var provider = new StubProvider(_ => new HealingLlmResult(HealingLlmStatuses.Error, null, 0, 0m));

        await using var world = await StartAsync(services => services.Replace(
            ServiceDescriptor.Singleton<IHealingLlmProvider>(provider)));

        var invocation = await InvokeAsync(world, AuthoringRequest(world.Project, ChecklistSchema));

        invocation.Result.Status.ShouldBe(HealingLlmStatuses.Error);
        invocation.Result.Content.ShouldBeNull();
        provider.Calls.ShouldBe(1);

        var row = (await ReadCallsAsync(world)).ShouldHaveSingleItem();
        row.Status.ShouldBe(HealingLlmStatuses.Error);
        row.Attempt.ShouldBe(1);
        row.Tokens.ShouldBe(0);
        row.Cost.ShouldBe(0m);
        invocation.CallId.ShouldBe(row.Id);
    }

    /// <summary>
    /// The ledger's own contract outranks a provider's report: tokens are a non-negative count, a cost must fit
    /// <c>numeric(12,6)</c> and <c>&gt;= 0</c>, and the status is the closed vocabulary. A report that breaks any of them
    /// is recorded as <c>error</c> with nothing spent — never as an exception out of the gateway, and never as a row the
    /// INSERT refuses.
    /// </summary>
    /// <param name="tokens">The token count the provider reports.</param>
    /// <param name="cost">The cost the provider reports (an attribute argument cannot be a <see cref="decimal"/>).</param>
    /// <param name="status">The status the provider reports.</param>
    [Theory]
    [InlineData(-1, 0.5, HealingLlmStatuses.Ok)]
    [InlineData(10, 1_000_000.0, HealingLlmStatuses.Ok)]
    [InlineData(10, 0.5, "provider_said_something_else")]
    public async Task InvokeHealingLlm_ReportOutsideTheLedgerContract_RecordsErrorWithoutSpend(
        int tokens,
        double cost,
        string status)
    {
        // The content itself is schema-valid, so only the report can decide the outcome.
        var provider = new StubProvider(_ => new HealingLlmResult(
            status,
            """{"checklist":["open the cart"]}""",
            tokens,
            (decimal)cost));

        await using var world = await StartAsync(services => services.Replace(
            ServiceDescriptor.Singleton<IHealingLlmProvider>(provider)));

        var invocation = await InvokeAsync(world, AuthoringRequest(world.Project, ChecklistSchema));

        invocation.Result.Status.ShouldBe(HealingLlmStatuses.Error);
        invocation.Result.Content.ShouldBeNull();
        invocation.Result.Tokens.ShouldBe(0);
        invocation.Result.Cost.ShouldBe(0m);

        // An unusable report is not a schema violation, so it is not retried either.
        provider.Calls.ShouldBe(1);

        var row = (await ReadCallsAsync(world)).ShouldHaveSingleItem();
        row.Status.ShouldBe(HealingLlmStatuses.Error);
        row.Tokens.ShouldBe(0);
        row.Cost.ShouldBe(0m);
        row.Attempt.ShouldBe(1);
        invocation.CallId.ShouldBe(row.Id);
    }

    // ------------------------------------------------------------------ the HTTP adapter, without a socket

    /// <summary>
    /// The HTTP adapter maps a non-success status to <c>error</c> instead of throwing or re-asking by itself, so the
    /// gateway records the attempt — once, since a POST is not retried unless the host opts in.
    /// </summary>
    [Fact]
    public async Task InvokeHealingLlm_HttpProvider_ServerError_RecordsErrorRow()
    {
        var handler = new RespondingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await using var world = await StartAsync(services => UseHttpProvider(services, handler));

        var invocation = await InvokeAsync(world, AuthoringRequest(world.Project, ChecklistSchema));

        invocation.Result.Status.ShouldBe(HealingLlmStatuses.Error);
        invocation.Result.Content.ShouldBeNull();
        handler.Calls.ShouldBe(1);

        var row = (await ReadCallsAsync(world)).ShouldHaveSingleItem();
        row.Status.ShouldBe(HealingLlmStatuses.Error);
        row.Attempt.ShouldBe(1);
        row.Tokens.ShouldBe(0);
        row.Cost.ShouldBe(0m);
        invocation.CallId.ShouldBe(row.Id);
    }

    /// <summary>
    /// A redirect is neither content nor a reason to leave the configured endpoint: the adapter reports the 307 as
    /// <c>error</c>, the handler is reached once, and the redirect target is never requested.
    /// </summary>
    [Fact]
    public async Task InvokeHealingLlm_HttpProvider_Redirect_IsNotFollowed()
    {
        var target = new Uri("http://healing.invalid/elsewhere");
        var handler = new RespondingHandler(_ => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
        {
            Headers = { Location = target },
        });

        await using var world = await StartAsync(services => UseHttpProvider(services, handler));

        var invocation = await InvokeAsync(world, AuthoringRequest(world.Project, ChecklistSchema));

        invocation.Result.Status.ShouldBe(HealingLlmStatuses.Error);
        invocation.Result.Content.ShouldBeNull();

        handler.Calls.ShouldBe(1);
        var requested = handler.Requests.ShouldHaveSingleItem();
        requested.AbsolutePath.ShouldBe("/llm/generate");
        requested.ShouldNotBe(target);

        var row = (await ReadCallsAsync(world)).ShouldHaveSingleItem();
        row.Status.ShouldBe(HealingLlmStatuses.Error);
        row.Attempt.ShouldBe(1);
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
    /// Makes the module's own HTTP adapter the provider over <paramref name="handler"/>: no socket is opened, and every
    /// request the adapter sends is observable. The HTTP adapter is not the default (O4 is open), so these cases wire it
    /// on purpose.
    /// </summary>
    /// <param name="services">The collection the test composes, before its provider is built.</param>
    /// <param name="handler">The handler that stands in for the provider's endpoint.</param>
    private static void UseHttpProvider(IServiceCollection services, HttpMessageHandler handler)
    {
        services.Replace(ServiceDescriptor.Singleton<IHealingLlmProvider>(provider =>
            provider.GetRequiredService<HttpHealingLlmProvider>()));

        services
            .AddHttpClient<HttpHealingLlmProvider>()
            .ConfigureHttpClient(client => client.BaseAddress = new Uri("http://healing.invalid/"))
            .ConfigurePrimaryHttpMessageHandler(() => handler);
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

    /// <summary>
    /// A provider that answers one queued result per attempt, then repeats the last one — which is how a call whose first
    /// attempt is refused and whose second is accepted is staged without any timing.
    /// </summary>
    private sealed class QueuedProvider(HealingLlmResult[] answers) : IHealingLlmProvider
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        public Task<HealingLlmResult> InvokeAsync(HealingLlmRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Interlocked.Increment(ref _calls) - 1;
            return Task.FromResult(answers[Math.Min(index, answers.Length - 1)]);
        }
    }

    /// <summary>
    /// A handler that answers with the response it is handed and records every request it was asked for — which is how
    /// "the redirect was not followed" and "a non-success status is not retried" are measured without a socket.
    /// </summary>
    private sealed class RespondingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Uri> _requests = new();

        private int _calls;

        /// <summary>How many requests reached the handler.</summary>
        internal int Calls => Volatile.Read(ref _calls);

        /// <summary>The URIs those requests were sent to, in arrival order.</summary>
        internal IReadOnlyCollection<Uri> Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref _calls);
            if (request.RequestUri is { } uri)
            {
                _requests.Enqueue(uri);
            }

            return Task.FromResult(respond(request));
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
