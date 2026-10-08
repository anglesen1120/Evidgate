namespace Evidgate.BuildingBlocks.SharedKernel.Secrets;

/// <summary>
/// The V1 <see cref="ISecretProvider"/>: a secret is looked up by its
/// environment-variable name in the process environment, or in any name→value map
/// the host supplies (the DI registration passes the host configuration, so the
/// environment-variables source is the injection point and a test host that sets
/// the same keys in memory resolves identically).
/// </summary>
/// <remarks>
/// A value never reaches a log line, an exception message or <see cref="ToString"/>:
/// the lookup delegate is the only holder of the values and this type prints none of
/// them. The resolution is synchronous in practice — the returned
/// <see cref="ValueTask{TResult}"/> is already completed — which is what lets the
/// options post-configurers resolve secrets while the host builds its options.
/// </remarks>
public sealed class EnvironmentSecretProvider : ISecretProvider
{
    private readonly Func<string, string?> _lookup;

    /// <summary>Creates a provider over a name→value lookup (the environment-variable name is the key).</summary>
    /// <param name="lookup">Returns the raw value for a name, or <see langword="null"/> when unset.</param>
    public EnvironmentSecretProvider(Func<string, string?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        _lookup = lookup;
    }

    /// <inheritdoc />
    public ValueTask<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!SecretNames.All.Contains(name, StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{name}' is not a known secret name.", nameof(name));
        }

        var value = _lookup(name);

        return ValueTask.FromResult(string.IsNullOrWhiteSpace(value) ? null : value);
    }
}
