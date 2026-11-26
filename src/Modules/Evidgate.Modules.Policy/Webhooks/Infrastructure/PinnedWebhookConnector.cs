using System.Net;
using System.Net.Sockets;
using Evidgate.Modules.Policy.Webhooks.Application;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Policy.Webhooks.Infrastructure;

/// <summary>
/// Opens the TCP stream of one delivery. The handler hands it only addresses the URL guard accepted a moment earlier,
/// so the HTTP stack never resolves the host itself and a DNS answer that changed in between cannot redirect the call.
/// </summary>
internal interface IWebhookConnector
{
    /// <summary>Connects to the first reachable address of <paramref name="addresses"/>.</summary>
    /// <param name="addresses">Addresses the guard validated for this attempt.</param>
    /// <param name="port">The port of the registered URL.</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    ValueTask<Stream> ConnectAsync(IReadOnlyList<IPAddress> addresses, int port, CancellationToken cancellationToken);
}

/// <summary>The production connector: a plain socket to a validated address, bounded by the connect timeout.</summary>
internal sealed class PinnedWebhookConnector(IOptions<WebhookDeliveryOptions> options) : IWebhookConnector
{
    /// <inheritdoc />
    public async ValueTask<Stream> ConnectAsync(
        IReadOnlyList<IPAddress> addresses,
        int port,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (addresses.Count == 0)
        {
            throw new InvalidOperationException("the delivery has no validated address to connect to");
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(options.Value.ConnectTimeout);

        SocketException? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), limit.Token).ConfigureAwait(false);

                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException failure)
            {
                socket.Dispose();
                last = failure;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw last!;
    }
}
