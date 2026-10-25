using System.Data.Common;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Testing;

namespace Identity.IntegrationTests;

/// <summary>
/// Row fixtures of the 02.2.1 membership/role suites: a member activated with the
/// given built-in role, the role ids of the seeded catalogue, and a role's grant set.
/// Everything is arranged through the harness's owner seam (01.6.1-T2), because
/// arranging rows is the one job a test may not do through the tenant path — the rows
/// themselves then travel through the app role and RLS like production.
/// </summary>
internal static class IdentityTestData
{
    /// <summary>
    /// An active membership in <paramref name="org"/> for a fresh user, holding the
    /// built-in role <paramref name="roleCode"/>.
    /// </summary>
    internal static async Task<Guid> SeedActiveMemberAsync(
        DatabaseFixture fixture,
        Guid org,
        string roleCode,
        string status = "active",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var userId = Guid.CreateVersion7();
        var membershipId = Guid.CreateVersion7();

        // 0044's `membership_user_fk` (membership.user_id -> public."user") makes a bound
        // membership name a real account, so the account is seeded before the membership.
        await fixture.SeedAsync(
            """insert into public."user" (id, email) values (@id, @email)""",
            [("id", userId), ("email", $"member-{userId:N}@example.test")],
            cancellationToken).ConfigureAwait(false);

        await fixture.SeedAsync(
            """
            insert into public.membership
                (id, org_id, user_id, invited_email, status, created_at, updated_at, version)
            values (@id, @org_id, @user_id, @email, @status, now(), now(), 1)
            """,
            [
                ("id", membershipId),
                ("org_id", org),
                ("user_id", userId),
                ("email", $"member-{userId:N}@example.test"),
                ("status", status),
            ],
            cancellationToken).ConfigureAwait(false);

        await fixture.SeedAsync(
            """
            insert into public.membership_role
                (id, org_id, membership_id, role_id, granted_at, created_at, updated_at, version)
            values (@id, @org_id, @membership_id, @role_id, now(), now(), now(), 1)
            """,
            [
                ("id", Guid.CreateVersion7()),
                ("org_id", org),
                ("membership_id", membershipId),
                ("role_id", await RoleIdAsync(fixture, org, roleCode, cancellationToken).ConfigureAwait(false)),
            ],
            cancellationToken).ConfigureAwait(false);

        return userId;
    }

    /// <summary>The id of a built-in catalogue role, read under RLS as the org.</summary>
    internal static async Task<Guid> RoleIdAsync(
        DatabaseFixture fixture,
        Guid org,
        string code,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using var scope = await fixture.BeginTenantScopeAsync(org, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var roleId = await scope.Db.QuerySingleOrDefaultAsync<Guid>(
            "select id from public.role where org_id is null and code = @code",
            new { code },
            cancellationToken).ConfigureAwait(false);

        if (roleId == Guid.Empty)
        {
            throw new InvalidOperationException($"no built-in role '{code}' in public.role");
        }

        return roleId;
    }

    /// <summary>The grant set of a role, read under RLS as the org.</summary>
    internal static async Task<IReadOnlyList<string>> RolePermissionsAsync(
        DatabaseFixture fixture,
        Guid org,
        Guid roleId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using var scope = await fixture.BeginTenantScopeAsync(org, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return await scope.Db.QueryAsync<string>(
            "select permission_code from public.role_permission where role_id = @role_id order by permission_code",
            new { role_id = roleId },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Corrupts a role's grant set with <paramref name="code"/> as the owner: a
    /// <c>role_permission</c> row a member principal must never be granted through
    /// (TL T79), which is exactly what the evaluator's read-time filter has to absorb.
    /// The row carries the org's <c>org_id</c>, so for a built-in role it is also the
    /// shape W7-SEC-05 refuses (an org-scoped grant on an <c>org_id is null</c> role).
    /// </summary>
    internal static Task<int> GrantAsync(
        DatabaseFixture fixture,
        Guid org,
        Guid roleId,
        string code,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        return fixture.SeedAsync(
            """
            insert into public.role_permission
                (id, org_id, role_id, permission_code, created_at, updated_at, version)
            values (@id, @org_id, @role_id, @code, now(), now(), 1)
            """,
            [
                ("id", Guid.CreateVersion7()),
                ("org_id", org),
                ("role_id", roleId),
                ("code", code),
            ],
            cancellationToken);
    }

    /// <summary>
    /// Attaches an extra role to the membership of <paramref name="userId"/> as the
    /// owner: the row a hand-written attach (or a state that predates a rule) would
    /// leave behind, which the evaluator's join has to absorb — the W7-SEC-03 kind case
    /// and, with <c>org_id</c> set, the W7-SEC-05 tie case.
    /// </summary>
    internal static Task<int> GrantRoleToMemberAsync(
        DatabaseFixture fixture,
        Guid org,
        Guid userId,
        Guid roleId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        return fixture.SeedAsync(
            """
            insert into public.membership_role
                (id, org_id, membership_id, role_id, granted_at, created_at, updated_at, version)
            select @id, @org_id, m.id, @role_id, now(), now(), now(), 1
              from public.membership m
             where m.org_id = @org_id
               and m.user_id = @user_id
            """,
            [
                ("id", Guid.CreateVersion7()),
                ("org_id", org),
                ("user_id", userId),
                ("role_id", roleId),
            ],
            cancellationToken);
    }

    /// <summary>
    /// True when the membership of <paramref name="userId"/> holds the built-in
    /// <paramref name="roleCode"/>, read as the owner so a refused command cannot hide a
    /// row that was written anyway (the "no row written" half of a refusal test).
    /// </summary>
    internal static async Task<bool> MemberHoldsRoleAsync(
        DatabaseFixture fixture,
        Guid org,
        Guid userId,
        string roleCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using var scope = await fixture.BeginTenantScopeAsync(org, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var count = await scope.Db.ScalarAsync<long>(
            """
            select count(*)
              from public.membership m
              join public.membership_role mr
                   on mr.org_id = m.org_id
                  and mr.membership_id = m.id
              join public.role r
                   on r.id = mr.role_id
             where m.org_id = @org_id
               and m.user_id = @user_id
               and r.code = @role_code
            """,
            new { org_id = org, user_id = userId, role_code = roleCode },
            cancellationToken).ConfigureAwait(false);

        return count > 0;
    }
}

/// <summary>
/// The P3 counter of the 02.2.1-T2.2 budget: every statement the evaluator issues
/// through <see cref="ITenantDb"/> is counted, so "permission resolution is one SQL
/// statement, never an N+1" is an assertion rather than a comment. It decorates the
/// production Dapper path, so nothing about the statement itself changes.
/// </summary>
internal sealed class CountingTenantDb(ITenantDb inner) : ITenantDb
{
    /// <summary>How many statements went through this decorator.</summary>
    internal int Statements { get; private set; }

    /// <inheritdoc />
    public DbConnection Connection => inner.Connection;

    /// <inheritdoc />
    public DbTransaction Transaction => inner.Transaction;

    /// <inheritdoc />
    public Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        Statements++;
        return inner.QueryAsync<T>(sql, parameters, cancellationToken);
    }

    /// <inheritdoc />
    public Task<T?> QuerySingleOrDefaultAsync<T>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        Statements++;
        return inner.QuerySingleOrDefaultAsync<T>(sql, parameters, cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> ExecuteAsync(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        Statements++;
        return inner.ExecuteAsync(sql, parameters, cancellationToken);
    }

    /// <inheritdoc />
    public Task<T?> ScalarAsync<T>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        Statements++;
        return inner.ScalarAsync<T>(sql, parameters, cancellationToken);
    }
}
