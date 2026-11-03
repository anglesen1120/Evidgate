using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.Modules.Identity.Login.Audit;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Audit;

/// <summary>
/// The identity audit writer of 02.3.1-T2.2 (Q5) against the real database: one row per org in that
/// org's own chain, a separate transaction per org, nothing written for an empty org list, nothing
/// written for an org that refuses, and metadata that never carries an address or a subject.
/// </summary>
/// <remarks>
/// <para>
/// Every write goes through the production <see cref="IdentityAuditWriter"/> over the harness's real
/// tenant scope factory and app role, so the chain guard, the advisory lock and the RLS policy of
/// <c>audit_log</c> are all exercised; the rows are then read back through a real read-only tenant
/// scope and the chain is recomputed with <see cref="AuditChainVerifier"/>, which is the same verifier
/// the <c>audit.verify_chain</c> task runs.
/// </para>
/// <para>
/// The orgs are seeded through the harness's owner seam (00 §5.5 C3): arranging rows is the one job a
/// test may not do through the tenant path. The user id needs no row — <c>audit_log</c> has no foreign
/// key to <c>user</c> — so these suites test the writer alone, not the login that calls it.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class IdentityAuditWriterTests : TenantTest
{
    /// <summary>The buffer every write records through; one instance per test, as the host has one per process.</summary>
    private readonly AuditTrail _audit = new();

    public IdentityAuditWriterTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

    [Fact]
    public async Task Write_OneRowPerOrg_WithTheActionActorAndObject()
    {
        var orgA = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var orgB = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var userId = Guid.CreateVersion7();

        await CreateWriter().WriteAsync(
            new IdentityAuditEvent(
                IdentityAuditActions.Login,
                userId,
                "google",
                new Dictionary<string, string> { ["outcome"] = "linked" }),
            [orgA, orgB],
            Token);

        foreach (var orgId in new[] { orgA, orgB })
        {
            var rows = await RowsAsync(orgId);

            rows.Count.ShouldBe(1);

            var row = rows[0];
            row.Seq.ShouldBe(1L);
            row.Action.ShouldBe("auth.login");
            row.ActorType.ShouldBe("user");
            row.ActorId.ShouldBe(userId.ToString("D"));
            row.ObjectType.ShouldBe("user");
            row.ObjectId.ShouldBe(userId.ToString("D"));

            // The first row of a chain starts at the genesis hash, and the chain it began is intact.
            row.PrevHash.ShouldAllBe(value => value == 0);
            (await VerifyAsync(orgId)).ShouldBeNull();

            using var document = JsonDocument.Parse(row.MetadataJson);
            var metadata = document.RootElement;
            metadata.GetProperty("provider").GetString().ShouldBe("google");
            metadata.GetProperty("outcome").GetString().ShouldBe("linked");
        }
    }

    [Fact]
    public async Task Write_TwoOrgs_ExtendsTwoIndependentChains()
    {
        var orgA = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var orgB = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var userId = Guid.CreateVersion7();
        var movedFrom = Guid.CreateVersion7();
        var writer = CreateWriter();

        await writer.WriteAsync(new IdentityAuditEvent(IdentityAuditActions.Login, userId, "google"), [orgA, orgB], Token);

        // The one row each org has after the shared event: an append must extend the chain from here,
        // never rewrite what it already holds.
        var genesis = (await RowsAsync(orgA)).ShouldHaveSingleItem();

        // A second event for one org only: the other org's chain must not grow.
        await writer.WriteAsync(
            new IdentityAuditEvent(
                IdentityAuditActions.LinkMove,
                userId,
                "github",
                new Dictionary<string, string> { ["previous_id"] = movedFrom.ToString("D") }),
            [orgA],
            Token);

        var chainA = await RowsAsync(orgA);
        var chainB = await RowsAsync(orgB);

        chainA.Count.ShouldBe(2);
        chainA[1].Seq.ShouldBe(2L);
        chainA[1].Action.ShouldBe("auth.link_move");
        chainA[1].MetadataJson.ShouldContain(movedFrom.ToString("D"));

        // The second row extends the first: its prev_hash is exactly the first row's entry_hash, and the
        // append left the first row's own hash untouched rather than rebuilding the chain.
        chainA[1].PrevHash.SequenceEqual(chainA[0].EntryHash).ShouldBeTrue();
        chainA[0].EntryHash.SequenceEqual(genesis.EntryHash)
            .ShouldBeTrue("appending a row never rewrites an earlier row of the chain");

        chainB.Count.ShouldBe(1);
        chainB[0].Action.ShouldBe("auth.login");

        (await VerifyAsync(orgA)).ShouldBeNull();
        (await VerifyAsync(orgB)).ShouldBeNull();
    }

    [Fact]
    public async Task Write_EmptyOrgList_WritesNothing()
    {
        await Fixture.SeedOrgAsync(cancellationToken: Token);
        var userId = Guid.CreateVersion7();

        await CreateWriter().WriteAsync(new IdentityAuditEvent(IdentityAuditActions.Login, userId, "google"), [], Token);

        (await TotalAuditRowsAsync()).ShouldBe(0L);
    }

    [Fact]
    public async Task Write_DuplicateOrgIds_WritesOneRowPerChain()
    {
        var orgId = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var userId = Guid.CreateVersion7();

        await CreateWriter().WriteAsync(
            new IdentityAuditEvent(IdentityAuditActions.Link, userId, "github"),
            [orgId, orgId, orgId],
            Token);

        var rows = await RowsAsync(orgId);

        rows.Count.ShouldBe(1);
        rows[0].Seq.ShouldBe(1L);
        (await VerifyAsync(orgId)).ShouldBeNull();
    }

    [Fact]
    public async Task Write_UnknownOrg_Throws_KeepsEarlierOrgsAndWritesNothingForIt()
    {
        // Seeded ids sort before the unknown one (all-zero leading field against 0x7fffffff), so the
        // writer reaches both real orgs first: the call is ordered ascending, which is what makes the
        // partial progress below deterministic rather than incidental.
        var orgA = await SeedOrgWithIdAsync(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
        var orgB = await SeedOrgWithIdAsync(Guid.Parse("00000000-0000-0000-0000-0000000000a2"));
        var unknown = Guid.Parse("7fffffff-ffff-ffff-ffff-ffffffffffff");
        var userId = Guid.CreateVersion7();

        new[] { orgA, orgB, unknown }.Order().SequenceEqual(new[] { orgA, orgB, unknown }).ShouldBeTrue();

        var refusal = await Should.ThrowAsync<PostgresException>(async () => await CreateWriter().WriteAsync(
            new IdentityAuditEvent(IdentityAuditActions.Login, userId, "google"),
            [orgA, orgB, unknown],
            Token));

        // The audit row's foreign key to the organization is what refuses: fail-closed, not a silent skip.
        refusal.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);

        // The orgs committed before the failing one keep their rows, and both chains still verify.
        (await RowsAsync(orgA)).Count.ShouldBe(1);
        (await RowsAsync(orgB)).Count.ShouldBe(1);
        (await VerifyAsync(orgA)).ShouldBeNull();
        (await VerifyAsync(orgB)).ShouldBeNull();

        // The failing org wrote nothing, and nothing was left behind for it either.
        (await RowsAsync(unknown)).ShouldBeEmpty();
        (await TotalAuditRowsAsync()).ShouldBe(2L);
    }

    [Fact]
    public async Task Write_Metadata_CarriesTheProviderAndNoAddressOrSubject()
    {
        var orgId = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var userId = Guid.CreateVersion7();

        await CreateWriter().WriteAsync(
            new IdentityAuditEvent(IdentityAuditActions.Link, userId, "github"),
            [orgId],
            Token);

        var row = (await RowsAsync(orgId)).ShouldHaveSingleItem();
        using var document = JsonDocument.Parse(row.MetadataJson);
        var metadata = document.RootElement;

        // Only the allowlisted keys the writer is meant to add: no address, no subject, no token.
        metadata.EnumerateObject().Select(property => property.Name).ShouldBe(["provider"]);
        metadata.GetProperty("provider").GetString().ShouldBe("github");

        foreach (var property in metadata.EnumerateObject())
        {
            property.Value.GetString()!.ShouldNotContain("@", Case.Sensitive);
        }

        // The row identifies the person by id, never by the address the provider verified.
        row.ObjectId.ShouldBe(userId.ToString("D"));
        row.MetadataJson.ShouldNotContain("subject", Case.Insensitive);
    }

    [Fact]
    public async Task Write_KeyOutsideTheAllowlist_RefusesBeforeAnyOrgIsTouched()
    {
        var orgId = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var userId = Guid.CreateVersion7();

        // A key the allowlist does not carry is a programming error, not user input: it must fail the
        // call before the first transaction, so no org is half-audited (the caller answers 503).
        await Should.ThrowAsync<ArgumentException>(async () => await CreateWriter().WriteAsync(
            new IdentityAuditEvent(
                IdentityAuditActions.Link,
                userId,
                "google",
                new Dictionary<string, string> { ["not_allowlisted_key"] = "value" }),
            [orgId],
            Token));

        (await TotalAuditRowsAsync()).ShouldBe(0L);
    }

    /// <summary>The writer under test, over the harness's real scope factory and app-role data path.</summary>
    private IdentityAuditWriter CreateWriter() => new(Fixture.Scopes, Fixture.Db, _audit);

    /// <summary>Seeds one active org with a chosen id, so the write order of a case is fixed by the test.</summary>
    /// <param name="orgId">The id the org must carry.</param>
    private async Task<Guid> SeedOrgWithIdAsync(Guid orgId)
    {
        await Fixture.SeedAsync(
            "insert into public.organization (id, name, slug) values (@id, @name, @slug)",
            [
                ("id", orgId),
                ("name", "Audit writer org"),
                ("slug", "audit-" + Guid.NewGuid().ToString("N")[..12]),
            ],
            Token);

        return orgId;
    }

    /// <summary>The org's rows oldest first, read through a real read-only tenant scope (RLS applies).</summary>
    /// <param name="orgId">The org whose chain is read.</param>
    private async Task<IReadOnlyList<StoredRow>> RowsAsync(Guid orgId)
    {
        await using var scope = await Fixture.BeginTenantScopeAsync(orgId, readOnlyTransaction: true, cancellationToken: Token);

        return await scope.Db.QueryAsync<StoredRow>(
            """
            select seq as "Seq"
                 , actor_type as "ActorType"
                 , actor_id as "ActorId"
                 , action as "Action"
                 , object_type as "ObjectType"
                 , object_id as "ObjectId"
                 , coalesce(metadata, '{}'::jsonb)::text as "MetadataJson"
                 , prev_hash as "PrevHash"
                 , entry_hash as "EntryHash"
              from public.audit_log
             where org_id = @org_id
             order by seq
            """,
            new { org_id = orgId },
            Token);
    }

    /// <summary>Recomputes the org's chain with the production verifier, inside that org's scope.</summary>
    /// <param name="orgId">The org whose chain is verified.</param>
    private async Task<AuditChainFinding?> VerifyAsync(Guid orgId)
    {
        await using var scope = await Fixture.BeginTenantScopeAsync(orgId, readOnlyTransaction: true, cancellationToken: Token);

        return await new AuditChainVerifier(scope.Db).VerifyAsync(orgId, Token);
    }

    /// <summary>Every audit row in the database, counted on the owner connection (arrangement bookkeeping).</summary>
    private async Task<long> TotalAuditRowsAsync()
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from public.audit_log";

        return (long)(await command.ExecuteScalarAsync(Token))!;
    }

    /// <summary>The columns these suites assert on, in select order.</summary>
    /// <param name="Seq">Position in the org's chain.</param>
    /// <param name="ActorType">Who acted; always <c>user</c> here.</param>
    /// <param name="ActorId">The user id as text.</param>
    /// <param name="Action">The audited action string.</param>
    /// <param name="ObjectType">The object kind; always <c>user</c> here.</param>
    /// <param name="ObjectId">The user id as text.</param>
    /// <param name="MetadataJson">The stored metadata, as JSON text.</param>
    /// <param name="PrevHash">The previous row's entry hash.</param>
    /// <param name="EntryHash">This row's entry hash.</param>
    private sealed record StoredRow(
        long Seq,
        string ActorType,
        string ActorId,
        string Action,
        string? ObjectType,
        string? ObjectId,
        string MetadataJson,
        byte[] PrevHash,
        byte[] EntryHash);
}
