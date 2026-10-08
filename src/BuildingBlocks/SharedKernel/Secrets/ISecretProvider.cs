namespace Evidgate.BuildingBlocks.SharedKernel.Secrets;

/// <summary>
/// The one seam every secret resolution goes through (10.1.1-T2, 00 §5.7 S3): a
/// connection string, a token pepper or a signing key is asked for by its
/// <see cref="SecretNames"/> name and is never read from <c>IConfiguration</c> or
/// <see cref="System.Environment"/> directly.
/// </summary>
/// <remarks>
/// V1 ships <see cref="EnvironmentSecretProvider"/> (environment injection). The
/// documented swap point for a managed secret store (O3) is the single DI
/// registration of this interface: a replacement implementation is registered in
/// its place and must have every value available synchronously at options
/// materialisation, because the options post-configurers resolve secrets while the
/// host builds its options — a managed store therefore prefetches at boot.
/// </remarks>
public interface ISecretProvider
{
    /// <summary>
    /// Resolves <paramref name="name"/>, one of <see cref="SecretNames.All"/>.
    /// Returns <see langword="null"/> when the secret is unset or empty/whitespace.
    /// </summary>
    /// <param name="name">The secret name, as declared by <see cref="SecretNames"/>.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is not a known secret name; the message names only the name.
    /// </exception>
    ValueTask<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default);
}
