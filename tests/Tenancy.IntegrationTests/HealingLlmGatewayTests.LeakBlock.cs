using System.Diagnostics;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Healing.Application;
using Evidgate.Modules.Healing.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

// A call the redaction gate refuses: no provider call, one audit entry that names the rule, the segment and the
// report digest, and a ledger row whose report digest is the stored report's. Most cases use a hand-written pipeline
// double because IRedactionPipeline is internal and cannot be proxied; the real pipeline is driven in the sibling partial.
public sealed partial class HealingLlmGatewayTests
{
    [Theory]
    [InlineData("segment:2", "2", "residual_credentials")]
    [InlineData("preamble", "0", "residual_bearer")]
    [InlineData("payload", "0", "redaction_budget_exceeded")]
    public async Task InvokeHealingLlm_ResidualLeak_RefusesProviderAndAuditsExactlyOnce(
        string fieldPath, string ordinal, string rule)
    {
        // Built at run time: the payload is a stand-in for a secret and is never a literal in the repository.
        var secret = string.Concat("synthetic-", "private-value");
        var report = new RedactionReport(
            RedactionRules.Version,
            [new("email", "segment:1", 1), new(rule, fieldPath, 1), new("residual_cookie", "segment:3", 1)]);
        var provider = new StubProvider(_ => new HealingLlmResult(HealingLlmStatuses.Ok, "{}", 12, 0.01m));
        var logger = new CapturingLogger();
        await using var world = await StartLeakWorldAsync(provider, new InjectedLeakPipeline(report, blocked: true), logger);
        using var activity = new Activity("blocked-healing-call").SetIdFormat(ActivityIdFormat.W3C).Start();
        activity.AddBaggage(AuditTrail.RequestIdBaggage, "healing-leak-test");

        var invocation = await InvokeAsync(world, AuthoringRequest(world.Project) with { Payload = secret });

        invocation.Result.Status.ShouldBe(HealingLlmStatuses.BlockedLeak);
        invocation.Result.Tokens.ShouldBe(0);
        invocation.Result.Cost.ShouldBe(0m);
        provider.Calls.ShouldBe(0, "a blocked outcome must never reach the provider");
        var row = (await ReadCallsAsync(world)).ShouldHaveSingleItem();
        row.Id.ShouldBe(invocation.CallId);
        row.Status.ShouldBe(HealingLlmStatuses.BlockedLeak);
        row.Tokens.ShouldBe(0);
        row.Cost.ShouldBe(0m);

        var audit = (await ReadLeakAuditsAsync(world)).ShouldHaveSingleItem();
        audit.Action.ShouldBe("healing_llm.blocked_leak");
        audit.ActorType.ShouldBe(AuditActorTypes.System);
        audit.ActorId.ShouldBe("system");
        audit.ObjectType.ShouldBe("healing_llm_call");
        audit.ObjectId.ShouldBe(invocation.CallId.ToString("D"));
        audit.ProjectId.ShouldBe(world.Project);
        audit.RequestId.ShouldBe("healing-leak-test");
        audit.TraceId.ShouldBe(activity.TraceId.ToString());
        var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(audit.Metadata).ShouldNotBeNull();
        metadata.Keys.ShouldBe(["rule_id", "segment_ordinal", "report_hash"], ignoreOrder: true);
        metadata["rule_id"].ShouldBe(rule);
        metadata["segment_ordinal"].ShouldBe(ordinal);
        metadata["report_hash"].ShouldBe(report.Hash);
        audit.Metadata.ShouldNotContain(secret);
        logger.Messages.ShouldNotBeEmpty("the counts-only warning is the positive control for the log assertion");
        logger.Messages.ShouldAllBe(message => !message.Contains(secret, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeHealingLlm_StoredReportHash_MatchesCallAndRecomputedReport(bool blocked)
    {
        var report = new RedactionReport(
            RedactionRules.Version,
            blocked ? [new("residual_credentials", "segment:1", 1)] : [new("email", "segment:1", 2)]);
        var provider = new StubProvider(_ => new HealingLlmResult(HealingLlmStatuses.Ok, "{}", 12, 0.01m));
        await using var world = await StartLeakWorldAsync(provider, new InjectedLeakPipeline(report, blocked));

        var invocation = await InvokeAsync(world, AuthoringRequest(world.Project));

        await using var services = world.Provider.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(
            world.Org, world.Project, readOnlyTransaction: true, TestContext.Current.CancellationToken);
        var stored = (await services.ServiceProvider.GetRequiredService<ITenantDb>().QueryAsync<LeakReportRow>(
            """
            select c.redaction_report_hash as "CallHash", r.report_hash as "ReportHash", r.report::text as "Report"
              from public.healing_llm_call c
              join public.healing_redaction_report r
                on r.org_id = c.org_id and r.report_hash = c.redaction_report_hash
             where c.id = @call_id
            """,
            new { call_id = invocation.CallId },
            TestContext.Current.CancellationToken)).ShouldHaveSingleItem();

        stored.CallHash.ShouldBe(stored.ReportHash);
        stored.ReportHash.ShouldBe(report.Hash);
        RedactionReport.HashOf(stored.Report).ShouldBe(stored.CallHash, "the stored report still has the digest recorded for it");
    }

    [Fact]
    public async Task InvokeHealingLlm_CleanPayload_SendsAndWritesNoBlockedLeakAudit()
    {
        var provider = new StubProvider(_ => new HealingLlmResult(HealingLlmStatuses.Ok, "{}", 12, 0.01m));
        var report = new RedactionReport(RedactionRules.Version, [new("email", "segment:1", 1)]);
        await using var world = await StartLeakWorldAsync(provider, new InjectedLeakPipeline(report, blocked: false));

        var invocation = await InvokeAsync(world, AuthoringRequest(world.Project));

        invocation.Result.Status.ShouldBe(HealingLlmStatuses.Ok);
        provider.Calls.ShouldBe(1);
        (await ReadLeakAuditsAsync(world)).ShouldBeEmpty();
    }

    [Fact]
    public async Task InvokeHealingLlm_AuditEntryRefused_RollsBackBlockedCallAndReport()
    {
        var provider = new StubProvider(_ => new HealingLlmResult(HealingLlmStatuses.Ok, "{}", 12, 0.01m));
        var report = new RedactionReport(RedactionRules.Version, [new("residual_credentials", "segment:1", 1)]);
        var audit = Substitute.For<IAuditTrail>();
        audit.When(trail => trail.Record(Arg.Any<string>(), Arg.Any<AuditActor>()))
            .Do(_ => throw new InvalidOperationException("audit refused"));
        await using var world = await StartAsync(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IHealingLlmProvider>(provider));
            services.Replace(ServiceDescriptor.Singleton<IRedactionPipeline>(new InjectedLeakPipeline(report, blocked: true)));
            services.Replace(ServiceDescriptor.Singleton(audit));
        });

        await Should.ThrowAsync<InvalidOperationException>(() => InvokeAsync(world, AuthoringRequest(world.Project)));

        provider.Calls.ShouldBe(0);
        (await ReadCallsAsync(world)).ShouldBeEmpty("the call row and the entry commit together");
        (await CountAsync(world, "healing_redaction_report")).ShouldBe(0, "the report rolls back with the row");
    }

    // The hold is committed before the mask runs, so an audit write that fails after a refusal rolls the settlement back
    // and would leave the hold charged although nothing was sent: it must be given back and the failure must surface.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeHealingLlm_ProvisionedBudgetAuditWriteFails_ReleasesHoldAndRollsBack(bool failsInFlush)
    {
        var provider = new StubProvider(_ => new HealingLlmResult(HealingLlmStatuses.Ok, "{}", 12, 0.01m));
        var report = new RedactionReport(RedactionRules.Version, [new("residual_credentials", "segment:1", 1)]);
        var failure = new IOException("audit write failed");
        var audit = Substitute.For<IAuditTrail>();
        if (failsInFlush)
        {
            audit.FlushAsync(Arg.Any<Guid>(), Arg.Any<ITenantDb>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<int>(failure));
        }
        else
        {
            audit.When(trail => trail.Record(Arg.Any<string>(), Arg.Any<AuditActor>())).Do(_ => throw failure);
        }

        await using var world = await StartAsync(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IHealingLlmProvider>(provider));
            services.Replace(ServiceDescriptor.Singleton<IRedactionPipeline>(new InjectedLeakPipeline(report, blocked: true)));
            services.Replace(ServiceDescriptor.Singleton(audit));
        });
        var database = await _fixture.SharedDatabaseAsync();
        await SeedBudgetAsync(database, world);

        var thrown = await Should.ThrowAsync<IOException>(() => InvokeAsync(world, AuthoringRequest(world.Project)));

        thrown.ShouldBeSameAs(failure);
        provider.Calls.ShouldBe(0);
        (await UsedAsync(database, world)).ShouldBe((0m, 0), "nothing was sent, so the committed hold goes back");
        (await ReadCallsAsync(world)).ShouldBeEmpty("the call row rolled back with the entry");
        (await CountAsync(world, "healing_redaction_report")).ShouldBe(0);
        (await ReadLeakAuditsAsync(world)).ShouldBeEmpty();
    }

    private Task<World> StartLeakWorldAsync(StubProvider provider, IRedactionPipeline pipeline, CapturingLogger? logger = null) =>
        StartAsync(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IHealingLlmProvider>(provider));
            services.Replace(ServiceDescriptor.Singleton(pipeline));
            if (logger is not null)
            {
                services.AddSingleton<ILogger<HealingLlmGateway>>(logger);
            }
        });

    private static async Task<IReadOnlyList<LeakAuditRow>> ReadLeakAuditsAsync(World world)
    {
        await using var services = world.Provider.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(
            world.Org, world.Project, readOnlyTransaction: true, TestContext.Current.CancellationToken);
        return await services.ServiceProvider.GetRequiredService<ITenantDb>().QueryAsync<LeakAuditRow>(
            """
            select action as "Action", actor_type as "ActorType", actor_id as "ActorId", object_type as "ObjectType",
                   object_id as "ObjectId", project_id as "ProjectId", request_id as "RequestId", trace_id as "TraceId",
                   metadata::text as "Metadata"
              from public.audit_log
             where action = 'healing_llm.blocked_leak'
            """,
            null,
            TestContext.Current.CancellationToken);
    }

    private static async Task<long> CountAsync(World world, string table)
    {
        await using var services = world.Provider.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
        await using var scope = await scopes.BeginAsync(
            world.Org, world.Project, readOnlyTransaction: true, TestContext.Current.CancellationToken);
        return await services.ServiceProvider.GetRequiredService<ITenantDb>().ScalarAsync<long>(
            $"select count(*) from public.{table}", null, TestContext.Current.CancellationToken);
    }

    private sealed record LeakAuditRow(
        string Action,
        string ActorType,
        string ActorId,
        string ObjectType,
        string ObjectId,
        Guid ProjectId,
        string? RequestId,
        string? TraceId,
        string Metadata);

    private sealed record LeakReportRow(string CallHash, string ReportHash, string Report);

    private sealed class InjectedLeakPipeline(RedactionReport report, bool blocked) : IRedactionPipeline
    {
        public RedactionOutcome Redact(string payload) => new(blocked ? string.Empty : payload, report, blocked);
    }

    private sealed class CapturingLogger : ILogger<HealingLlmGateway>
    {
        public List<string> Messages { get; } = [];

        public List<(LogLevel Level, EventId Id, string Message)> Events { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Messages.Add(formatter(state, exception));
            Events.Add((logLevel, eventId, formatter(state, exception)));
        }
    }
}
