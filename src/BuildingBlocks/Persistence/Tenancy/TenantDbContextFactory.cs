using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.BuildingBlocks.Persistence.Tenancy;

/// <summary>
/// The EF Core half of the data-access path (01.4.2-T1.3): every context is built
/// on the scope's connection, joined to the scope's transaction with
/// <c>Database.UseTransaction</c> — the same transaction Dapper reads in — and
/// carries the tenant interceptor plus the module configuration assemblies.
/// No lazy loading is configured anywhere on this path (00 §5.5 C4).
/// </summary>
internal sealed class TenantDbContextFactory<TContext> : ITenantDbContextFactory<TContext>
    where TContext : DbContext
{
    private static readonly Func<DbContextOptions<TContext>, TContext> Construct = CompileConstructor();

    private readonly ITenantScopeAccessor _scopes;
    private readonly TenantCommandInterceptor _interceptor;
    private readonly Assembly[] _configurationAssemblies;

    internal TenantDbContextFactory(
        ITenantScopeAccessor scopes,
        TenantCommandInterceptor interceptor,
        Assembly[] configurationAssemblies)
    {
        _scopes = scopes;
        _interceptor = interceptor;
        _configurationAssemblies = configurationAssemblies;
    }

    /// <inheritdoc />
    public TContext Create()
    {
        var scope = _scopes.Require();

        var options = new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(scope.Connection)
            .AddInterceptors(_interceptor)
            .UseTenantModel(_configurationAssemblies)
            .Options;

        var context = Construct(options);

        // C3: the connection is EF's and Dapper's; the transaction is the scope's,
        // so a write here and a read there see each other before the commit.
        context.Database.UseTransaction(scope.Transaction);

        return context;
    }

    /// <summary>
    /// Builds the constructor call once, so a context per request does not pay
    /// reflection on every creation. A module context must take the options in its
    /// constructor, as <see cref="EvidgateDbContext"/> documents.
    /// </summary>
    private static Func<DbContextOptions<TContext>, TContext> CompileConstructor()
    {
        var options = Expression.Parameter(typeof(DbContextOptions<TContext>), "options");

        var constructor = typeof(TContext).GetConstructor([typeof(DbContextOptions<TContext>)])
            ?? typeof(TContext).GetConstructor([typeof(DbContextOptions)])
            ?? throw new InvalidOperationException(
                $"'{typeof(TContext).FullName}' must declare a public constructor taking "
                + $"DbContextOptions<{typeof(TContext).Name}> and pass it to {nameof(EvidgateDbContext)}.");

        return Expression
            .Lambda<Func<DbContextOptions<TContext>, TContext>>(Expression.New(constructor, options), options)
            .Compile();
    }
}
