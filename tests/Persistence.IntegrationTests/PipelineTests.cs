using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using FluentValidation;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace Persistence.IntegrationTests;

/// <summary>
/// 01.4.2-T4.1–T4.3 against real PostgreSQL: the C2 behaviour chain on the paths
/// the plan names — a query opens <c>BEGIN READ ONLY</c> with the tenant set from
/// its first statement (C1/T95/AC6), an invalid command never opens a transaction,
/// a denied command rolls back without committing, a failed Result rolls back with
/// its write, and an <c>Idempotency-Key</c> replays or conflicts (00 §9.1). Every
/// test gets a fresh migrated database, and the probe table is counted as the owner
/// (which sees every tenant's rows), so "nothing committed" is proven, not assumed.
/// </summary>
[Collection(PersistenceSuite.Name)]
[Trait("Category", "Docker")]
public sealed class PipelineTests
{
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid ProjectA = Guid.Parse("33333333-3333-7333-8333-333333333333");

    private readonly PostgresPersistenceFixture _fixture;

    public PipelineTests(PostgresPersistenceFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// AC6/T95: a query's scope is read-only (the transaction's mode is the scope's
    /// own first statement) and the tenant setting is in force in the same
    /// statement — so no statement of the query path ever runs writable and none
    /// runs unscoped.
    /// </summary>
    [Fact]
    public async Task Query_Scope_Is_Read_Only_And_Tenant_Scoped_From_Its_First_Statement()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = PipelineTestHost.Start(_fixture, database, new RecordingPermissionEvaluator(granted: true));

        using var tenant = host.TenantContext.Publish(OrgA, ProjectA);

        var result = await host.Mediator.Send(new ProbeScopeQuery(OrgA), Token);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.ReadOnlyMode.ShouldBe("on");
        result.Value.TenantOrgId.ShouldBe(OrgA.ToString());

        var opened = host.Scopes.Opens.ShouldHaveSingleItem();
        opened.OrgId.ShouldBe(OrgA);
        opened.ProjectId.ShouldBe(ProjectA);
        opened.ReadOnly.ShouldBeTrue();
    }

    /// <summary>C1/T95: a write inside a query's read-only transaction is refused by the database itself.</summary>
    [Fact]
    public async Task Write_In_A_Query_Scope_Fails_At_The_Database()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = PipelineTestHost.Start(_fixture, database, new RecordingPermissionEvaluator(granted: true));

        using var tenant = host.TenantContext.Publish(OrgA, ProjectA);
        var id = Guid.CreateVersion7();

        var failure = await Should.ThrowAsync<PostgresException>(
            async () => await host.Mediator.Send(new ProbeReadOnlyWriteQuery(id), Token));

        failure.SqlState.ShouldBe(PostgresErrorCodes.ReadOnlySqlTransaction);
        (await _fixture.CountProbeRowsAsOwnerAsync(database, id)).ShouldBe(0);
    }

    /// <summary>
    /// T4.1 done-when: validation is stage 2, BEFORE the tenant transaction — the
    /// invalid command answers 422 <c>request_invalid</c> with the field reason and
    /// no scope was ever opened.
    /// </summary>
    [Fact]
    public async Task Invalid_Command_Is_Rejected_Before_The_Transaction_Opens()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = PipelineTestHost.Start(_fixture, database, new RecordingPermissionEvaluator(granted: true));

        using var tenant = host.TenantContext.Publish(OrgA, ProjectA);

        var result = await host.Mediator.Send(new ProbeInvalidCommand(Guid.Empty), Token);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.RequestInvalid);
        result.Error.Status.ShouldBe(422);
        result.Error.Details.ShouldNotBeNull().ShouldContain(
            detail => detail.Path == "Id" || detail.Path == "id");
        host.Scopes.Opens.ShouldBeEmpty();
        (await _fixture.CountProbeRowsAsOwnerAsync(database, Guid.Empty)).ShouldBe(0);
    }

    /// <summary>
    /// T4.2 done-when: a denied command answers 403, the authorization stage read the
    /// handler's declared permission inside the tenant scope, and the transaction
    /// rolled back — the row the handler would have written is not there.
    /// </summary>
    [Fact]
    public async Task Denied_Command_Rolls_Back_Without_Committing()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var permissions = new RecordingPermissionEvaluator(granted: false);
        await using var host = PipelineTestHost.Start(_fixture, database, permissions);

        using var tenant = host.TenantContext.Publish(OrgA, ProjectA);
        var id = Guid.CreateVersion7();

        var result = await host.Mediator.Send(new ProbeDeniedCommand(id), Token);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(ApiErrorCode.Forbidden);
        result.Error.Status.ShouldBe(403);
        permissions.Checks.ShouldContain(check => check.Permission == "probe:denied" && check.OrgId == OrgA);

        host.Scopes.Opens.ShouldHaveSingleItem().ReadOnly.ShouldBeFalse();
        (await _fixture.CountProbeRowsAsOwnerAsync(database, id)).ShouldBe(0);
    }

    /// <summary>C2/C3: a successful command commits its row inside the tenant scope, at the status it declared.</summary>
    [Fact]
    public async Task Command_Commits_Its_Row_In_The_Tenant_Scope()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = PipelineTestHost.Start(_fixture, database, new RecordingPermissionEvaluator(granted: true));

        using var tenant = host.TenantContext.Publish(OrgA, ProjectA);
        var id = Guid.CreateVersion7();

        var result = await host.Mediator.Send(new ProbeWriteCommand(id), Token);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        result.Value.ShouldBe(id);
        result.Status.ShouldBe(201);
        host.Scopes.Opens.ShouldHaveSingleItem().ReadOnly.ShouldBeFalse();
        (await _fixture.CountProbeRowsAsOwnerAsync(database, id)).ShouldBe(1);
    }

    /// <summary>C2: a failed Result rolls the command back — the write never becomes durable.</summary>
    [Fact]
    public async Task Failed_Result_Rolls_The_Command_Back()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = PipelineTestHost.Start(_fixture, database, new RecordingPermissionEvaluator(granted: true));

        using var tenant = host.TenantContext.Publish(OrgA, ProjectA);
        var id = Guid.CreateVersion7();

        var result = await host.Mediator.Send(new ProbeFailingCommand(id), Token);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("probe_conflict");
        (await _fixture.CountProbeRowsAsOwnerAsync(database, id)).ShouldBe(0);
    }

    /// <summary>
    /// Fail closed: without a resolved request tenant a pipeline-managed command
    /// throws instead of running unscoped or against a default organization.
    /// </summary>
    [Fact]
    public async Task Missing_Tenant_Context_Fails_Closed_Without_Opening_A_Transaction()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = PipelineTestHost.Start(_fixture, database, new RecordingPermissionEvaluator(granted: true));

        await Should.ThrowAsync<TenantContextMissingException>(
            async () => await host.Mediator.Send(new ProbeWriteCommand(Guid.CreateVersion7()), Token));

        host.Scopes.Opens.ShouldBeEmpty();
    }

    /// <summary>
    /// 00 §9.1: the same key with the same request replays the stored response
    /// (201 and the original id, with no second write); a different request under
    /// that key is 409 <c>idempotency_conflict</c>.
    /// </summary>
    [Fact]
    public async Task Idempotent_Command_Replays_And_Conflicts()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = PipelineTestHost.Start(_fixture, database, new RecordingPermissionEvaluator(granted: true));

        // Org-scoped: idempotency_record.project_id carries a composite FK to
        // identity.project, and this test creates no project row.
        using var tenant = host.TenantContext.Publish(OrgA, projectId: null);
        var id = Guid.CreateVersion7();
        const string key = "probe-key-1";

        var first = await host.Mediator.Send(new ProbeIdempotentCommand(id, key), Token);
        first.IsSuccess.ShouldBeTrue(first.Error?.Message);
        first.Value.ShouldBe(id);
        first.Status.ShouldBe(201);
        (await _fixture.CountProbeRowsAsOwnerAsync(database, id)).ShouldBe(1);

        var replay = await host.Mediator.Send(new ProbeIdempotentCommand(id, key), Token);
        replay.IsSuccess.ShouldBeTrue(replay.Error?.Message);
        replay.Value.ShouldBe(id);
        replay.Status.ShouldBe(201);
        (await _fixture.CountProbeRowsAsOwnerAsync(database, id)).ShouldBe(1);

        var otherId = Guid.CreateVersion7();
        var conflict = await host.Mediator.Send(new ProbeIdempotentCommand(otherId, key), Token);
        conflict.IsSuccess.ShouldBeFalse();
        conflict.Error!.Code.ShouldBe(ApiErrorCode.IdempotencyConflict);
        conflict.Error.Status.ShouldBe(409);
        (await _fixture.CountProbeRowsAsOwnerAsync(database, otherId)).ShouldBe(0);
    }

    /// <summary>
    /// A DTO-answering message that declares no permission, credential or membership
    /// attribute keeps the recorded pass-through: no request tenant, no scope and no
    /// evaluator call. A message that DOES declare one fails closed instead, which the
    /// pipeline guard tests pin.
    /// </summary>
    [Fact]
    public async Task Unattributed_Dto_Message_Keeps_The_Recorded_Pass_Through()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var permissions = new RecordingPermissionEvaluator(granted: false);
        await using var host = PipelineTestHost.Start(_fixture, database, permissions);

        var id = Guid.CreateVersion7();

        var result = await host.Mediator.Send(new ProbeDtoCommand(id), Token);

        result.Id.ShouldBe(id);
        host.Scopes.Opens.ShouldBeEmpty();
        permissions.Checks.ShouldBeEmpty();
    }

    /// <summary>
    /// A host that registers its own validator must have it run: the behaviour
    /// resolves <c>IEnumerable&lt;IValidator&lt;TMessage&gt;&gt;</c> from DI.
    /// </summary>
    [Fact]
    public async Task Validators_Are_Resolved_From_The_Container()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = PipelineTestHost.Start(
            _fixture,
            database,
            new RecordingPermissionEvaluator(granted: true),
            services => services.AddSingleton<IValidator<ProbeWriteCommand>, ProbeWriteCommandValidator>());

        using var tenant = host.TenantContext.Publish(OrgA, ProjectA);

        var result = await host.Mediator.Send(new ProbeWriteCommand(Guid.Empty), Token);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Status.ShouldBe(422);
        host.Scopes.Opens.ShouldBeEmpty();
    }

    /// <summary>Seeded validator: the probe write command refuses the empty id.</summary>
    internal sealed class ProbeWriteCommandValidator : AbstractValidator<ProbeWriteCommand>
    {
        public ProbeWriteCommandValidator() =>
            RuleFor(command => command.Id)
                .Must(id => id != Guid.Empty)
                .WithMessage("id is required");
    }
}
