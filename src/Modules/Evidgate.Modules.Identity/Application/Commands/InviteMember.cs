using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Authorization;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Domain;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Evidgate.Modules.Identity.Application.Commands;

/// <summary>
/// Invite a colleague to the org (<c>POST /v1/orgs/{o}/memberships</c>, 02.1.2 §5):
/// one <c>pending</c> membership bound to the normalised address, optionally with the
/// roles it starts with. Idempotent under the client's <c>Idempotency-Key</c> (T11);
/// a second invite for the same address while one is pending is 409
/// <c>invite_exists</c> (AC1), which the partial unique index
/// <c>membership_pending_email_key</c> decides, not a pre-read alone.
/// </summary>
/// <param name="OrgId">The org from the route; it must equal the request tenant (otherwise 404).</param>
/// <param name="Email">The invited address; normalised before it is stored or compared.</param>
/// <param name="Roles">Role ids the membership starts with, or null for none.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1).</param>
internal sealed record InviteMemberCommand(
    Guid OrgId,
    string Email,
    IReadOnlyList<Guid>? Roles,
    string IdempotencyKey)
    : ICommand<Result<MembershipView>>, IIdempotentCommand;

/// <summary>
/// Shape validation of <see cref="InviteMemberCommand"/> (C2 stage 2): the address
/// is non-empty, within RFC 5321's 320 characters, and in the ASCII
/// <c>local@domain</c> shape of <see cref="InviteEmail"/> — the same subset the
/// database's normalisation check can compare against. Reasons are lower_snake
/// (00 §9.2).
/// </summary>
internal sealed class InviteMemberCommandValidator : AbstractValidator<InviteMemberCommand>
{
    /// <summary>The most role grants a single invite may carry (input limit, 00 §5.7 S3).</summary>
    private const int MaxInitialRoles = 32;

    /// <summary>Builds the rules.</summary>
    public InviteMemberCommandValidator()
    {
        RuleFor(command => command.Email)
            .NotEmpty()
            .WithErrorCode("required");

        RuleFor(command => command.Email)
            .MaximumLength(InviteEmail.MaxLength)
            .WithErrorCode("too_long")
            .When(command => !string.IsNullOrEmpty(command.Email));

        RuleFor(command => command.Email)
            .Must(email => InviteEmail.IsValid(email))
            .WithErrorCode("invalid_format")
            .When(command => !string.IsNullOrEmpty(command.Email));

        RuleFor(command => command.Roles)
            .Must(roles => roles is null || roles.Count <= MaxInitialRoles)
            .WithErrorCode("too_many");

        RuleFor(command => command.Roles)
            .Must(roles => roles is null || roles.All(roleId => roleId != Guid.Empty))
            .WithErrorCode("invalid");
    }
}

/// <summary>
/// Handler of <see cref="InviteMemberCommand"/>. The order of its refusals is
/// deliberately: route org vs request tenant (404 — the tenant is never taken from
/// the URL), unknown role (409 <c>unknown_role</c>), self-escalation (403
/// <c>forbidden</c>, W7-SEC-02), duplicate pending invite (409 <c>invite_exists</c>),
/// then the write, whose unique-index violation maps to the same 409 so a concurrent
/// duplicate cannot create a second row.
/// <para>
/// A role whose <c>kind</c> is not <c>member</c> is refused as
/// <c>unknown_role</c> like an invisible one (W7-SEC-03): the staff and service
/// catalogue rows are attached to a staff account or a service token, never to a
/// membership, and the RLS filter does not express that — the role is visible, which is
/// why the check is here rather than in the query the tenant can see through.
/// </para>
/// </summary>
[AuditedAction("membership.invite")]
[Permission(Permissions.Membership.Write)]
internal sealed class InviteMemberCommandHandler(
    ITenantDbContextFactory<IdentityDbContext> contexts,
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    ICallerPermissionSet permissions,
    IAuditTrail audit)
    : ICommandHandler<InviteMemberCommand, Result<MembershipView>>
{
    /// <inheritdoc />
    public async ValueTask<Result<MembershipView>> Handle(
        InviteMemberCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(InviteMemberCommand));

        var orgId = scopes.Require().OrgId;

        if (command.OrgId != orgId)
        {
            return Result.Failure<MembershipView>(Error.NotFound(OrgMismatch.Message));
        }

        var email = InviteEmail.Normalize(command.Email);
        var requested = command.Roles?.Distinct().ToArray() ?? [];

        await using var context = contexts.Create();

        // RLS decides which roles resolve: the built-in catalogue plus the caller's own
        // org rows, never another org's (02 §4.5). A request naming a role the tenant
        // cannot see is refused, not silently dropped.
        var roles = requested.Length == 0
            ? []
            : await context.Roles
                .Where(role => requested.Contains(role.Id) && role.Kind == RoleKind.Member)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        if (roles.Count != requested.Length)
        {
            return Result.Failure<MembershipView>(Error.Conflict(
                ApiErrorCode.UnknownRole,
                "one or more roles are neither member roles of the catalogue nor roles of this organization"));
        }

        var caller = principals.Current;

        // W7-SEC-02: an invite addressed to the caller's own membership is the same
        // self-grant as their membership id, so it carries only what the caller already
        // holds (SelfEscalation.IsSelfTargetAsync is the shared definition of "self").
        if (roles.Count > 0
            && await SelfEscalation
                .IsSelfTargetAsync(context, orgId, caller, targetUserId: null, email, cancellationToken)
                .ConfigureAwait(false))
        {
            var missing = await SelfEscalation.MissingAsync(
                permissions,
                orgId,
                caller,
                await SelfEscalation
                    .RoleCodesAsync(context, [.. roles.Select(role => role.Id)], cancellationToken)
                    .ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

            if (missing.Count > 0)
            {
                return Result.Failure<MembershipView>(SelfEscalation.Refused);
            }
        }

        if (await context.Memberships
                .AnyAsync(
                    membership => membership.OrgId == orgId
                        && membership.InvitedEmail == email
                        && membership.Status == MembershipStatus.Pending,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            return Result.Failure<MembershipView>(DuplicateInvite);
        }

        var invitedBy = caller?.UserId;
        var membership = new Membership
        {
            Id = Guid.CreateVersion7(),
            OrgId = orgId,
            InvitedEmail = email,
            Status = MembershipStatus.Pending,
            InvitedBy = invitedBy,
        };

        await context.Memberships.AddAsync(membership, cancellationToken).ConfigureAwait(false);

        foreach (var role in roles)
        {
            await context.MembershipRoles.AddAsync(
                new MembershipRole
                {
                    Id = Guid.CreateVersion7(),
                    OrgId = orgId,
                    MembershipId = membership.Id,
                    RoleId = role.Id,
                    GrantedBy = invitedBy,
                },
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // `membership_pending_email_key` is the arbiter (02.1.2 §4): a concurrent
            // invite that passed the pre-read above must still produce exactly one row
            // and one 409.
            return Result.Failure<MembershipView>(DuplicateInvite);
        }

        var view = await MembershipReader.ReadAsync(db, orgId, membership.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"the membership {membership.Id} was inserted in this transaction yet cannot be read back");

        audit.Describe(new AuditObject("membership", view.Id.ToString("D")));

        return Result.Success(view, status: 201);
    }

    /// <summary>The 409 both the pre-read and the unique-index violation answer (AC1).</summary>
    private static Error DuplicateInvite => Error.Conflict(
        ApiErrorCode.InviteExists,
        "a pending invite already exists for that address in this organization");

    /// <summary>True when the failure is a unique violation (SQLSTATE 23505).</summary>
    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
