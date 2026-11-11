using System.Net.Http.Headers;
using System.Security.Cryptography;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.Modules.Identity.Ci;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The real Api composition (<c>Program</c> → <c>ApiHost</c>) over one migrated test
/// database, for the 02.1.1-T2 registry routes: the same routes, C2 pipeline, envelope
/// filter and — with <see cref="RealEvaluator"/> — the same permission evaluator the
/// deployed host runs.
/// </summary>
/// <remarks>
/// 02.6.2 has not shipped the entry-point resolution of the request tenant and caller,
/// so a test middleware publishes them from <c>X-Test-*</c> headers (<see cref="Caller"/>
/// builds them). Without a caller the production default applies: no principal. Every
/// Npgsql command is captured (<see cref="Statements"/>) for the statement-budget test
/// (P3), because Npgsql 10 has no command interceptor (see Tasks.IntegrationTests'
/// OutboxDispatcherTests for the same technique).
/// </remarks>
internal sealed class TenancyApiFactory : WebApplicationFactory<Program>
{
    private const string OrgHeader = "X-Test-Org";
    private const string ProjectHeader = "X-Test-Project";
    private const string UserHeader = "X-Test-User";
    private const string EmailHeader = "X-Test-Email";
    private const string ServiceScopeHeader = "X-Test-Service-Scope";
    private const string ServiceOrgHeader = "X-Test-Service-Org";
    private const string ServicePermissionsHeader = "X-Test-Service-Permissions";

    private readonly string _connectionString;
    private readonly bool _realEvaluator;
    private readonly bool _realResolver;
    private readonly bool _ci;
    private readonly string _createdAt;

    private TenancyApiFactory(string connectionString, bool realEvaluator, bool realResolver = false, bool ci = false)
    {
        _connectionString = connectionString;
        _realEvaluator = realEvaluator;
        _realResolver = realResolver;
        _ci = ci;
        _createdAt = TenancyHostLeakGuard.Site();
    }

    /// <summary>The GitHub OIDC stand-in (a local RSA keypair) when built with <see cref="Ci"/>.</summary>
    public TestJwks Jwks { get; } = new();

    /// <summary>The configured CI key material, for tests that mint or check tokens.</summary>
    public static CiTestKeys Keys { get; } = new();

    /// <summary>
    /// <see cref="RealResolver"/> plus CI authentication: signing key and peppers configured, the real
    /// <c>ci_producer</c> authenticator in place, and GitHub's JWKS served from <see cref="Jwks"/>.
    /// </summary>
    public static TenancyApiFactory Ci(string appConnectionString, IReadOnlyDictionary<string, string?>? overrides = null) =>
        new(appConnectionString, realEvaluator: true, realResolver: true, ci: true) { _overrides = overrides };

    private IReadOnlyDictionary<string, string?>? _overrides;

    /// <summary>The caller a <see cref="WithPrincipal"/> host publishes for every request (06.1.2-T1).</summary>
    private (Guid Org, Guid Project, IRequestPrincipal Principal)? _publishedPrincipal;

    /// <summary>Extra in-memory configuration a <see cref="WithPrincipal"/> host adds (06.1.2-T1).</summary>
    private IReadOnlyDictionary<string, string?>? _extraConfiguration;

    /// <summary>Extra service registrations a <see cref="WithPrincipal"/> host applies (10.3.1-T1.2).</summary>
    private Action<IServiceCollection>? _extraServices;

    /// <summary>
    /// A host whose requests carry <paramref name="principal"/> — a service principal with a
    /// <c>CredentialId</c> (the slot-binding identity, T9) and an optional <c>CiRunReference</c>, which the
    /// <c>X-Test-*</c> header stand-in cannot express — plus optional extra configuration (06.1.2-T1) and
    /// optional service registrations (10.3.1-T1.2: the task queue, so a test can run the real consumer
    /// against the row this host's request produced).
    /// </summary>
    public static TenancyApiFactory WithPrincipal(
        string appConnectionString,
        Guid org,
        Guid project,
        IRequestPrincipal principal,
        IReadOnlyDictionary<string, string?>? configuration = null,
        Action<IServiceCollection>? services = null) =>
        new(appConnectionString, realEvaluator: true, realResolver: true)
        {
            _publishedPrincipal = (org, project, principal),
            _extraConfiguration = configuration,
            _extraServices = services,
        };

    /// <summary>Every command Npgsql executed for this host, whitespace-normalised, in order.</summary>
    public StatementLog Statements { get; } = new();

    private Action? _release;

    /// <summary>Re-points a pooled <see cref="WithPrincipal"/> host at the next test's caller.</summary>
    internal void Republish(Guid org, Guid project, IRequestPrincipal principal) =>
        _publishedPrincipal = (org, project, principal);

    /// <summary>Marks this host as pooled: disposal by a test only calls <paramref name="release"/>.</summary>
    internal void Pool(Action release) => _release = release;

    /// <summary>Really disposes a pooled host, when its pool is torn down.</summary>
    internal ValueTask TearDownAsync()
    {
        _release = null;
        TenancyHostLeakGuard.Stopped(this);
        return base.DisposeAsync();
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        if (_release is { } release)
        {
            release();
            return ValueTask.CompletedTask;
        }

        TenancyHostLeakGuard.Stopped(this);

        return base.DisposeAsync();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_release is { } release)
        {
            if (disposing)
            {
                release();
            }

            return;
        }

        TenancyHostLeakGuard.Stopped(this);
        base.Dispose(disposing);
    }

    /// <summary>A host whose evaluator grants every permission, with optional extra service registrations.</summary>
    public static TenancyApiFactory GrantAll(string appConnectionString, Action<IServiceCollection>? services = null) =>
        new(appConnectionString, realEvaluator: false) { _extraServices = services };

    /// <summary>A host with the module's real evaluator (membership rows and token claims decide).</summary>
    public static TenancyApiFactory RealEvaluator(string appConnectionString) => new(appConnectionString, realEvaluator: true);

    /// <summary>The request headers of one caller.</summary>
    /// <summary>
    /// The real tenant resolution of 02.6.2: no test middleware publishes a tenant. A
    /// header authenticator stands in for the session/token authenticators of 02.3.x/02.4.x,
    /// so membership, the project scope and the service claims all go through the
    /// production <c>TenantResolutionFilter</c> and the real evaluator.
    /// </summary>
    public static TenancyApiFactory RealResolver(
        string appConnectionString,
        IReadOnlyDictionary<string, string?>? configuration = null,
        Action<IServiceCollection>? services = null) =>
        new(appConnectionString, realEvaluator: true, realResolver: true)
        {
            _extraConfiguration = configuration,
            _extraServices = services,
        };

    /// <summary>Headers for <see cref="RealResolver"/>: a user, optionally naming an org by header.</summary>
    public static Action<HttpRequestHeaders> Resolved(
        Guid? user = null,
        string? email = null,
        Guid? orgHeader = null,
        Guid? serviceOrg = null,
        Guid? serviceProject = null,
        IReadOnlyCollection<string>? servicePermissions = null) => headers =>
    {
        if (user is { } u)
        {
            headers.Add(UserHeader, u.ToString());
        }

        if (email is not null)
        {
            headers.Add(EmailHeader, email);
        }

        if (orgHeader is { } o)
        {
            headers.Add("X-Evidgate-Org", o.ToString());
        }

        if (servicePermissions is not null)
        {
            headers.Add(ServicePermissionsHeader, string.Join(',', servicePermissions));
        }

        if (serviceOrg is { } so)
        {
            headers.Add(ServiceOrgHeader, so.ToString());
        }

        if (serviceProject is { } sp)
        {
            headers.Add(ServiceScopeHeader, sp.ToString());
        }
    };

    public static Action<HttpRequestHeaders> Caller(
        Guid? org = null,
        Guid? user = null,
        string? email = null,
        Guid? project = null,
        Guid? serviceScope = null,
        IReadOnlyCollection<string>? servicePermissions = null) => headers =>
    {
        if (org is { } o)
        {
            headers.Add(OrgHeader, o.ToString());
        }

        if (project is { } p)
        {
            headers.Add(ProjectHeader, p.ToString());
        }

        if (user is { } u)
        {
            headers.Add(UserHeader, u.ToString());
        }

        if (email is not null)
        {
            headers.Add(EmailHeader, email);
        }

        if (servicePermissions is not null)
        {
            headers.Add(ServicePermissionsHeader, string.Join(',', servicePermissions));
            if (serviceScope is { } scope)
            {
                headers.Add(ServiceScopeHeader, scope.ToString());
            }
        }
    };

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        TenancyHostLeakGuard.Started(this, _createdAt);
        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Database:ConnectionString"] = _connectionString,
                ["EVIDGATE:Runs:Cancel:SoftStopDelay"] = "00:00:30",
                ["EVIDGATE:Runs:Cancel:KillDelay"] = "00:02:00",
                ["EVIDGATE:Identity:CustomRolesEnabled"] = "false",
                // 02.1.2-T2: the invite routes mint a token, so the pepper must exist (see IdentityApiFactory).
                ["EVIDGATE:Invite:Pepper"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            }));

        if (_ci)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(Keys.Configuration);
                if (_overrides is not null)
                {
                    configuration.AddInMemoryCollection(_overrides);
                }
            });
        }

        if (_extraConfiguration is not null)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(_extraConfiguration));
        }

        builder.ConfigureTestServices(services =>
        {
            // First, so the caller's `user` row exists before anything the route writes can reference it
            // (0044's membership_user_fk names it on POST /v1/orgs — see SessionUserSeed).
            services.AddSingleton<IStartupFilter>(new SessionUserStartupFilter(_connectionString));

            if (_realResolver)
            {
                // In CI mode only the production ci_producer authenticator identifies service callers;
                // the header stand-in still identifies members.
                services.AddSingleton<ICallerAuthenticator, HeaderAuthenticator>();
            }

            if (_ci)
            {
                services.RemoveAll<IGitHubJwksProvider>();
                services.AddSingleton<IGitHubJwksProvider>(Jwks);
            }
            else
            {
                services.AddSingleton<IStartupFilter>(new RequestContextStartupFilter());
            }

            if (_publishedPrincipal is not null)
            {
                // Runs after RequestContextStartupFilter, so it replaces the header-derived caller. Read per
                // request, so a pooled host can be re-pointed at the next test's caller.
                services.AddSingleton<IStartupFilter>(
                    new PublishedPrincipalStartupFilter(() => _publishedPrincipal!.Value));
            }

            // The same app-role data source, with a logger that records each command.
            services.RemoveAll<NpgsqlDataSource>();
            services.AddSingleton(_ => new NpgsqlDataSourceBuilder(_connectionString)
                .UseLoggerFactory(Statements)
                .Build());

            if (!_realEvaluator)
            {
                services.AddSingleton<IPermissionEvaluator>(new GrantAllEvaluator());
            }

            // 10.3.1-T1.2: last, so the caller's additions see the host's final registrations
            // (the replaced NpgsqlDataSource included).
            _extraServices?.Invoke(services);
        });
    }

    /// <summary>
    /// Seeds the <c>public."user"</c> row a signed-in caller implies for the id the <c>X-Test-User</c> header
    /// names (0044's <c>membership_user_fk</c>; see <see cref="SessionUserSeed"/>). The stand-in authenticator
    /// takes the caller's place, so it also takes the place of the login upsert that writes this row.
    /// </summary>
    private sealed class SessionUserStartupFilter(string appConnectionString) : IStartupFilter
    {
        /// <inheritdoc />
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (Guid.TryParse(context.Request.Headers[UserHeader], out var user))
                {
                    await SessionUserSeed.EnsureAsync(
                            appConnectionString,
                            user,
                            context.Request.Headers[EmailHeader].ToString(),
                            context.RequestAborted)
                        .ConfigureAwait(false);
                }

                await nextMiddleware().ConfigureAwait(false);
            });

            next(app);
        };
    }

    /// <summary>Publishes the tenant and the caller the <c>X-Test-*</c> headers name.</summary>
    private sealed class RequestContextStartupFilter : IStartupFilter
    {
        /// <inheritdoc />
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                var headers = context.Request.Headers;
                IDisposable? tenantScope = null;
                IDisposable? principalScope = null;

                if (Guid.TryParse(headers[OrgHeader], out var org))
                {
                    Guid? project = Guid.TryParse(headers[ProjectHeader], out var p) ? p : null;
                    tenantScope = context.RequestServices.GetRequiredService<IRequestTenantContextSetter>().Publish(org, project);
                }

                IRequestPrincipal? principal = null;
                if (Guid.TryParse(headers[UserHeader], out var user))
                {
                    var email = headers[EmailHeader].ToString();
                    principal = RequestPrincipal.Member(user, string.IsNullOrEmpty(email) ? null : email);
                }
                else if (headers.TryGetValue(ServicePermissionsHeader, out var claimed))
                {
                    Guid? scope = Guid.TryParse(headers[ServiceScopeHeader], out var s) ? s : null;
                    var permissions = claimed.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries);
                    principal = RequestPrincipal.Service(scope, permissions);
                }

                if (principal is not null)
                {
                    principalScope = context.RequestServices.GetRequiredService<IRequestPrincipalSetter>().Publish(principal);
                }

                try
                {
                    await nextMiddleware().ConfigureAwait(false);
                }
                finally
                {
                    principalScope?.Dispose();
                    tenantScope?.Dispose();
                }
            });

            next(app);
        };
    }

    /// <summary>
    /// Publishes a fixed caller and tenant for every request (06.1.2-T1): the header stand-in cannot
    /// express a service principal's <c>CredentialId</c> or its <c>CiRunReference</c>.
    /// </summary>
    private sealed class PublishedPrincipalStartupFilter(
        Func<(Guid Org, Guid Project, IRequestPrincipal Principal)> current) : IStartupFilter
    {
        /// <inheritdoc />
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                var (org, project, principal) = current();
                using var tenantScope = context.RequestServices
                    .GetRequiredService<IRequestTenantContextSetter>()
                    .Publish(org, project);
                using var principalScope = context.RequestServices
                    .GetRequiredService<IRequestPrincipalSetter>()
                    .Publish(principal);

                await nextMiddleware().ConfigureAwait(false);
            });

            next(app);
        };
    }

    /// <summary>Grants every permission; used where authorization is not the rule under test.</summary>
    /// <summary>The stand-in credential authenticator: headers become an <see cref="AuthenticatedCaller"/>.</summary>
    private sealed class HeaderAuthenticator : ICallerAuthenticator
    {
        public ValueTask<AuthenticatedCaller?> AuthenticateAsync(ICallerRequest request, CancellationToken cancellationToken)
        {
            if (Guid.TryParse(request.Header(UserHeader), out var user))
            {
                var email = request.Header(EmailHeader);

                return ValueTask.FromResult<AuthenticatedCaller?>(new AuthenticatedCaller(
                    RequestPrincipal.Member(user, string.IsNullOrEmpty(email) ? null : email)));
            }

            if (request.Header(ServicePermissionsHeader) is { } claimed)
            {
                Guid? bound = Guid.TryParse(request.Header(ServiceOrgHeader), out var org) ? org : null;
                Guid? project = Guid.TryParse(request.Header(ServiceScopeHeader), out var p) ? p : null;
                var permissions = claimed.Split(',', StringSplitOptions.RemoveEmptyEntries);

                return ValueTask.FromResult<AuthenticatedCaller?>(
                    new AuthenticatedCaller(RequestPrincipal.Service(project, permissions), bound, project));
            }

            return ValueTask.FromResult<AuthenticatedCaller?>(null);
        }
    }

    private sealed class GrantAllEvaluator : IPermissionEvaluator
    {
        /// <inheritdoc />
        public ValueTask<PermissionDecision> EvaluateAsync(
            string permission,
            IRequestPrincipal principal,
            IRequestTenantContext context,
            CancellationToken cancellationToken) => ValueTask.FromResult(PermissionDecision.Granted);
    }
}
