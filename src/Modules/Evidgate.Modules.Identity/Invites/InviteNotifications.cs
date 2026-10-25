using System.Globalization;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Outbox;

namespace Evidgate.Modules.Identity.Invites;

/// <summary>
/// The one piece of work the invite routes hand to the background (02.1.2-T2 §7): the email that
/// tells the invited address it has an invite. There is no email sender in V1 and none is stubbed —
/// the row is the contract, and the task type has no registered handler yet.
/// </summary>
/// <remarks>
/// <para>
/// <b>No secret, no address.</b> The payload carries the membership and the org, nothing else: the
/// token and the invited email are what the sender must look up in its own provisioning (PDPL,
/// 00 §5.7 S3), and a payload the database logs can never be a leak.
/// </para>
/// <para>
/// <b>One row per mint.</b> The idempotency key ends in the row version the mint produced
/// (<c>invite:v1</c> for the create, <c>invite:v2</c>, <c>v3</c> … for each resend), so
/// <c>outbox_org_idem_uq</c> makes a duplicate mint of one attempt impossible while a resend still
/// enqueues its own row.
/// </para>
/// </remarks>
internal static class InviteNotifications
{
    /// <summary>The outbox task type of the invite email; no handler registers it yet (02.1.2 §7).</summary>
    public const string TaskType = "identity.invite.notify";

    /// <summary><c>aggregateType</c> of the row: the membership it belongs to.</summary>
    public const string AggregateType = "membership";

    /// <summary><c>kind</c> of a first invite.</summary>
    public const string InviteKind = "invite";

    /// <summary><c>kind</c> of a resend.</summary>
    public const string ResendKind = "resend";

    /// <summary>
    /// The dedupe key of one mint: <c>identity:membership:&lt;id&gt;:invite:v&lt;version&gt;</c>, where
    /// <paramref name="version"/> is the version the row holds after the write/insert that minted the
    /// token (1 for the create, one more for each resend).
    /// </summary>
    /// <param name="membershipId">The membership that carries the invite.</param>
    /// <param name="version">The row's version after the minting write.</param>
    public static string IdempotencyKey(Guid membershipId, int version) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"identity:membership:{membershipId:D}:invite:v{version}");

    /// <summary>The payload: ids and the kind only — never the token, the prefix or the address.</summary>
    /// <param name="membershipId">The membership that carries the invite.</param>
    /// <param name="orgId">Its org, which is also the row's tenant.</param>
    /// <param name="kind">"invite" or "resend".</param>
    public static string Payload(Guid membershipId, Guid orgId, string kind) =>
        JsonSerializer.Serialize(new
        {
            membership_id = membershipId,
            org_id = orgId,
            kind,
        });

    /// <summary>Appends the row on the caller's tenant transaction, next to the membership write.</summary>
    /// <param name="outbox">The transactional outbox writer (01.5.1-T2.2).</param>
    /// <param name="membershipId">The membership that carries the invite.</param>
    /// <param name="orgId">Its org.</param>
    /// <param name="kind">"invite" or "resend".</param>
    /// <param name="version">The row's version after the minting write.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    public static Task<Guid> EnqueueAsync(
        IOutboxWriter outbox,
        Guid membershipId,
        Guid orgId,
        string kind,
        int version,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outbox);

        return outbox.EnqueueAsync(
            TaskType,
            Payload(membershipId, orgId, kind),
            AggregateType,
            membershipId,
            IdempotencyKey(membershipId, version),
            cancellationToken);
    }
}
