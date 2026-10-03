using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Evidgate.Api.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
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

    private TenancyApiFactory(string connectionString, bool realEvaluator, bool realResolver = false)
    {
        _connectionString = connectionString;
        _realEvaluator = realEvaluator;
        _realResolver = realResolver;
    }

    /// <summary>Every command Npgsql executed for this host, whitespace-normalised, in order.</summary>
    public StatementLog Statements { get; } = new();

    /// <summary>A host whose evaluator grants every permission (the rule under test is elsewhere).</summary>
    public static TenancyApiFactory GrantAll(string appConnectionString) => new(appConnectionString, realEvaluator: false);

    /// <summary>A host with the module's real evaluator (membership rows and token claims decide).</summary>
    public static TenancyApiFactory RealEvaluator(string appConnectionString) => new(appConnectionString, realEvaluator: true);

    /// <summary>The request headers of one caller.</summary>
    /// <summary>
    /// The real tenant resolution of 02.6.2: no test middleware publishes a tenant. A
    /// header authenticator stands in for the session/token authenticators of 02.3.x/02.4.x,
    /// so membership, the project scope and the service claims all go through the
    /// production <c>TenantResolutionFilter</c> and the real evaluator.
    /// </summary>
    public static TenancyApiFactory RealResolver(string appConnectionString) =>
        new(appConnectionString, realEvaluator: true, realResolver: true);

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
        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Database:ConnectionString"] = _connectionString,
                ["EVIDGATE:Identity:CustomRolesEnabled"] = "false",
            }));

        builder.ConfigureTestServices(services =>
        {
            if (_realResolver)
            {
                services.AddSingleton<ICallerAuthenticator, HeaderAuthenticator>();
            }
            else
            {
                services.AddSingleton<IStartupFilter>(new RequestContextStartupFilter());
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
        });
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

    /// <summary>Grants every permission; used where authorization is not the rule under test.</summary>
    /// <summary>The stand-in credential authenticator: headers become an <see cref="AuthenticatedCaller"/>.</summary>
    private sealed class HeaderAuthenticator : ICallerAuthenticator
    {
        public ValueTask<AuthenticatedCaller?> AuthenticateAsync(HttpContext context, CancellationToken cancellationToken)
        {
            var headers = context.Request.Headers;

            if (Guid.TryParse(headers[UserHeader], out var user))
            {
                var email = headers[EmailHeader].ToString();

                return ValueTask.FromResult<AuthenticatedCaller?>(new AuthenticatedCaller(
                    RequestPrincipal.Member(user, string.IsNullOrEmpty(email) ? null : email)));
            }

            if (headers.TryGetValue(ServicePermissionsHeader, out var claimed))
            {
                Guid? bound = Guid.TryParse(headers[ServiceOrgHeader], out var org) ? org : null;
                Guid? project = Guid.TryParse(headers[ServiceScopeHeader], out var p) ? p : null;
                var permissions = claimed.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries);

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

/// <summary>
/// Npgsql's <c>Executing command: {CommandText}</c> log line, captured once per command
/// before it is sent; transaction control is dropped so the log is the work itself.
/// </summary>
internal sealed partial class StatementLog : ILoggerFactory, ILogger
{
    private const string ExecutingCommandPrefix = "Executing command: ";

    private readonly ConcurrentQueue<string> _statements = new();

    /// <summary>Everything captured since the last <see cref="Clear"/>.</summary>
    public IReadOnlyList<string> Snapshot() => [.. _statements];

    /// <summary>Forgets every captured statement.</summary>
    public void Clear() => _statements.Clear();

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => this;

    /// <inheritdoc />
    public void AddProvider(ILoggerProvider provider)
    {
        // One capture serves every category.
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to release.
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        var message = formatter(state, exception);
        if (message is null || !message.StartsWith(ExecutingCommandPrefix, StringComparison.Ordinal))
        {
            return;
        }

        var text = Whitespace().Replace(message[ExecutingCommandPrefix.Length..], " ").Trim();
        if (!TransactionControl().IsMatch(text))
        {
            _statements.Enqueue(text);
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("^(BEGIN|COMMIT|ROLLBACK|START TRANSACTION|SET TRANSACTION|SAVEPOINT|RELEASE)", RegexOptions.IgnoreCase)]
    private static partial Regex TransactionControl();
}
