using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 13.1.1-T2.1 through a real PostgreSQL: migration <c>0040_identity_authoring_permissions.sql</c> seeds the four
/// catalogue strings 00 §2:60 adds for the authoring feature (CR-D-2, 13 §13 CR-2) and grants each to exactly the
/// built-in roles that line names.
/// <para>
/// The expectation below is transcribed from 00 §2:60, never read from <c>contracts/permissions.json</c>: the
/// migration's rows carry that file's content, so deriving the expectation from it would make the assertion
/// tautological. <c>SeedMatrixTests</c> (tests/Identity.IntegrationTests) is the suite that diffs the seeded catalogue
/// against the JSON file; this one pins the owner-facing mapping in the project that owns the schema and its
/// migrations.
/// </para>
/// <para>
/// The fixture is this project's own <see cref="PostgresTenancyFixture"/> — one digest-pinned PostgreSQL 18
/// container per assembly, a fresh database per test brought to head by the real <c>Evidgate.Migrator</c> — the
/// 07.3.1-T2 / 06.1.1-T1 precedent for a migration in this suite (<c>QuarantineSchemaTests</c>). These assertions
/// read as the superuser because the catalogue is platform reference data (<c>org_id is null</c>, 02 §4.5); the
/// tenant-visible read of the same rows is <c>SeedMatrixTests</c>' (the app role under RLS).
/// </para>
/// </summary>
[Trait("Category", "Docker")]
public sealed class AuthoringPermissionSeedTests
{
    /// <summary>
    /// 00 §2:60: read for every member role, write and generate for the drafting triple, script generation for the
    /// approving pair only (`qa_engineer` drafts, it does not open a pull request against customer code).
    /// </summary>
    private static readonly Dictionary<string, string[]> Overview = new(StringComparer.Ordinal)
    {
        ["authoring:read"] = ["qa_engineer", "release_approver", "tenant_admin", "test_maintainer", "viewer"],
        ["authoring:write"] = ["qa_engineer", "tenant_admin", "test_maintainer"],
        ["authoring:generate"] = ["qa_engineer", "tenant_admin", "test_maintainer"],
        ["script:generate"] = ["tenant_admin", "test_maintainer"],
    };

    private readonly PostgresTenancyFixture _fixture;

    public AuthoringPermissionSeedTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>
    /// AC17's seed half: the four rows exist as tenant-assignable, non-sensitive, non-reserved catalogue entries whose
    /// <c>resource</c>/<c>action</c> spell the code, and their <c>role_permission</c> cells are exactly 00 §2:60's
    /// mapping — no extra holder, no missing one.
    /// </summary>
    [Fact]
    public async Task SeedFourCodes_MappedPer00S2()
    {
        var database = await _fixture.SharedDatabaseAsync();

        // (a) The four catalogue rows: `resource:action == code` (the catalogue file's own rule), never `sensitive`
        // (none of them gates secret-adjacent data), never `reserved` (each has a named V1 consumer, 13.3.1/13.5.1,
        // so the flag would contradict the catalogue's definition of it), and tenant-assignable (a member role holds
        // every one of them).
        (await _fixture.ColumnAsync<string>(
            database,
            """
            select code || ':' || resource || ':' || action || ':' || sensitive::text || ':' || reserved::text
                   || ':' || tenant_assignable::text
              from public.permission
             where code in ('authoring:read', 'authoring:write', 'authoring:generate', 'script:generate')
             order by code
            """)).ShouldBe(
            [
                "authoring:generate:authoring:generate:false:false:true",
                "authoring:read:authoring:read:false:false:true",
                "authoring:write:authoring:write:false:false:true",
                "script:generate:script:generate:false:false:true",
            ]);

        // A seeded row without a description would fail the catalogue's own 422-free read surface and the
        // file-parity assertion in SeedMatrixTests; caught here so the migration, not the API, is named.
        (await _fixture.ColumnAsync<string>(
            database,
            """
            select code
              from public.permission
             where code in ('authoring:read', 'authoring:write', 'authoring:generate', 'script:generate')
               and (description is null or btrim(description) = '')
            """)).ShouldBeEmpty();

        // (b) The grant cells, one code at a time. The rows come back joined to the built-in roles only
        // (`org_id is null`): an org-scoped custom role cannot appear here.
        foreach (var (code, expected) in Overview)
        {
            (await _fixture.ColumnAsync<string>(
                database,
                $"""
                select r.code
                  from public.role_permission rp
                  join public.role r on r.id = rp.role_id
                 where rp.permission_code = '{code}'
                   and r.org_id is null
                 order by r.code collate "C"
                """)).ShouldBe(expected, $"the holders of '{code}' must be exactly 00 §2:60's mapping");
        }
    }
}
