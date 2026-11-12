using Microsoft.AspNetCore.Hosting;

namespace Evidgate.Api.Management;

/// <summary>
/// Adds the management endpoint to the addresses Kestrel listens on. An explicit <c>Listen*</c> call would make
/// Kestrel ignore <c>ASPNETCORE_URLS</c> and <c>ASPNETCORE_HTTP_PORTS</c> (the image sets the latter), so the
/// management address is appended to the URL list the host already resolved instead.
/// </summary>
public static class ManagementListener
{
    /// <summary>Validates the options and extends the server URLs when a management port is set.</summary>
    public static void Configure(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Configuration.GetSection(ManagementOptions.SectionName).Get<ManagementOptions>()
            ?? new ManagementOptions();
        options.Validate();

        if (options.ListenUrl is not { } managementUrl)
        {
            return;
        }

        var publicUrls = PublicUrls(builder.Configuration);
        foreach (var url in publicUrls)
        {
            if (Uri.TryCreate(url.Replace("+", "localhost", StringComparison.Ordinal).Replace("*", "localhost", StringComparison.Ordinal), UriKind.Absolute, out var uri)
                && uri.Port == options.ManagementPort)
            {
                throw new InvalidOperationException(
                    "EVIDGATE__API__MANAGEMENTPORT must differ from every public listener port");
            }
        }

        builder.WebHost.UseUrls([.. publicUrls, managementUrl]);
    }

    private static List<string> PublicUrls(ConfigurationManager configuration)
    {
        var urls = configuration[WebHostDefaults.ServerUrlsKey];
        if (!string.IsNullOrWhiteSpace(urls))
        {
            return [.. urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }

        var ports = configuration["HTTP_PORTS"];
        if (!string.IsNullOrWhiteSpace(ports))
        {
            return [.. ports.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(port => $"http://*:{port}")];
        }

        // Kestrel's own default when nothing is configured.
        return ["http://localhost:5000"];
    }
}
