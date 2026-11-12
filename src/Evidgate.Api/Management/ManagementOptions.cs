using System.Net;

namespace Evidgate.Api.Management;

/// <summary>
/// The Api's management surface (<c>/metrics</c> and the detailed readiness report), bound from
/// <c>EVIDGATE__API__MANAGEMENT*</c>. It is served on its own listener so the public port can stay behind an
/// ingress that has no reason to know these paths; unset, the management routes are not served at all.
/// </summary>
public sealed class ManagementOptions
{
    /// <summary>Configuration section bound to <c>EVIDGATE__API__*</c>.</summary>
    public const string SectionName = "EVIDGATE:Api";

    /// <summary>Loopback: a deployment that scrapes from another host says so explicitly.</summary>
    public const string DefaultAddress = "127.0.0.1";

    /// <summary>TCP port of the management listener; unset turns the management surface off.</summary>
    public int? ManagementPort { get; init; }

    /// <summary>IP literal the management listener binds.</summary>
    public string ManagementAddress { get; init; } = DefaultAddress;

    /// <summary>The management URL Kestrel is asked to add, or null when the surface is off.</summary>
    public string? ListenUrl => ManagementPort is { } port
        ? $"http://{(IPAddress.Parse(ManagementAddress).AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ManagementAddress}]" : ManagementAddress)}:{port}"
        : null;

    /// <summary>Throws a message naming the key for a value the boot cannot use.</summary>
    public void Validate()
    {
        if (ManagementPort is < 1 or > 65535)
        {
            throw new InvalidOperationException("EVIDGATE__API__MANAGEMENTPORT must be a TCP port (1..65535)");
        }

        if (!IPAddress.TryParse(ManagementAddress, out _))
        {
            throw new InvalidOperationException("EVIDGATE__API__MANAGEMENTADDRESS must be an IP address");
        }
    }
}
