using System.Net;
using Evidgate.Modules.Healing.Application;
using Evidgate.Modules.Healing.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
[Trait("Category", "Docker")]
public sealed partial class HealingLlmGatewayTests
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
        var database = await _fixture.SharedDatabaseAsync();
        var plan = await PlannedExecutionSeeder.SeedPlanAsync(_fixture, database, world.Org, 1);
        var execution = plan.Executions[0];
        await using (var seed = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(database)))
        {
            var attempt = Guid.CreateVersion7();
            await seed.ExecuteAsync(
                """
                insert into public.case_attempt
                    (id, org_id, run_id, execution_key, case_id, attempt_no, raw_status, normalized_status)
                values (@id, @org_id, @run_id, @execution_key, @case_id, 1, 'failed', 'failed')
                """,
                ("id", attempt), ("org_id", world.Org), ("run_id", plan.RunId),
                ("execution_key", execution.ExecutionKey), ("case_id", execution.CaseId));
            await seed.ExecuteAsync(
                """
                insert into public.incident
                    (id, org_id, project_id, run_id, attempt_id, case_id, case_revision_id, execution_key, engine)
                values (@id, @org_id, @project_id, @run_id, @attempt_id, @case_id, @case_revision_id, @execution_key, 'playwright')
                """,
                ("id", incident), ("org_id", world.Org), ("project_id", world.Project), ("run_id", plan.RunId),
                ("attempt_id", attempt), ("execution_key", execution.ExecutionKey),
                ("case_id", execution.CaseId), ("case_revision_id", execution.CaseRevisionId));
            await seed.CommitAsync();
        }

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
