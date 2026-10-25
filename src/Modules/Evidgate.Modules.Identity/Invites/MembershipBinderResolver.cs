using Npgsql;

namespace Evidgate.Modules.Identity.Invites;

/// <summary>One pending invite as the pre-tenant invite resolvers return it (T75): the org and the row.</summary>
/// <param name="OrgId">The tenant the invite belongs to.</param>
/// <param name="MembershipId">The <c>pending</c> membership row the invite names.</param>
internal readonly record struct InviteCandidate(Guid OrgId, Guid MembershipId);

/// <summary>
/// The pre-tenant invite lookups of the bind path (02.1.2-T2, T75): they run before any
/// tenant scope exists, on the app role, through the <c>evidgate_resolve</c> definer
/// functions of migration 0043 — never by reading <c>membership</c> directly, which RLS
/// would answer with nothing (00 §5.4.8, precedent <c>Infrastructure/Persistence/CiResolver.cs</c>
/// and <c>IMemberOrgResolver</c>).
/// </summary>
/// <remarks>
/// The functions return ids only: no address, no hash, no status. A lookup that matches
/// nothing and one that matched a row which then failed every check inside the tenant
/// scope are therefore the same answer to the caller (T75, no oracle).
/// </remarks>
internal interface IMembershipBinderResolver
{
    /// <summary>The pending invite behind a public secret prefix, or null.</summary>
    /// <param name="prefix">The 12-character prefix, already validated by <see cref="InviteTokenFormat.TryParse"/>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<InviteCandidate?> ResolveBySecretAsync(string prefix, CancellationToken cancellationToken);

    /// <summary>
    /// The pending invites whose address is <paramref name="email"/> in an org that opted
    /// in to verified-domain auto-bind (<c>invite_autobind_candidates</c>, 0043).
    /// </summary>
    /// <param name="email">The normalised, provider-verified address.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<IReadOnlyList<InviteCandidate>> AutoBindCandidatesAsync(string email, CancellationToken cancellationToken);
}

/// <summary>Npgsql implementation of <see cref="IMembershipBinderResolver"/> on the app-role data source.</summary>
/// <param name="dataSource">The app-role data source (never the owner's).</param>
internal sealed class NpgsqlMembershipBinderResolver(NpgsqlDataSource dataSource) : IMembershipBinderResolver
{
    /// <inheritdoc />
    public async ValueTask<InviteCandidate?> ResolveBySecretAsync(string prefix, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        await using var command = dataSource.CreateCommand(
            "select org_id, membership_id from evidgate_resolve.invite_by_secret(@prefix)");
        command.Parameters.AddWithValue("prefix", prefix);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new InviteCandidate(reader.GetGuid(0), reader.GetGuid(1))
            : null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<InviteCandidate>> AutoBindCandidatesAsync(
        string email,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(email);

        await using var command = dataSource.CreateCommand(
            "select org_id, membership_id from evidgate_resolve.invite_autobind_candidates(@email)");
        command.Parameters.AddWithValue("email", email);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var candidates = new List<InviteCandidate>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(new InviteCandidate(reader.GetGuid(0), reader.GetGuid(1)));
        }

        return candidates;
    }
}
