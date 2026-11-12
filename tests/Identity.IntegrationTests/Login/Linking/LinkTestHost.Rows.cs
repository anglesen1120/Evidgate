using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Testing;

namespace Identity.IntegrationTests.Login.Linking;

/// <summary>
/// The assertion half of <see cref="LinkTestHost"/>: the response members a case reads, the owner-seam row reads,
/// the chain verification, and the throwaway audit fault that proves a failed append takes the link with it.
/// </summary>
internal sealed partial class LinkTestHost
{
    /// <summary>The raw body text, for the assertions that nothing sensitive reaches the wire.</summary>
    internal static Task<string> BodyAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Content.ReadAsStringAsync(Token);
    }

    /// <summary>A JSON member of the response body, or null when it is absent.</summary>
    internal static async Task<string?> MemberAsync(HttpResponseMessage response, string name)
    {
        ArgumentNullException.ThrowIfNull(response);

        var body = await response.Content.ReadAsStringAsync(Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        using var document = JsonDocument.Parse(body);

        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty(name, out var member)
            ? member.ToString()
            : null;
    }

    // ------------------------------------------------------------------ rows (owner seam)

    /// <summary>The <c>user.id</c> of <c>(provider, subject)</c>, read as the owner (the app role cannot).</summary>
    internal async Task<Guid?> UserIdAsync(string provider, string subject) =>
        await OwnerScalarAsync(
            "select user_id from public.user_identity where provider = @provider and subject = @subject",
            [("provider", provider), ("subject", subject)]).ConfigureAwait(false) is Guid id
            ? id
            : null;

    /// <summary>The <c>email_at_provider</c> of <c>(provider, subject)</c>, read as the owner.</summary>
    internal async Task<string?> IdentityEmailAsync(string provider, string subject) =>
        await OwnerScalarAsync(
            "select email_at_provider from public.user_identity where provider = @provider and subject = @subject",
            [("provider", provider), ("subject", subject)]).ConfigureAwait(false) as string;

    /// <summary>How many rows <paramref name="sql"/> counts, read as the owner.</summary>
    /// <param name="sql">The counting query.</param>
    /// <param name="values">Its parameters.</param>
    internal async Task<long> CountAsync(string sql, params (string Name, object Value)[] values) =>
        (long)(await OwnerScalarAsync(sql, values).ConfigureAwait(false))!;

    /// <summary>
    /// The org's audit rows oldest first, read through a real read-only tenant scope, so the RLS policy of
    /// <c>audit_log</c> applies exactly as it does for the deployment.
    /// </summary>
    /// <param name="orgId">The org whose chain is read.</param>
    internal async Task<IReadOnlyList<AuditRow>> AuditRowsAsync(Guid orgId)
    {
        await using var scope = await Fixture
            .BeginTenantScopeAsync(orgId, readOnlyTransaction: true, cancellationToken: Token)
            .ConfigureAwait(false);

        return await scope.Db.QueryAsync<AuditRow>(
            """
            select action as "Action"
                 , object_id::text as "ObjectId"
                 , coalesce(metadata, '{}'::jsonb)::text as "MetadataJson"
              from public.audit_log
             order by seq
            """,
            cancellationToken: Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Recomputes the org's chain with the production verifier inside that org's scope: the chain is intact when
    /// this answers null. The hash rule is never re-implemented here.
    /// </summary>
    /// <param name="orgId">The org whose chain is verified.</param>
    internal async Task<AuditChainFinding?> VerifyChainAsync(Guid orgId)
    {
        await using var scope = await Fixture
            .BeginTenantScopeAsync(orgId, readOnlyTransaction: true, cancellationToken: Token)
            .ConfigureAwait(false);

        return await new AuditChainVerifier(scope.Db).VerifyAsync(orgId, Token).ConfigureAwait(false);
    }

    /// <summary>Runs one statement as the owner (arranging the boundary of a case).</summary>
    internal Task ArrangeAsync(string sql, params (string Name, object Value)[] values) =>
        Fixture.SeedAsync(sql, values, Token);

    /// <summary>
    /// Installs the throwaway fault: a trigger on <c>audit_log</c> that raises on every insert, so a definer's
    /// chain append aborts inside its own transaction and everything the call wrote rolls back with it. Created
    /// as the owner under a name no production object uses, and dropped by <see cref="DropAuditFaultAsync"/>.
    /// </summary>
    internal async Task InstallAuditFaultAsync()
    {
        await DropAuditFaultAsync().ConfigureAwait(false);

        await ArrangeAsync(
            """
            create function public.link_audit_fault() returns trigger
              language plpgsql
              set search_path = ''
              as $$
              begin
                raise exception 'link audit fault';
              end
              $$;
            """).ConfigureAwait(false);

        await ArrangeAsync(
            "create trigger link_audit_fault before insert on public.audit_log "
            + "for each row execute function public.link_audit_fault()").ConfigureAwait(false);
    }

    /// <summary>Drops the throwaway trigger and its function, whatever the case's outcome was.</summary>
    internal async Task DropAuditFaultAsync()
    {
        await ArrangeAsync("drop trigger if exists link_audit_fault on public.audit_log").ConfigureAwait(false);
        await ArrangeAsync("drop function if exists public.link_audit_fault()").ConfigureAwait(false);
    }

    /// <summary>
    /// Seeds one active membership through the owner seam, which is how a case gives its signed-in user the
    /// membership a session requires (00 §5.4.8: no session without one).
    /// </summary>
    /// <param name="orgId">The org.</param>
    /// <param name="userId">The member.</param>
    /// <param name="status">The membership status.</param>
    internal Task SeedMembershipAsync(Guid orgId, Guid userId, string status = "active") =>
        ArrangeAsync(
            """
            insert into public.membership
                (id, org_id, user_id, invited_email, status, created_at, updated_at, version)
            values (@id, @org_id, @user_id, @email, @status, now(), now(), 1)
            """,
            ("id", Guid.CreateVersion7()),
            ("org_id", orgId),
            ("user_id", userId),
            ("email", "member-" + userId.ToString("N") + "@example.test"),
            ("status", status));

    /// <summary>One <c>audit_log</c> row as these suites assert on it.</summary>
    /// <param name="Action">The action, e.g. <c>auth.link</c>.</param>
    /// <param name="ObjectId">The audited object id as text.</param>
    /// <param name="MetadataJson">The allowlisted metadata, as JSON text.</param>
    internal sealed record AuditRow(string Action, string? ObjectId, string? MetadataJson);
}
