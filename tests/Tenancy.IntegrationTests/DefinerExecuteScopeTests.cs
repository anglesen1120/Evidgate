using Npgsql;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Who may execute three SECURITY DEFINER functions that act across tenants or outside a tenant. Each call is made
/// the way the role makes it (<c>set role</c> from the superuser connection), because a grant only counts when a
/// call is refused or served, and the exact ACL is read back so a widened grant fails here even when the new
/// grantee is a role this suite does not call as.
/// </summary>
[Trait("Category", "Docker")]
public sealed class DefinerExecuteScopeTests
{
    private const string OwnerOnly = "evidgate_owner";

    private readonly PostgresTenancyFixture _fixture;

    public DefinerExecuteScopeTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static readonly string[] Roles = ["evidgate_app", "evidgate_operator", "evidgate_worker_sched"];

    public static TheoryData<string, string, string> Functions => new()
    {
        { "evidgate.audit_org_ids(uuid, integer)", "select count(*) from evidgate.audit_org_ids(null, 1)", "evidgate_worker_sched" },
        { "evidgate.purge_expired_ci_token_issue(integer)", "select evidgate.purge_expired_ci_token_issue(1)", "evidgate_worker_sched" },
        {
            "evidgate.usage_price_insert_global(uuid, text, numeric, text, timestamptz, timestamptz, numeric, text)",
            "select evidgate.usage_price_insert_global(gen_random_uuid(), 'results', 0.0100, 'USD', "
            + "timestamptz '2030-01-01', null, null, 'scope-card')",
            "evidgate_app"
        },
        {
            "evidgate.session_admin_may_revoke(uuid, uuid, uuid)",
            "select evidgate.session_admin_may_revoke(gen_random_uuid(), gen_random_uuid(), gen_random_uuid())",
            OwnerOnly
        },
        {
            "evidgate.session_revoke_audit_append(uuid, uuid, text, text, jsonb, text, text)",
            "select evidgate.session_revoke_audit_append(gen_random_uuid(), gen_random_uuid(), 'user', 'x', '{}'::jsonb, null, null)",
            OwnerOnly
        },
    };

    [Theory]
    [MemberData(nameof(Functions))]
    public async Task Execute_IsOwnedByTheNamedRoleAlone(string signature, string call, string intended)
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();

        // The exact grantee list: the owner and the intended role, nothing else and never PUBLIC.
        var grantees = await _fixture.ColumnAsync<string>(
            database,
            "select case when a.grantee = 0 then 'public' else a.grantee::regrole::text end "
            + "from pg_proc p, aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a "
            + $"where p.oid = '{signature}'::regprocedure and a.privilege_type = 'EXECUTE' order by 1");
        grantees.Order(StringComparer.Ordinal).ShouldBe(
            new[] { "evidgate_owner", intended }.Distinct().Order(StringComparer.Ordinal), signature);

        // No grantee may pass EXECUTE on: a grant option widens the list without changing who is named above.
        (await _fixture.ScalarAsync<long>(
            database,
            "select count(*) from pg_proc p, aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a "
            + $"where p.oid = '{signature}'::regprocedure and a.privilege_type = 'EXECUTE' and a.is_grantable"))
            .ShouldBe(0, $"{signature} must carry no grant option");

        foreach (var role in Roles)
        {
            var error = await CallAsRoleAsync(database, role, call);
            if (role == intended)
            {
                error.ShouldBeNull($"{intended} is the caller the grant names for {signature}");
            }
            else
            {
                error.ShouldNotBeNull($"{role} must be refused {signature}");
                error.SqlState.ShouldBe("42501", $"{role} on {signature}");
                error.MessageText.ShouldContain("permission denied for function", customMessage: $"{role} on {signature}");
            }
        }
    }

    private async Task<PostgresException?> CallAsRoleAsync(string database, string role, string call)
    {
        await using var connection = await _fixture.OpenSuperuserAsync(database);
        await using var command = connection.CreateCommand();
        command.CommandText = $"set role {role}; {call}";
        try
        {
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            return null;
        }
        catch (PostgresException error)
        {
            return error;
        }
    }
}
