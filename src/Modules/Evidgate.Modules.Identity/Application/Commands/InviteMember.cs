using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;
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
using Evidgate.Modules.Identity.Invites;
using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Evidgate.Modules.Identity.Application.Commands;

/// <summary>
/// Invite a colleague to the org (<c>POST /v1/orgs/{o}/memberships</c>, 02.1.2 §5):
/// one <c>pending</c> membership bound to the normalised address, optionally with the
/// roles it starts with, carrying a minted invite token (02.1.2-T2). Idempotent under
/// the client's <c>Idempotency-Key</c> (T11); a second invite for the same address
/// while one is pending is 409 <c>invite_exists</c> (AC1), which the partial unique
/// index <c>membership_pending_email_key</c> decides, not a pre-read alone.
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
    : ICommand<Result<InviteIssuedView>>, IIdempotentCommand;

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
/// the URL), invite minting configured (503), unknown role (409 <c>unknown_role</c>),
/// self-escalation (403 <c>forbidden</c>, W7-SEC-02), duplicate pending invite (409
/// <c>invite_exists</c>), seat limit (429 <c>quota_exceeded</c>, 02.1.2-T2.3), then the
/// write, whose unique-index violations map to the same 409 and to a 503 for the
/// token prefix so a collision is never a 500.
/// An address that belongs to a user holding a removed membership of this org is
/// refused with 409 <c>member_removed</c>, before the seat check.
/// <para>
/// A role whose <c>kind</c> is not <c>member</c> is refused as
/// <c>unknown_role</c> like an invisible one (W7-SEC-03): the staff and service
/// catalogue rows are attached to a staff account or a service token, never to a
/// membership, and the RLS filter does not express that — the role is visible, which is
/// why the check is here rather than in the query the tenant can see through.
/// </para>
/// <para>
/// The minted token leaves through <see cref="InviteSecretCarrier"/>, never through the
/// result: the <c>Idempotency-Key</c> store persists this answer's body, and a replay
/// must not hand a secret back (design [DECISION D-S78-Q1]).
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
    IAuditTrail audit,
    IOutboxWriter outbox,
    InviteSecrets inviteSecrets,
    InviteSecretCarrier inviteSecretCarrier,
    TimeProvider clock)
    : ICommandHandler<InviteMemberCommand, Result<InviteIssuedView>>
{
    /// <inheritdoc />
    public async ValueTask<Result<InviteIssuedView>> Handle(
        InviteMemberCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(InviteMemberCommand));

        var orgId = scopes.Require().OrgId;

        if (command.OrgId != orgId)
        {
            return Result.Failure<InviteIssuedView>(Error.NotFound(OrgMismatch.Message));
        }

        // 02.1.2-T2 §5: without a pepper no token can be hashed, so nothing may be minted. Answered
        // before any write, like the session and CI key material (503 unavailable, retryable).
        if (!inviteSecrets.CanHash)
        {
            return Result.Failure<InviteIssuedView>(InviteErrors.NotConfigured);
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
            return Result.Failure<InviteIssuedView>(Error.Conflict(
                ApiErrorCode.UnknownRole,
                "one or more roles are neither member roles of the catalogue nor roles of this organization"));
        }

        var caller = principals.Current;

        // W7-SEC-02: an invite carries only what the caller already holds, whoever the
        // address belongs to (a second mailbox the caller controls is the caller).
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
            return Result.Failure<InviteIssuedView>(SelfEscalation.Refused);
        }

        // `membership_org_user_key` spans every status, so a pending row for this address
        // could never bind while the removed one exists: refuse instead of issuing an
        // invite no login can redeem.
        if (await context.Memberships
                .AnyAsync(
                    membership => membership.OrgId == orgId
                        && membership.InvitedEmail == email
                        && membership.Status == MembershipStatus.Removed
                        && membership.UserId != null,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            return Result.Failure<InviteIssuedView>(Error.Conflict(
                ApiErrorCode.MemberRemoved,
                "a removed member cannot be re-invited to this organization"));
        }

        if (await context.Memberships
                .AnyAsync(
                    membership => membership.OrgId == orgId
                        && membership.InvitedEmail == email
                        && membership.Status == MembershipStatus.Pending,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            return Result.Failure<InviteIssuedView>(DuplicateInvite);
        }

        // 02.1.2-T2.3: `active + pending` rows must stay below `max_seats`, measured under the org
        // row lock so concurrent invites of one org cannot overfill it. Resend does not count.
        var seats = await SeatQuota.CheckAsync(db, orgId, SeatCountMode.Invite, cancellationToken)
            .ConfigureAwait(false);

        if (!seats.HasRoom)
        {
            return Result.Failure<InviteIssuedView>(InviteErrors.QuotaExceeded(seats.Limit));
        }

        var mint = await InviteMints.MintAsync(context, inviteSecrets, cancellationToken).ConfigureAwait(false);

        if (mint is null)
        {
            return Result.Failure<InviteIssuedView>(InviteErrors.MintingUnavailable);
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

        mint.ApplyTo(membership, clock);

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
        catch (DbUpdateException exception) when (Violates(exception, PendingEmailIndex))
        {
            // `membership_pending_email_key` is the arbiter (02.1.2 §4): a concurrent
            // invite that passed the pre-read above must still produce exactly one row
            // and one 409.
            return Result.Failure<InviteIssuedView>(DuplicateInvite);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // The platform-wide prefix index (another org's row won the same 62^12 draw). The
            // aborted transaction cannot mint again, so this answers the retryable 503 the design
            // asks for instead of a 500.
            return Result.Failure<InviteIssuedView>(InviteErrors.MintingUnavailable);
        }

        var view = await MembershipReader.ReadAsync(db, orgId, membership.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"the membership {membership.Id} was inserted in this transaction yet cannot be read back");

        // One outbox row per mint, in the same transaction (§7): ids and the kind only.
        await InviteNotifications.EnqueueAsync(
            outbox,
            view.Id,
            orgId,
            InviteNotifications.InviteKind,
            view.Version,
            cancellationToken).ConfigureAwait(false);

        // The token travels outside the Result, so the response store never holds it.
        inviteSecretCarrier.Token = mint.Full;

        audit.Describe(new AuditObject("membership", view.Id.ToString("D")));

        return Result.Success(InviteIssuedView.From(view, inviteToken: null), status: 201);
    }

    /// <summary>The partial unique index of the pending address (0011).</summary>
    private const string PendingEmailIndex = "membership_pending_email_key";

    /// <summary>The 409 both the pre-read and the unique-index violation answer (AC1).</summary>
    private static Error DuplicateInvite => Error.Conflict(
        ApiErrorCode.InviteExists,
        "a pending invite already exists for that address in this organization");

    /// <summary>True when the failure is a unique violation (SQLSTATE 23505).</summary>
    private static bool IsUniqueViolation(DbUpdateException exception) => Violates(exception, constraint: null);

    /// <summary>True when the failure is a unique violation of <paramref name="constraint"/> (or of any, when null).</summary>
    private static bool Violates(DbUpdateException exception, string? constraint) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
        && (constraint is null || string.Equals(postgres.ConstraintName, constraint, StringComparison.Ordinal));
}
