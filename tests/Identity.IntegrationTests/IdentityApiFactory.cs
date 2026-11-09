using System.Security.Cryptography;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.Modules.Identity.Authorization;
using Evidgate.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Identity.IntegrationTests;

/// <summary>
/// What the test host stands in for until 02.6.2 ships the real entry-point
/// resolution: the request tenant, the caller, the permission evaluator and the
/// module's flag. Every default is the production one — no caller, the module's real
/// evaluator, custom roles off.
/// </summary>
/// <param name="OrgId">The org every request runs as.</param>
/// <param name="ProjectId">The project the scope is narrowed to, when the case needs one.</param>
/// <param name="Principal">The resolved caller, or null when the host resolved none.</param>
/// <param name="Evaluator">A substitute evaluator, or null to keep the module's own (02.2.1-T2.2).</param>
/// <param name="CustomRolesEnabled">The <c>EVIDGATE__IDENTITY__CUSTOMROLESENABLED</c> value the host binds.</param>
internal sealed record IdentityHostSetup(
    Guid OrgId,
    Guid? ProjectId = null,
    IRequestPrincipal? Principal = null,
    IPermissionEvaluator? Evaluator = null,
    bool CustomRolesEnabled = false);

/// <summary>
/// The real Api composition (<c>Program</c> → <c>ApiHost</c>) over the harness's
/// migrated database, for the catalogue and authorization route tests (02.2.1): the
/// same routes, the same C2 pipeline, the same <c>ResultEndpointFilter</c> and — since
/// T2.2 — the same permission evaluator the deployed host runs, so a route the host
/// forgets to compose or a denial that loses its <c>error.code</c> fails here.
/// </summary>
/// <remarks>
/// Two substitutions stand in for what 02.6.2 will ship: the request tenant and the
/// caller are published by a test middleware (the host's principal resolver does not
/// exist yet), and the evaluator is the test's where the case needs one. Without a
/// caller the production composition answers 403 <c>forbidden</c> (the fail-closed
/// default), which the deny-path suite asserts on purpose.
/// </remarks>
internal sealed class IdentityApiFactory : WebApplicationFactory<Program>
{
    private readonly IdentityHostSetup _setup;

    private IdentityApiFactory(string connectionString, IdentityHostSetup setup)
    {
        ConnectionString = connectionString;
        _setup = setup;
    }

    /// <summary>The app-role connection string the host starts with (the harness database).</summary>
    public string ConnectionString { get; }

    /// <summary>
    /// A host whose request runs as a member of <paramref name="orgId"/> with
    /// <paramref name="grantAll"/> deciding whether every permission is granted —
    /// false keeps the production deny-all default.
    /// </summary>
    public static IdentityApiFactory Start(DatabaseFixture fixture, Guid orgId, bool grantAll = true)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        return Start(
            fixture,
            new IdentityHostSetup(
                orgId,
                Principal: RequestPrincipal.Member(DefaultActor(fixture, orgId)),
                Evaluator: grantAll ? new GrantAllPermissionEvaluator() : new DenyAllPermissionEvaluator()));
    }

    /// <summary>
    /// The caller the default host publishes: an existing active <c>tenant_admin</c> of the
    /// org, so a case about a member mutation acts at the org's owner rank. A random id
    /// remains the fallback for a route whose fixture seeds no administrator.
    /// </summary>
    private static Guid DefaultActor(DatabaseFixture fixture, Guid orgId)
    {
        using var connection = new NpgsqlConnection(fixture.OwnerConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            select m.user_id
              from public.membership m
              join public.membership_role mr
                   on mr.membership_id = m.id
                  and mr.org_id = m.org_id
              join public.role r
                   on r.id = mr.role_id
                  and r.org_id is null
                  and r.code = 'tenant_admin'
             where m.org_id = @org_id
               and m.status = 'active'
               and m.user_id is not null
             order by m.user_id
             limit 1
            """;
        command.Parameters.AddWithValue("org_id", orgId);

        return command.ExecuteScalar() as Guid? ?? Guid.CreateVersion7();
    }

    /// <summary>A host configured by <see cref="IdentityHostSetup"/>.</summary>
    public static IdentityApiFactory Start(DatabaseFixture fixture, IdentityHostSetup setup)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(setup);

        return new IdentityApiFactory(fixture.AppConnectionString, setup);
    }

    /// <summary>Base64 of a fresh 32-byte pepper for the invite group (00 §5.7 S3: no literal key in a test).</summary>
    private static class InviteTestPepper
    {
        /// <summary>Base64 pepper of this process, so every host of the suite shares one value.</summary>
        public static string Value { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Database:ConnectionString"] = ConnectionString,
                ["EVIDGATE:Runs:Cancel:SoftStopDelay"] = "00:00:30",
                ["EVIDGATE:Runs:Cancel:KillDelay"] = "00:02:00",
                // The module's flag, bound through AddIdentityModule exactly as the
                // deployed host binds it (02.2.1 §5, RECOMMENDATIONS 02-Q3).
                ["EVIDGATE:Identity:CustomRolesEnabled"] = _setup.CustomRolesEnabled ? "true" : "false",
                // 02.1.2-T2: invite minting needs a pepper, and every membership test that invites
                // would otherwise answer 503. A fresh random key per host, never a literal.
                ["EVIDGATE:Invite:Pepper"] = InviteTestPepper.Value,
            }));

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter>(new RequestContextStartupFilter(_setup));

            if (_setup.Evaluator is { } evaluator)
            {
                // Appended after the module's Replace, so a substituted evaluator
                // wins for this host only (the IdentityApiFactory contract). The
                // caller-set seam of W7-SEC-02 is substituted with it: the commands'
                // self-escalation rule and the gate must not disagree about what the
                // fake caller holds, or a fake-based create could be refused by a real
                // membership read it never had. A fake that does not implement the seam
                // (the pipeline's own DenyAllPermissionEvaluator) leaves the module's
                // evaluator in place — harmlessly, since its verdict never lets a
                // handler run.
                services.AddSingleton(evaluator);

                if (evaluator is ICallerPermissionSet callerSet)
                {
                    services.AddSingleton(callerSet);
                }
            }
        });
    }

    /// <summary>Publishes a fixed tenant and caller for every request, then runs the app's pipeline.</summary>
    private sealed class RequestContextStartupFilter(IdentityHostSetup setup) : IStartupFilter
    {
        /// <inheritdoc />
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                var tenant = context.RequestServices.GetRequiredService<IRequestTenantContextSetter>();
                using var tenantScope = tenant.Publish(setup.OrgId, setup.ProjectId);

                IDisposable? principalScope = setup.Principal is { } principal
                    ? context.RequestServices.GetRequiredService<IRequestPrincipalSetter>().Publish(principal)
                    : null;

                try
                {
                    await nextMiddleware().ConfigureAwait(false);
                }
                finally
                {
                    principalScope?.Dispose();
                }
            });

            next(app);
        };
    }
}

/// <summary>
/// Grants every permission — the member the tests assume (02.6.2's real evaluator is
/// <c>EvaluatorTests</c>'). It answers the W7-SEC-02 caller-set seam with the whole
/// catalogue, so a fake-authorized caller holds everything and a test that is about a
/// different rule is never refused by the self-escalation check. It publishes no
/// membership row unless one is named: a route that records its caller as a row
/// (the api-token create's <c>created_by</c>) must run on the real evaluator or pass
/// the id of a membership that exists.
/// </summary>
/// <param name="membershipId">The membership the seam reports, or null for "this caller has no row".</param>
internal sealed class GrantAllPermissionEvaluator(Guid? membershipId = null) : IPermissionEvaluator, ICallerPermissionSet
{
    /// <inheritdoc />
    public ValueTask<PermissionDecision> EvaluateAsync(
        string permission,
        IRequestPrincipal principal,
        IRequestTenantContext context,
        CancellationToken cancellationToken) => ValueTask.FromResult(PermissionDecision.Granted);

    /// <inheritdoc />
    public ValueTask<CallerAccess> ResolveAsync(
        Guid orgId,
        IRequestPrincipal principal,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new CallerAccess(
            membershipId,
            Permissions.All.ToHashSet(StringComparer.Ordinal)));
}

/// <summary>
/// Denies with one fixed 00 §9.2 code — the deny-path suite's evaluator. It holds
/// nothing, the fail-closed half of the caller-set seam (W7-SEC-02).
/// </summary>
internal sealed class FixedPermissionEvaluator(string denyCode) : IPermissionEvaluator, ICallerPermissionSet
{
    /// <inheritdoc />
    public ValueTask<PermissionDecision> EvaluateAsync(
        string permission,
        IRequestPrincipal principal,
        IRequestTenantContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(PermissionDecision.Denied(denyCode));

    /// <inheritdoc />
    public ValueTask<CallerAccess> ResolveAsync(
        Guid orgId,
        IRequestPrincipal principal,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new CallerAccess(null, new HashSet<string>(StringComparer.Ordinal)));
}
