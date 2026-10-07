using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace Isolation.Tests.Api;

/// <summary>
/// The API isolation suite's world (02.9.1-T1.1): one digest-pinned PostgreSQL 18 container (or the
/// AC5 reused server), one database migrated to head by the real Migrator, the real host over it
/// (<see cref="IsolationApiFactory"/>) and the two-org <see cref="IsolationWorld"/>, built once
/// per collection.
/// </summary>
/// <remarks>
/// The database bootstrap is <see cref="PostgresIsolationFixture"/> itself (container, superuser
/// bootstrap, <c>Evidgate.Migrator</c>, app-role logins), owned by this fixture rather than shared
/// with <see cref="IsolationSuite"/>, so the world's rows never meet another suite's.
/// Nothing resets between test classes: every request of the suite is meant to be refused, and
/// <see cref="SnapshotOrgAsync"/> lets a test prove that no write happened.
/// </remarks>
public sealed class ApiIsolationFixture : IAsyncLifetime
{
    private readonly PostgresIsolationFixture _postgres = new();
    private string? _database;
    private IsolationApiFactory? _factory;
    private IsolationWorld? _world;
    private bool _disposed;

    /// <summary>The running host.</summary>
    public IsolationApiFactory Factory => _factory ?? throw NotStarted();

    /// <summary>The seeded two-org world.</summary>
    public IsolationWorld World => _world ?? throw NotStarted();

    /// <summary>The running host's root service provider.</summary>
    public IServiceProvider Services => Factory.Services;

    /// <summary>The <c>evidgate_owner</c> connection string (arranging and snapshots only, never an assertion path).</summary>
    public string OwnerConnectionString => _postgres.OwnerConnectionString(_database ?? throw NotStarted());

    /// <summary>The <c>evidgate_app</c> connection string the host runs on.</summary>
    public string AppConnectionString => _postgres.AppConnectionString(_database ?? throw NotStarted());

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        try
        {
            await _postgres.InitializeAsync().ConfigureAwait(false);
            _database = await _postgres.CreateDatabaseAsync().ConfigureAwait(false);
            _factory = new IsolationApiFactory(AppConnectionString);

            using var client = CreateClient();
            _world = await IsolationWorld.SeedAsync(client, _factory.Services, OwnerConnectionString, TestContext.Current.CancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_factory is not null)
        {
            await _factory.DisposeAsync().ConfigureAwait(false);
            _factory = null;
        }

        await _postgres.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// A client authenticated as <paramref name="user"/> (the member stand-in headers) naming
    /// <paramref name="orgId"/> in <c>X-Evidgate-Org</c>. Clients are owned by the factory and
    /// released with it; disposing one early is allowed.
    /// </summary>
    public HttpClient MemberClient(IsolationUser user, Guid orgId)
    {
        ArgumentNullException.ThrowIfNull(user);

        var client = CreateClient();
        client.DefaultRequestHeaders.Add(IsolationApiFactory.UserHeader, user.UserId.ToString());
        client.DefaultRequestHeaders.Add(IsolationApiFactory.EmailHeader, user.Email);
        client.DefaultRequestHeaders.Add(IsolationApiFactory.OrgHeader, orgId.ToString());
        return client;
    }

    /// <summary>A client with no credential at all.</summary>
    public HttpClient AnonymousClient() => CreateClient();

    /// <summary>A client presenting <paramref name="ciProducerJwt"/> as <c>Authorization: Bearer</c> (the real authenticator).</summary>
    public HttpClient ServiceClient(string ciProducerJwt)
    {
        ArgumentException.ThrowIfNullOrEmpty(ciProducerJwt);

        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ciProducerJwt);
        return client;
    }

    /// <summary>
    /// A fingerprint of every row <paramref name="orgId"/> owns, read through the owner connection:
    /// the org's own <c>organization</c> row, then each <c>public</c> table with an <c>org_id</c>
    /// column (discovered from <c>information_schema</c>, partitions folded into their parent, in
    /// name order) as one <c>table:rows:md5</c> line, where the md5 covers <c>row_to_json</c> of the
    /// org's rows ordered by <c>id</c>. Equal before and after a block of requests means none of
    /// them wrote to the org.
    /// </summary>
    public async Task<string> SnapshotOrgAsync(Guid orgId)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(OwnerConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var tables = new List<(string Name, bool HasId)>();
        await using (var discover = new NpgsqlCommand(
            """
            select c.table_name::text,
                   exists (select 1
                             from information_schema.columns i
                            where i.table_schema = c.table_schema
                              and i.table_name = c.table_name
                              and i.column_name = 'id')
              from information_schema.columns c
              join information_schema.tables t
                on t.table_schema = c.table_schema and t.table_name = c.table_name and t.table_type = 'BASE TABLE'
              join pg_catalog.pg_class pc
                on pc.oid = format('%I.%I', c.table_schema, c.table_name)::regclass
             where c.table_schema = 'public'
               and c.column_name = 'org_id'
               and not pc.relispartition
             order by c.table_name
            """,
            connection))
        {
            await using var reader = await discover.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tables.Add((reader.GetString(0), reader.GetBoolean(1)));
            }
        }

        var snapshot = new StringBuilder();
        snapshot.Append("organization:")
            .AppendLine(await FingerprintAsync(connection, "public.organization", "t.id", "t.id", orgId, cancellationToken).ConfigureAwait(false));

        foreach (var (name, hasId) in tables)
        {
            var table = "public.\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            snapshot.Append(name).Append(':')
                .AppendLine(await FingerprintAsync(connection, table, "t.org_id", hasId ? "t.id" : "row_to_json(t)::text", orgId, cancellationToken).ConfigureAwait(false));
        }

        return snapshot.ToString();
    }

    private static async Task<string> FingerprintAsync(
        NpgsqlConnection connection,
        string table,
        string orgColumn,
        string order,
        Guid orgId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"select count(*), coalesce(md5(string_agg(row_to_json(t)::text, chr(10) order by {order})), '-') from {table} t where {orgColumn} = @org",
            connection);
        command.Parameters.AddWithValue("org", orgId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        return string.Create(CultureInfo.InvariantCulture, $"{reader.GetInt64(0)}:{reader.GetString(1)}");
    }

    private HttpClient CreateClient() =>
        Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static InvalidOperationException NotStarted() =>
        new("the API isolation fixture has not been initialised");
}

/// <summary>Binds <see cref="ApiIsolationFixture"/> to the API isolation test classes (one world per collection).</summary>
[CollectionDefinition(Name)]
public sealed class ApiIsolationSuite : ICollectionFixture<ApiIsolationFixture>
{
    /// <summary>The collection name test classes reference.</summary>
    public const string Name = "api-isolation";
}
