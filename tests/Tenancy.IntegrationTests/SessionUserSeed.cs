using System.Collections.Concurrent;
using Npgsql;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The <c>public."user"</c> row a signed-in caller implies, seeded for the fabricated callers of this suite.
/// <para>
/// 02.3.1-T2 / migration 0044 adds and validates <c>membership_user_fk</c>
/// (<c>membership.user_id → public."user"</c>). The one production path that writes a membership
/// outside the login upsert is <c>POST /v1/orgs</c>: it founds the caller's membership in the new
/// organization, and in production that caller already has a <c>user</c> row, because the login
/// upsert of 0042 wrote it before the request arrived. The suite's <c>X-Test-User</c> header
/// (<see cref="TenancyApiFactory"/>) names a user id that never logged in, so without this row the
/// founder insert fails 23503 and every org-creating test fails in its arrange step. The stand-in
/// therefore seeds the same row first, through the owner seam that arranges every other row of the
/// suite (01.6.1-T2) — called from the host's own startup filter, so no test has to remember it.
/// </para>
/// <para>
/// The write runs on its own owner connection rather than the app role's data source: 0041 gives the
/// runtime roles nothing at all on <c>user</c>, and a separate connection keeps the insert out of the
/// statement log the P3 statement-budget test reads. Concurrent first requests of one caller share a
/// single in-flight seed, so a racing request never runs before the row exists, and the completed seed
/// stays in the dictionary for the life of the process: the row is written once per (database, caller),
/// so no later request of that caller repeats the owner insert. Only a faulted seed is dropped, which
/// lets the next request retry and report its own error.
/// </para>
/// </summary>
internal static class SessionUserSeed
{
    /// <summary>The owner connection string of each database the fixture minted, by database name.</summary>
    private static readonly ConcurrentDictionary<string, string> OwnerConnections = new(StringComparer.Ordinal);

    /// <summary>The seed in flight (or done) per (database, user), so one caller is written once.</summary>
    private static readonly ConcurrentDictionary<(string Database, Guid User), Task> Seeds = new();

    /// <summary>Registers the owner connection of a freshly minted database.</summary>
    /// <param name="database">The database name.</param>
    /// <param name="ownerConnectionString">Its owner-role connection string.</param>
    internal static void Register(string database, string ownerConnectionString) =>
        OwnerConnections[database] = ownerConnectionString;

    /// <summary>Seeds the <c>user</c> row of <paramref name="user"/> once per database.</summary>
    /// <param name="appConnectionString">The host's app connection string, which names the database.</param>
    /// <param name="user">The caller's user id.</param>
    /// <param name="email">The caller's address, or null for a synthetic one.</param>
    /// <param name="cancellationToken">The request's token: this runs inside the request pipeline.</param>
    /// <exception cref="InvalidOperationException">The database was not minted by the suite's fixture.</exception>
    internal static async Task EnsureAsync(
        string appConnectionString,
        Guid user,
        string? email,
        CancellationToken cancellationToken)
    {
        var database = new NpgsqlConnectionStringBuilder(appConnectionString).Database
            ?? throw new InvalidOperationException("the suite's app connection string names no database");

        if (!OwnerConnections.TryGetValue(database, out var ownerConnectionString))
        {
            throw new InvalidOperationException(
                $"no owner connection is registered for '{database}': the database must come from "
                + $"{nameof(PostgresTenancyFixture)}, the one place that mints (and registers) it");
        }

        var key = (database, user);
        while (true)
        {
            if (Seeds.TryGetValue(key, out var inFlight))
            {
                // The row is (or will be) there: a second insert would only be a no-op, and waiting means a
                // racing request cannot reach the founder insert before the row is committed.
                await inFlight.ConfigureAwait(false);
                return;
            }

            var seed = SeedAsync(ownerConnectionString, user, email, cancellationToken);
            if (!Seeds.TryAdd(key, seed))
            {
                continue;
            }

            try
            {
                await seed.ConfigureAwait(false);
            }
            catch
            {
                // A faulted seed must not look done: the entry goes so the next request retries and reports
                // its own error instead of this one. A completed seed stays, because the row it wrote is
                // what every later request of the caller relies on.
                Seeds.TryRemove(key, out _);
                throw;
            }

            return;
        }
    }

    /// <summary>Writes the <c>user</c> row on the owner connection.</summary>
    private static async Task SeedAsync(
        string ownerConnectionString,
        Guid user,
        string? email,
        CancellationToken cancellationToken)
    {
        // `user.email` is NOT NULL with a 3..320 character bound (0041): a header address outside it gets
        // the synthetic form, exactly like the fixture rows the identity suites seed.
        var address = email is { Length: >= 3 and <= 320 } ? email : $"user-{user:N}@example.test";

        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            """insert into public."user" (id, email) values (@id, @email) on conflict (id) do nothing""",
            connection);
        command.Parameters.AddWithValue("id", user);
        command.Parameters.AddWithValue("email", address);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
