using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The quota counter's write surface: the app role may set the five columns the increment and a raise own and
/// nothing else, no role removes a counter, and no platform role reaches it — asserted in execution as well as in
/// the catalogue, because a table-wide grant says nothing about a column grant.
/// </summary>
public sealed partial class UsageQuotaSchemaTests
{
    [Fact]
    public async Task QuotaCounter_AppRoleWritesOnlyTheCounterColumns()
    {
        // Dedicated: TRUNCATE asks for ACCESS EXCLUSIVE on a table every other test reads.
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        var (org, project) = await SeedTenantAsync(database, "acme", "checkout");
        var id = Guid.CreateVersion7();

        await ExecuteAsync(_fixture.AppConnectionString(database), org, QuotaInsert(id, org, project, used: 3m));

        // The positive control: the five columns the increment and a raise write are inside the grant.
        await using (var app = await TenantScope.BeginAsync(_fixture.AppConnectionString(database), org))
        {
            (await app.ExecuteAsync(
                $"update public.quota_counter set used = 4, quota_limit = 120, warn_ratio = 0.9, "
                + $"warned_at = now(), blocked_at = now() where id = '{id}'"))
                .ShouldBe(1);
            await app.CommitAsync();
        }

        foreach (var (label, sql) in new[]
        {
            ("rewrite the counter's row id", $"update public.quota_counter set id = '{Guid.CreateVersion7()}' where id = '{id}'"),
            ("move the counter to another tenant", $"update public.quota_counter set org_id = '{Guid.CreateVersion7()}' where id = '{id}'"),
            ("move the counter to another project", $"update public.quota_counter set project_id = '{Guid.CreateVersion7()}' where id = '{id}'"),
            ("move the counter to another period", $"update public.quota_counter set period = '2026-11' where id = '{id}'"),
            ("move the counter to another metric", $"update public.quota_counter set metric = 'ai_tokens' where id = '{id}'"),
            ("backdate the row", $"update public.quota_counter set created_at = now() - interval '1 day' where id = '{id}'"),
            ("rewrite the update stamp", $"update public.quota_counter set updated_at = now() - interval '1 day' where id = '{id}'"),
            ("rewrite the concurrency token", $"update public.quota_counter set version = 1 where id = '{id}'"),
            ("delete the counter", $"delete from public.quota_counter where id = '{id}'"),
            ("truncate the counters", "truncate public.quota_counter"),
        })
        {
            var error = await CaptureErrorAsync(_fixture.AppConnectionString(database), org, sql);

            error.SqlState.ShouldBe("42501", $"{label} must be a permission error for evidgate_app");
        }

        (await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from public.quota_counter where id = '{id}' "
            + "and used = 4 and quota_limit = 120 and version = 2"))
            .ShouldBe(1, "every refused statement left the counter byte-identical in its own scope");

        foreach (var (role, privilege, expected) in new[]
        {
            ("evidgate_app", "SELECT", true),
            ("evidgate_app", "INSERT", true),
            ("evidgate_app", "UPDATE", false),
            ("evidgate_app", "DELETE", false),
            ("evidgate_app", "TRUNCATE", false),
            ("evidgate_operator", "SELECT", false),
            ("evidgate_worker_sched", "SELECT", false),
        })
        {
            (await _fixture.HasTablePrivilegeAsync(database, role, Table, privilege))
                .ShouldBe(expected, $"{role} {privilege} on quota_counter");
        }

        // UPDATE is table-level FALSE because the grant is per column; both halves say what the counter owns.
        foreach (var column in new[] { "used", "quota_limit", "warn_ratio", "warned_at", "blocked_at" })
        {
            (await _fixture.ScalarAsync<bool>(
                database,
                $"select has_column_privilege('evidgate_app', 'public.quota_counter', '{column}', 'UPDATE')"))
                .ShouldBeTrue($"the counter writes {column}");
        }

        foreach (var column in new[] { "id", "org_id", "project_id", "period", "metric", "created_at", "updated_at", "version" })
        {
            (await _fixture.ScalarAsync<bool>(
                database,
                $"select has_column_privilege('evidgate_app', 'public.quota_counter', '{column}', 'UPDATE')"))
                .ShouldBeFalse($"a counter's scope and audit columns never move ({column})");
        }
    }
}
