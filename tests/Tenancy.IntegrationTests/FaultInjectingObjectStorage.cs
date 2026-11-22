using System.Collections.Concurrent;
using System.Net;
using Evidgate.BuildingBlocks.ObjectStorage;
using Microsoft.Extensions.DependencyInjection;

namespace Tenancy.IntegrationTests;

/// <summary>
/// A test <see cref="IObjectStorage"/> over the real adapter (06.3.1-T1 review F-P2/F-C4): it counts the
/// prefix reads the completion issues and, per case, answers one read with fault bytes (the bytes swapped
/// between the staging read and the copy) or a <c>null</c> (the object vanished). Registered through
/// <c>ConfigureTestServices</c>, so the rest of the pipeline stays production; the real adapter is built by the
/// container and attached to the decorator on first resolution.
/// </summary>
internal sealed class FaultInjectingObjectStorage : IObjectStorage, IDisposable
{
    private readonly ConcurrentDictionary<string, int> _readsPerKey = new(StringComparer.Ordinal);
    private IObjectStorage? _inner;
    private int _prefixReads;

    /// <summary>Every <see cref="ReadPrefixAsync"/> call made since the host started (staging and final).</summary>
    public int PrefixReads => Volatile.Read(ref _prefixReads);

    /// <summary>Keys whose prefix read answers <c>null</c> (the object vanished), regardless of the store.</summary>
    public HashSet<string> AbsentPrefixKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Keys whose conditional prefix read answers <c>412 PreconditionFailed</c> (the object no longer carries the
    /// ETag the preceding <c>HEAD</c> reported), regardless of the store (06.3.1-T2.2).
    /// </summary>
    public HashSet<string> PreconditionFailedKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>The ETag the last <c>HEAD</c> of each key reported, so a test can prove a read was pinned to it.</summary>
    public ConcurrentDictionary<string, string?> HeadETags { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Every prefix read in order, with the <c>If-Match</c> the caller passed: the pinning proof (06.3.1-T2.2).
    /// The entry is recorded before any short-circuit, so an injected <c>null</c> or <c>412</c> read still shows
    /// the ETag the verifier passed.
    /// </summary>
    public ConcurrentQueue<(string Key, string? IfMatch)> ReadCalls { get; } = new();

    /// <summary>
    /// Replaces the prefix response for a key's nth read (1-based) when it returns a non-null array; returning
    /// <c>null</c> delegates to the store. Set after the artifact is minted so the final key is known.
    /// </summary>
    public Func<string, int, byte[]?>? PrefixOverride { get; set; }

    /// <summary>
    /// Replaces the host's <see cref="IObjectStorage"/> registration with <paramref name="decorator"/> over the
    /// real adapter the container builds, so the decorator sees every storage call the completion makes.
    /// </summary>
    /// <param name="services">The host's service collection, inside <c>ConfigureTestServices</c>.</param>
    /// <param name="decorator">The decorator instance the test holds to configure and inspect.</param>
    public static void Decorate(IServiceCollection services, FaultInjectingObjectStorage decorator)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(decorator);

        var descriptor = services.Last(service => service.ServiceType == typeof(IObjectStorage));
        services.Remove(descriptor);
        services.AddSingleton<IObjectStorage>(provider =>
        {
            decorator.Attach((IObjectStorage)ActivatorUtilities.CreateInstance(
                provider, descriptor.ImplementationType
                    ?? throw new InvalidOperationException("the IObjectStorage registration has no implementation type")));

            return decorator;
        });
    }

    /// <inheritdoc />
    public PresignedPut PresignPut(PresignPutRequest request) => Inner().PresignPut(request);

    public PresignedGet PresignGet(string objectKey) => Inner().PresignGet(objectKey);

    /// <inheritdoc />
    public async Task<ObjectHead?> HeadAsync(string objectKey, CancellationToken cancellationToken)
    {
        var head = await Inner().HeadAsync(objectKey, cancellationToken).ConfigureAwait(false);
        if (head is not null)
        {
            HeadETags[objectKey] = head.ETag;
        }

        return head;
    }

    /// <inheritdoc />
    public Task<byte[]?> ReadPrefixAsync(
        string objectKey, int maxBytes, CancellationToken cancellationToken, string? ifMatch = null)
    {
        Interlocked.Increment(ref _prefixReads);
        var index = _readsPerKey.AddOrUpdate(objectKey, 1, (_, previous) => previous + 1);
        ReadCalls.Enqueue((objectKey, ifMatch));

        if (AbsentPrefixKeys.Contains(objectKey))
        {
            return Task.FromResult<byte[]?>(null);
        }

        if (PreconditionFailedKeys.Contains(objectKey))
        {
            throw new ObjectStorageException("read-prefix", "PreconditionFailed", HttpStatusCode.PreconditionFailed);
        }

        if (PrefixOverride?.Invoke(objectKey, index) is { } replacement)
        {
            return Task.FromResult<byte[]?>(replacement);
        }

        return Inner().ReadPrefixAsync(objectKey, maxBytes, cancellationToken, ifMatch);
    }

    /// <inheritdoc />
    public Task<byte[]?> GetAsync(string objectKey, long maxBytes, CancellationToken cancellationToken) =>
        Inner().GetAsync(objectKey, maxBytes, cancellationToken);

    /// <inheritdoc />
    public Task CopyAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken) =>
        Inner().CopyAsync(sourceKey, destinationKey, cancellationToken);

    /// <inheritdoc />
    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) =>
        Inner().DeleteAsync(objectKey, cancellationToken);

    /// <summary>Disposes the real adapter the decorator built (the container disposes this singleton).</summary>
    public void Dispose() => (_inner as IDisposable)?.Dispose();

    /// <summary>Attaches the real adapter the decorator delegates to (called once by the DI factory).</summary>
    private void Attach(IObjectStorage inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    private IObjectStorage Inner() =>
        _inner ?? throw new InvalidOperationException("the decorator was used before its inner adapter was attached");
}
