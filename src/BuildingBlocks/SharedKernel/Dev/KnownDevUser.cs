namespace Evidgate.BuildingBlocks.SharedKernel.Dev;

/// <summary>
/// The one synthetic account <c>seed-dev</c> writes and the development sign-in acts as: its id is a natural
/// key of the idempotent seed, so both sides must name the same value and neither may spell it twice.
/// </summary>
/// <remarks>
/// The seed (<c>Evidgate.Migrator</c>) owns the row; the Identity module's development-only session route owns
/// the sign-in. They live in different projects and neither references the other, so the id is declared once
/// here — beside <see cref="Secrets.SecretNames.DevIngestToken"/>, the platform's other development-only
/// constant — and both read it.
/// </remarks>
public static class KnownDevUser
{
    /// <summary>The seeded dev user's <c>public."user"</c> id, stable across seed reruns.</summary>
    public static readonly Guid UserId = Guid.Parse("0198a000-0000-7000-8000-00000000d003");
}
