using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using FluentValidation;
using Mediator;

namespace Persistence.IntegrationTests;

// 01.4.2-T4 test-only messages and handlers. A test command/query pair with its
// handler is all the C2 pipeline needs — no module is invented for the test, and
// these types live in this assembly (not Evidgate.*), so the architecture rules
// never scan them.

/// <summary>Reads the scope's own state — the T95 proof (00 §5.5 C1).</summary>
internal sealed record ProbeScopeQuery(Guid OrgId) : IQuery<Result<ProbeScopeState>>;

/// <summary>What the query path's first statement observed: the transaction mode and the tenant setting together.</summary>
internal sealed record ProbeScopeState(string ReadOnlyMode, string? TenantOrgId);

/// <summary>The single row of the probe select, projected by Dapper (snake_case → PascalCase).</summary>
internal sealed record ProbeScopeRow(string ReadOnlyMode, string? TenantOrgId);

[Permission("probe:read")]
internal sealed class ProbeScopeQueryHandler(ITenantDb db) : IQueryHandler<ProbeScopeQuery, Result<ProbeScopeState>>
{
    public async ValueTask<Result<ProbeScopeState>> Handle(ProbeScopeQuery query, CancellationToken cancellationToken)
    {
        var row = await db.QuerySingleOrDefaultAsync<ProbeScopeRow>(
            """
            select current_setting('transaction_read_only') as read_only_mode,
                   current_setting('evidgate.org_id', true) as tenant_org_id
            """,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Result.Success(new ProbeScopeState(row!.ReadOnlyMode, row.TenantOrgId));
    }
}

/// <summary>A query whose handler tries to write: in a read-only transaction PostgreSQL refuses it (T95).</summary>
internal sealed record ProbeReadOnlyWriteQuery(Guid Id) : IQuery<Result<Guid>>;

[Permission("probe:read")]
internal sealed class ProbeReadOnlyWriteQueryHandler(ITenantDb db, ITenantScopeAccessor scopes)
    : IQueryHandler<ProbeReadOnlyWriteQuery, Result<Guid>>
{
    public async ValueTask<Result<Guid>> Handle(ProbeReadOnlyWriteQuery query, CancellationToken cancellationToken)
    {
        await ProbeRows.InsertAsync(db, scopes, query.Id, cancellationToken).ConfigureAwait(false);

        return Result.Success(query.Id);
    }
}

/// <summary>A command that inserts one probe row and answers 201 (the write path of C3/C4).</summary>
internal sealed record ProbeWriteCommand(Guid Id) : ICommand<Result<Guid>>;

[Permission("probe:write")]
internal sealed class ProbeWriteCommandHandler(ITenantDb db, ITenantScopeAccessor scopes)
    : ICommandHandler<ProbeWriteCommand, Result<Guid>>
{
    public async ValueTask<Result<Guid>> Handle(ProbeWriteCommand command, CancellationToken cancellationToken)
    {
        await ProbeRows.InsertAsync(db, scopes, command.Id, cancellationToken).ConfigureAwait(false);

        return Result.Success(command.Id, 201);
    }
}

/// <summary>Rejected by its validator (T4.1): validation is stage 2, before the transaction opens.</summary>
internal sealed record ProbeInvalidCommand(Guid Id) : ICommand<Result<Guid>>;

internal sealed class ProbeInvalidCommandValidator : AbstractValidator<ProbeInvalidCommand>
{
    public ProbeInvalidCommandValidator() =>
        RuleFor(command => command.Id)
            .Must(id => id != Guid.Empty)
            .WithMessage("id is required");
}

[Permission("probe:write")]
internal sealed class ProbeInvalidCommandHandler(ITenantDb db, ITenantScopeAccessor scopes)
    : ICommandHandler<ProbeInvalidCommand, Result<Guid>>
{
    public async ValueTask<Result<Guid>> Handle(ProbeInvalidCommand command, CancellationToken cancellationToken)
    {
        await ProbeRows.InsertAsync(db, scopes, command.Id, cancellationToken).ConfigureAwait(false);

        return Result.Success(command.Id);
    }
}

/// <summary>Denied by the permission checker (T4.2): the scope opens and rolls back, nothing commits.</summary>
internal sealed record ProbeDeniedCommand(Guid Id) : ICommand<Result<Guid>>;

[Permission("probe:denied")]
internal sealed class ProbeDeniedCommandHandler(ITenantDb db, ITenantScopeAccessor scopes)
    : ICommandHandler<ProbeDeniedCommand, Result<Guid>>
{
    public async ValueTask<Result<Guid>> Handle(ProbeDeniedCommand command, CancellationToken cancellationToken)
    {
        await ProbeRows.InsertAsync(db, scopes, command.Id, cancellationToken).ConfigureAwait(false);

        return Result.Success(command.Id);
    }
}

/// <summary>Writes, then answers a failure Result: the write must roll back with it (C2).</summary>
internal sealed record ProbeFailingCommand(Guid Id) : ICommand<Result<Guid>>;

[Permission("probe:write")]
internal sealed class ProbeFailingCommandHandler(ITenantDb db, ITenantScopeAccessor scopes)
    : ICommandHandler<ProbeFailingCommand, Result<Guid>>
{
    public async ValueTask<Result<Guid>> Handle(ProbeFailingCommand command, CancellationToken cancellationToken)
    {
        await ProbeRows.InsertAsync(db, scopes, command.Id, cancellationToken).ConfigureAwait(false);

        return Result.Failure<Guid>(new Error("probe_conflict", "the probe refused to finish", 409));
    }
}

/// <summary>Idempotent: the same key with the same payload replays, a different payload is 409 (00 §9.1).</summary>
internal sealed record ProbeIdempotentCommand(Guid Id, string Key) : ICommand<Result<Guid>>, IIdempotentCommand
{
    /// <inheritdoc />
    public string IdempotencyKey => Key;
}

[Permission("probe:write")]
internal sealed class ProbeIdempotentCommandHandler(ITenantDb db, ITenantScopeAccessor scopes)
    : ICommandHandler<ProbeIdempotentCommand, Result<Guid>>
{
    public async ValueTask<Result<Guid>> Handle(ProbeIdempotentCommand command, CancellationToken cancellationToken)
    {
        await ProbeRows.InsertAsync(db, scopes, command.Id, cancellationToken).ConfigureAwait(false);

        return Result.Success(command.Id, 201);
    }
}

/// <summary>
/// An unattributed DTO response lets the probe show that a message without an authorization
/// gate passes through the tenant stages rather than being refused for its response shape.
/// </summary>
internal sealed record ProbeDtoResult(Guid Id);

internal sealed record ProbeDtoCommand(Guid Id) : ICommand<ProbeDtoResult>;

internal sealed class ProbeDtoCommandHandler : ICommandHandler<ProbeDtoCommand, ProbeDtoResult>
{
    public ValueTask<ProbeDtoResult> Handle(ProbeDtoCommand command, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ProbeDtoResult(command.Id));
}

/// <summary>The one place the probe handlers write, so every rollback test counts the same row.</summary>
internal static class ProbeRows
{
    internal const string Label = "pipeline-probe";

    /// <summary>
    /// Inserts one <c>public.rls_probe</c> row on the ambient scope's connection and
    /// transaction. `org_id` comes from the scope, never from the caller — the same
    /// rule <c>OutboxWriter</c> follows.
    /// </summary>
    internal static Task<int> InsertAsync(ITenantDb db, ITenantScopeAccessor scopes, Guid id, CancellationToken cancellationToken) =>
        db.ExecuteAsync(
            "insert into public.rls_probe (org_id, id, label) values (@OrgId, @Id, @Label)",
            new { OrgId = scopes.Require().OrgId, Id = id, Label },
            cancellationToken);
}

/// <summary>
/// Grants (or denies) any permission, and records what the authorization stage
/// asked for — the 02.2.1-T2 evaluator's seam, substituted the same way a host's
/// real evaluator replaces the pipeline's fail-closed default.
/// </summary>
internal sealed class RecordingPermissionEvaluator(bool granted) : IPermissionEvaluator
{
    private readonly List<(string Permission, Guid OrgId)> _checks = [];

    internal IReadOnlyList<(string Permission, Guid OrgId)> Checks => _checks;

    /// <inheritdoc />
    public ValueTask<PermissionDecision> EvaluateAsync(
        string permission,
        IRequestPrincipal principal,
        IRequestTenantContext context,
        CancellationToken cancellationToken)
    {
        _checks.Add((permission, context.OrgId));
        return ValueTask.FromResult(granted
            ? PermissionDecision.Granted
            : PermissionDecision.Denied(ApiErrorCode.Forbidden));
    }
}

/// <summary>
/// The caller of every flow of this host: 02.6.2-T1.1 resolves the credential and
/// publishes the principal next to the tenant, and until it lands this stands in for
/// it so the probe commands run as a member. A test that needs "no principal
/// resolved" clears <see cref="Current"/>.
/// </summary>
internal sealed class ProbePrincipalAccessor : IRequestPrincipalAccessor
{
    /// <inheritdoc />
    public IRequestPrincipal? Current { get; set; } = RequestPrincipal.Member(Guid.CreateVersion7());
}
