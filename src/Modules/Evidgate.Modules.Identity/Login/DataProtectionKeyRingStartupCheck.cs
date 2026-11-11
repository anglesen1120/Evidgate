using Evidgate.BuildingBlocks.Persistence.Isolation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Evidgate.Modules.Identity.Login;

/// <summary>
/// Loads (and, on a first start, creates) the shared key ring before the host serves traffic. The framework
/// reads the ring lazily, so without this a host whose database is unreachable would start and fail its first
/// cookie; with it the host refuses to start and never runs on an ephemeral ring.
/// </summary>
/// <remarks>
/// It rides the boot-verification switch (<c>EVIDGATE__ISOLATION__SELFCHECK=Disabled</c>) that already
/// exempts test hosts which deliberately have no database; a disabled host still fails closed per request,
/// because the repository is the only ring source.
/// </remarks>
internal sealed class DataProtectionKeyRingStartupCheck(
    IKeyManager keyManager,
    IOptions<IsolationSelfCheckOptions> options) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.SelfCheck == IsolationSelfCheckMode.Disabled)
        {
            return Task.CompletedTask;
        }

        // GetAllKeys reads the table (throws when it is unreachable); a fresh deployment has no key yet, so one is
        // created and stored here instead of on the first login. Two replicas starting together may each create
        // one: both rows are valid and every replica reads both.
        if (keyManager.GetAllKeys().Count == 0)
        {
            var now = DateTimeOffset.UtcNow;
            keyManager.CreateNewKey(now, now.AddDays(90));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
