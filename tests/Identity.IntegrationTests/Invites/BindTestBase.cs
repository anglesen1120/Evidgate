using System.Text;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Domain;
using Evidgate.Modules.Identity.Invites;
using Evidgate.Testing;
using Microsoft.Extensions.Options;

namespace Identity.IntegrationTests.Invites;

/// <summary>
/// The shared arrangement of the 02.1.2-T2 bind suites: the migrated database from the
/// harness (isolated by tenant unless the suite asks for a private copy), the real invite
/// key material and a <c>MembershipBinder</c> over the app role — so every case runs the
/// production resolvers, the production tenant scope and the production single-use update.
/// </summary>
/// <remarks>
/// <para>
/// The rows themselves are arranged through <see cref="InviteTestBase"/>'s helpers, which is
/// the one seeding convention of this feature (00 §5.5 C3: arranging rows is the one job a
/// test may not do through the tenant path). Only what the bind suites need beyond that lives
/// here: the pepper, a minted token per seeded invite, the org's seat limit, and the
/// assertions' tenant-scope reads.
/// </para>
/// <para>
/// The pepper is built at runtime, never written as a literal
/// (<c>scripts/check-no-secret-literals.sh</c>), and the invite secret of every seeded row is
/// the one <see cref="InviteTokenFormat"/> minted for it — so a passing bind also proves the
/// mint the command side performs and the verify the binder performs agree.
/// </para>
/// </remarks>
public abstract class BindTestBase : InviteTestBase
{
    /// <summary>The pepper every hash in these suites is made with (32 bytes, as the options require).</summary>
    protected static readonly byte[] Pepper = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();

    /// <summary>The pepper version stored beside every hash.</summary>
    protected const int PepperVersion = 3;

    private InviteSecrets? _secrets;

    protected BindTestBase(DatabaseFixture fixture, DatabaseIsolation isolation = DatabaseIsolation.Shared)
        : base(fixture, isolation)
    {
    }

    /// <summary>The invite key material over the suite's pepper.</summary>
    private protected InviteSecrets Secrets => _secrets ??= new InviteSecrets(
        Options.Create(new InviteOptions { Pepper = Convert.ToBase64String(Pepper), PepperVersion = PepperVersion }));

    /// <summary>
    /// The binder under test, on the app-role data source and the production scope factory.
    /// </summary>
    /// <param name="clockOffset">
    /// How far ahead of the database clock the binder's clock reads; the expiry case needs a
    /// row the resolver still returns but the binder must refuse.
    /// </param>
    private protected MembershipBinder CreateBinder(TimeSpan? clockOffset = null) =>
        new(
            Fixture.AppDataSource,
            Fixture.Scopes,
            Fixture.Db,
            Secrets,
            clockOffset is { } offset ? new OffsetClock(offset) : TimeProvider.System,
            Audit);

    /// <summary>
    /// The audit buffer every binder in these suites records on: the production
    /// <see cref="AuditTrail"/> (an ambient buffer per async flow), independent of the harness's
    /// service provider so a suite can inspect what one bind appended.
    /// </summary>
    private protected AuditTrail Audit { get; } = new();

    /// <summary>Seeds one pending invite with a freshly minted token and its expiry.</summary>
    /// <param name="orgId">The org.</param>
    /// <param name="email">The invited address; stored normalised.</param>
    /// <param name="status">The row's status (a cancelled or expired invite keeps its token for audit).</param>
    /// <param name="expiresIn">How far in the future the invite expires, or how far in the past when negative.</param>
    protected async Task<SeededInvite> SeedInviteAsync(
        Guid orgId,
        string email,
        string status = MembershipStatus.Pending,
        TimeSpan? expiresIn = null)
    {
        var generated = InviteTokenFormat.Generate();
        var (version, hash) = Secrets.HashCurrent(Encoding.UTF8.GetBytes(generated.Secret));
        var normalised = InviteEmail.Normalize(email);
        var membershipId = await SeedMembershipAsync(
            orgId,
            normalised,
            status,
            generated.Prefix,
            hash,
            version,
            DateTimeOffset.UtcNow.Add(expiresIn ?? InviteLifetime.Default));

        return new SeededInvite(membershipId, normalised, generated.Prefix, generated.Full);
    }

    /// <summary>Seeds the plain pending invite of 02.1.2-T1: no token, so nothing can bind it.</summary>
    /// <param name="orgId">The org.</param>
    /// <param name="email">The invited address.</param>
    protected Task<Guid> SeedPendingInviteWithoutTokenAsync(Guid orgId, string email) =>
        SeedMembershipAsync(orgId, InviteEmail.Normalize(email));

    /// <summary>Seeds one active membership, so the org has a member the seat count sees.</summary>
    /// <param name="orgId">The org.</param>
    /// <param name="email">The member's address.</param>
    /// <param name="userId">The bound user, or a fresh id.</param>
    protected async Task<(Guid MembershipId, Guid UserId)> SeedActiveMemberAsync(
        Guid orgId,
        string email,
        Guid? userId = null)
    {
        var boundUserId = userId ?? await SeedUserAsync().ConfigureAwait(false);
        var membershipId = await SeedMembershipAsync(
            orgId,
            InviteEmail.Normalize(email),
            MembershipStatus.Active);

        await Fixture.SeedAsync(
            "update public.membership set user_id = @user_id where id = @id",
            [("user_id", boundUserId), ("id", membershipId)],
            Token);

        return (membershipId, boundUserId);
    }

    /// <summary>
    /// Seeds the <c>public."user"</c> row a <c>membership.user_id</c> now needs and returns the
    /// id: 0044 adds and validates <c>membership_user_fk</c> (<c>membership.user_id →
    /// public."user"</c>), so a bind — or any fixture that writes a bound user — fails 23503
    /// unless the user row exists, exactly as in production, where the login upsert writes the
    /// <c>user</c> before the binder writes the <c>user_id</c>.
    /// </summary>
    /// <param name="userId">The id to seed, or null for a fresh UUIDv7.</param>
    protected async Task<Guid> SeedUserAsync(Guid? userId = null)
    {
        var id = userId ?? Guid.CreateVersion7();

        await Fixture.SeedAsync(
            """insert into public."user" (id, email) values (@id, @email)""",
            [("id", id), ("email", $"user-{id:N}@example.test")],
            Token);

        return id;
    }

    /// <summary>Sets the org's seat limit; null means unlimited.</summary>
    /// <param name="orgId">The org.</param>
    /// <param name="maxSeats">The limit, or null.</param>
    protected Task SetMaxSeatsAsync(Guid orgId, int? maxSeats) => Fixture.SeedAsync(
        "update public.organization set max_seats = @max_seats where id = @org_id",
        [("max_seats", (object?)maxSeats ?? DBNull.Value), ("org_id", orgId)],
        Token);

    /// <summary>Reads one membership's status and bound user through a real tenant scope.</summary>
    /// <param name="orgId">The org.</param>
    /// <param name="membershipId">The row.</param>
    protected async Task<(string Status, Guid? UserId)> MembershipStateAsync(Guid orgId, Guid membershipId)
    {
        await using var scope = await Fixture.BeginTenantScopeAsync(orgId, readOnlyTransaction: true, cancellationToken: Token);

        var status = await scope.Db.ScalarAsync<string>(
            "select status from public.membership where org_id = @org_id and id = @id",
            new { org_id = orgId, id = membershipId },
            Token) ?? throw new InvalidOperationException($"membership {membershipId:D} is not visible in org {orgId:D}");

        var userId = await scope.Db.ScalarAsync<Guid?>(
            "select user_id from public.membership where org_id = @org_id and id = @id",
            new { org_id = orgId, id = membershipId },
            Token);

        return (status, userId);
    }

    /// <summary>Counts the org's memberships in one status through a real tenant scope.</summary>
    /// <param name="orgId">The org.</param>
    /// <param name="status">The status to count.</param>
    protected async Task<long> CountInStatusAsync(Guid orgId, string status)
    {
        await using var scope = await Fixture.BeginTenantScopeAsync(orgId, readOnlyTransaction: true, cancellationToken: Token);

        return await scope.Db.ScalarAsync<long>(
            "select count(*) from public.membership where org_id = @org_id and status = @status",
            new { org_id = orgId, status },
            Token);
    }

    /// <summary>A pending invite as the suite seeded it: what a login would present.</summary>
    /// <param name="MembershipId">The membership row.</param>
    /// <param name="Email">The normalised invited address.</param>
    /// <param name="Prefix">The public lookup prefix.</param>
    /// <param name="Token">The full token a user would present (<c>evgi_&lt;prefix&gt;_&lt;secret&gt;</c>).</param>
    protected sealed record SeededInvite(
        Guid MembershipId,
        string Email,
        string Prefix,
        string Token);
}

/// <summary>A clock that reads ahead of the database's, so a still-resolvable invite is already expired.</summary>
/// <param name="offset">How far ahead of UTC the binder's clock reads.</param>
internal sealed class OffsetClock(TimeSpan offset) : TimeProvider
{
    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + offset;
}
