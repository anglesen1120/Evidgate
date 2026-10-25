using System.Text;
using Evidgate.Modules.Identity.Domain;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.Modules.Identity.Invites;

/// <summary>A minted invite: the plaintext token and the three columns it becomes.</summary>
/// <param name="Full">The plaintext <c>evgi_&lt;prefix&gt;_&lt;secret&gt;</c>; never stored, never logged.</param>
/// <param name="Prefix">The public lookup key (<c>membership.invite_secret_prefix</c>).</param>
/// <param name="Hash">HMAC-SHA256 of the secret half (<c>membership.invite_token_hash</c>).</param>
/// <param name="PepperVersion">The pepper version the hash was computed under.</param>
internal sealed record InviteMint(string Full, string Prefix, byte[] Hash, int PepperVersion)
{
    /// <summary>Writes the three token columns of <paramref name="membership"/>, leaving the status alone.</summary>
    /// <param name="membership">The row being created or resent.</param>
    /// <param name="clock">The deployment clock (<c>now()</c> is the transaction's, this is the same instant).</param>
    public void ApplyTo(Membership membership, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(clock);

        membership.InviteSecretPrefix = Prefix;
        membership.InviteTokenHash = Hash;
        membership.InvitePepperVersion = PepperVersion;
        membership.InviteExpiresAt = clock.GetUtcNow() + InviteLifetime.Default;
    }
}

/// <summary>
/// Mints the invite token of 02.1.2-T2 §5 on the caller's tenant transaction, and checks the one
/// thing the database cannot check for us cheaply: that the public prefix is free.
/// </summary>
/// <remarks>
/// <para>
/// <c>membership_invite_secret_prefix_key</c> is the authority — 62<sup>12</sup> prefixes make a
/// collision astronomically unlikely, so the pre-read exists only to make a re-mint in the same org
/// deterministic; three attempts, then the caller answers 503 <c>unavailable</c> (retryable), never
/// a 500.
/// </para>
/// <para>
/// The pre-read runs under RLS, so it sees this org's prefixes only. The unique index is
/// platform-wide, which is why the caller still maps a <c>23505</c> on it to the same 503: the whole
/// tenant transaction is aborted at that point (a failed statement cannot be retried inside it), so
/// a retry loop after the insert is impossible by construction.
/// </para>
/// </remarks>
internal static class InviteMints
{
    /// <summary>Attempts before the caller answers 503 instead of minting.</summary>
    internal const int Attempts = 3;

    /// <summary>Generates a token whose prefix this org does not already hold, or null after <see cref="Attempts"/>.</summary>
    /// <param name="context">The write model of the caller's tenant transaction.</param>
    /// <param name="secrets">The pepper seam that hashes the secret half.</param>
    /// <param name="cancellationToken">Cancels the prefix pre-reads.</param>
    public static async ValueTask<InviteMint?> MintAsync(
        IdentityDbContext context,
        InviteSecrets secrets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(secrets);

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            var token = InviteTokenFormat.Generate();

            var taken = await context.Memberships
                .AnyAsync(membership => membership.InviteSecretPrefix == token.Prefix, cancellationToken)
                .ConfigureAwait(false);

            if (taken)
            {
                continue;
            }

            var (version, hash) = secrets.HashCurrent(Encoding.UTF8.GetBytes(token.Secret));

            return new InviteMint(token.Full, token.Prefix, hash, version);
        }

        return null;
    }
}
