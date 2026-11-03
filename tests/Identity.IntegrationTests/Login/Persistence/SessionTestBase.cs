using System.Security.Cryptography;
using System.Text;
using Evidgate.Modules.Identity.Sessions;
using Evidgate.Testing;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Login.Persistence;

/// <summary>
/// The shared arrangement of the persistence suites: the harness database (isolated by
/// tenant unless the suite asks for a private copy), the app-role and owner connections a
/// persistence test needs, the fixed pepper and the store factories.
/// </summary>
/// <remarks>
/// Every assertion about a credential goes through the owner connection on purpose: <c>evidgate_app</c> holds
/// no privilege on <c>user</c>, <c>user_identity</c> and <c>session</c> (0041), so the owner is the only role
/// that can look at what was actually stored. The write path under test is always the app role's.
/// </remarks>
[Trait("Category", "Docker")]
public abstract class SessionTestBase : TenantTest
{
    /// <summary>The pepper every store in these suites hashes with.</summary>
    protected static readonly byte[] Pepper = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();

    /// <summary>The pepper version stored beside each hash.</summary>
    protected const int PepperVersion = 7;

    protected SessionTestBase(DatabaseFixture fixture, DatabaseIsolation isolation = DatabaseIsolation.Shared)
        : base(fixture, isolation)
    {
    }

    /// <summary>The app-role data source the stores are built on (the production wiring's role).</summary>
    protected NpgsqlDataSource AppDataSource => Fixture.AppDataSource;

    /// <summary>The pepper as the base64 the options carry.</summary>
    protected static string PepperBase64 => Convert.ToBase64String(Pepper);

    /// <summary>A session-options snapshot with the given pepper and the default lifetimes.</summary>
    /// <param name="pepper">The base64 pepper, or null for a deployment that has not provisioned one.</param>
    private protected static IOptions<SessionOptions> SessionOptionsFor(string? pepper) =>
        Options.Create(new SessionOptions { Pepper = pepper, PepperVersion = PepperVersion });

    /// <summary>A <c>SessionStore</c> over the app-role data source, driven by <paramref name="clock"/>.</summary>
    /// <param name="clock">The clock the two expiries are computed from.</param>
    /// <param name="pepper">The base64 pepper, or null.</param>
    private protected SessionStore CreateSessionStore(TimeProvider clock, string? pepper = null) =>
        new(
            AppDataSource,
            SessionOptionsFor(pepper ?? PepperBase64),
            new SessionSecrets(SessionOptionsFor(pepper ?? PepperBase64)),
            clock);

    /// <summary>The HMAC the database row must carry for <paramref name="cookieValue"/>.</summary>
    /// <param name="cookieValue">The cookie value the store returned.</param>
    protected static byte[] ExpectedHmac(string cookieValue) =>
        HMACSHA256.HashData(Pepper, Encoding.ASCII.GetBytes(cookieValue));

    /// <summary>Seeds one active user through the owner seam.</summary>
    /// <param name="email">The address.</param>
    /// <param name="displayName">The display name, or null.</param>
    protected async Task<Guid> SeedUserAsync(string email, string? displayName = "Test User")
    {
        var id = Guid.CreateVersion7();
        await Fixture.SeedAsync(
            "insert into public.\"user\" (id, email, display_name) values (@id, @email, @display_name)",
            [("id", id), ("email", email), ("display_name", (object?)displayName ?? DBNull.Value)],
            Token);
        return id;
    }

    /// <summary>Runs one statement as the owner and returns the refusal it must raise.</summary>
    /// <param name="sql">The statement.</param>
    /// <param name="values">Its parameters.</param>
    protected async Task<PostgresException> OwnerRefusalAsync(
        string sql,
        params (string Name, object Value)[] values) =>
        await Should.ThrowAsync<PostgresException>(() => Fixture.SeedAsync(sql, values, Token));

    /// <summary>Runs one statement as the app role and returns the refusal it must raise.</summary>
    /// <param name="sql">The statement.</param>
    /// <param name="values">Its parameters.</param>
    protected async Task<PostgresException> AppRefusalAsync(
        string sql,
        params (string Name, object Value)[] values)
    {
        await using var command = AppDataSource.CreateCommand(sql);
        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Token));
    }

    /// <summary>One scalar on the owner connection (row arrangement and credential inspection).</summary>
    /// <param name="sql">The query.</param>
    /// <param name="values">Its parameters.</param>
    protected async Task<T> OwnerScalarAsync<T>(string sql, params (string Name, object Value)[] values)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(Token))!;
    }

    /// <summary>One row of scalars on the owner connection; every column is non-null for the caller.</summary>
    /// <param name="sql">The query.</param>
    /// <param name="values">Its parameters.</param>
    protected async Task<object?[]> OwnerRowAsync(string sql, params (string Name, object Value)[] values)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(Token);
        (await reader.ReadAsync(Token)).ShouldBeTrue();

        var row = new object?[reader.FieldCount];
        for (var index = 0; index < reader.FieldCount; index++)
        {
            row[index] = await reader.IsDBNullAsync(index, Token) ? null : reader.GetValue(index);
        }

        return row;
    }

    /// <summary>One statement as the owner, for arranging a row or the accepting side of a boundary.</summary>
    /// <param name="sql">The statement.</param>
    /// <param name="values">Its parameters.</param>
    protected Task OwnerExecuteAsync(string sql, params (string Name, object Value)[] values) =>
        Fixture.SeedAsync(sql, values, Token);
}
