using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Shouldly;
using Xunit;

namespace Parser.Tests;

/// <summary>
/// 06.3.6 AC4 ("no network") for the parse child: what can be proven in code, and what only a deployment can prove.
/// <para>
/// <b>The code half.</b> The child is a console process whose isolation is enforced twice: the source-level rule in
/// <c>Architecture.Tests</c> (<c>ParserChildIsolationRules.ForbiddenApis</c>, which denies the <c>System.Net</c>
/// namespace text, <c>HttpClient</c>, <c>Socket</c>, <c>TcpClient</c> and <c>Dns.</c> in the child's own files and in
/// the profile files it links, plus the <c>System.Net</c> package prefix in its lock file) and the built binary this
/// file reads: <see cref="Child_Assembly_References_No_Networking_Type"/> parses <c>Evidgate.Parser.dll</c>'s
/// metadata and asserts no <c>System.Net</c> assembly reference and no type in a <c>System.Net</c> namespace. The
/// source rule can be satisfied by a global using or a generated file; the metadata rule cannot.
/// </para>
/// <para>
/// <b>The environment half.</b> The runner hands the child a scrubbed allow-list and never an inherited copy
/// (<c>ChildEnvironment</c>, 06.3.6-T1.3), so no proxy, CA-bundle, resolver or .NET networking switch from the host
/// reaches it. Both the unit form (<see cref="Runner_Child_Environment_Holds_Only_The_Allow_List"/>) and the
/// end-to-end form (a spawned child dumping its own environment) are asserted here for the network variables
/// specifically.
/// </para>
/// <para>
/// <b>The runtime half, which is a deployment item.</b> A network namespace needs <c>CAP_SYS_ADMIN</c> or an
/// unprivileged user namespace; the S117 probe
/// (<see cref="SandboxLimitsProbeTests.NewNetworkNamespace_CannotReachALoopbackListenerOfTheParent"/>) measured
/// that neither is available to the container the child runs in, and <c>docs/security/parse-child-network-isolation.md</c>
/// records the same measurement for this image (receiver 10.2.1-T1.1). What is left is the deployment's egress
/// deny, and <see cref="A_Process_Started_Like_The_Child_Reaches_Loopback_But_Has_No_Egress_Path"/> proves it where
/// it can be proven — inside a container started with <c>--network none</c> — and skips with the reason on a host
/// that has an egress path of its own.
/// </para>
/// </summary>
public sealed class ChildNetworkTests(ITestOutputHelper output)
{
    /// <summary>
    /// Set to <c>1</c> by the gate stage that runs this class inside a container started with
    /// <c>--network none</c>: there the no-egress assertions are the point of the run, so a precondition that would
    /// skip the test fails it instead. A plain developer or CI test run leaves it unset and still skips.
    /// </summary>
    private const string RequireNoEgressVariable = "EVIDGATE_REQUIRE_NO_EGRESS";

    /// <summary>The public address the egress probe tries when it is allowed to: DNS-resolved, port 443.</summary>
    private const string EgressHost = "example.com";

    /// <summary>The same probe without DNS in the way, so a TCP-only failure is still observable.</summary>
    private const string EgressAddress = "1.1.1.1";

    /// <summary>The port both egress attempts use; 443 is what a parser would reach for if it could.</summary>
    private const int EgressPort = 443;

    /// <summary>
    /// The proxy variables set in the test process to prove the child's environment is scrubbed rather than that the
    /// host happens to configure none; both spellings, because the CLI tools the child's base image used to ship
    /// honour either.
    /// </summary>
    private static readonly string[] ProxyVariables = ["HTTPS_PROXY", "https_proxy"];

    /// <summary>
    /// AC4, code half: the child binary can form no outbound connection by construction. The compiled assembly is
    /// asserted rather than the sources, so a global using, a generated file or an analyzer-suppressed file cannot
    /// hide a <c>System.Net</c> use; the source-level rule stays in <c>Architecture.Tests</c>
    /// (<c>ParserChildIsolationRules.ForbiddenApis</c>, covered by <c>ParserChildIsolationTests</c>).
    /// </summary>
    [Fact]
    public void Child_Assembly_References_No_Networking_Type()
    {
        var assemblyPath = ChildRunner.ChildAssemblyPath();

        using var stream = File.OpenRead(assemblyPath);
        using var portable = new PEReader(stream);
        var reader = portable.GetMetadataReader();

        var referencedAssemblies = reader.AssemblyReferences
            .Select(handle => reader.GetAssemblyReference(handle))
            .Select(reference => reader.GetString(reference.Name))
            .ToArray();

        // A vacuous pass is the failure mode this guard exists for: the scan must have read the real child, whose
        // one project reference is the artifact contracts.
        referencedAssemblies.ShouldContain(
            "Evidgate.Modules.Evidence.Contracts",
            $"'{assemblyPath}' must be the built parse child");
        reader.TypeReferences.Count.ShouldBeGreaterThan(0, $"'{assemblyPath}' must carry type references");

        referencedAssemblies
            .Where(name => name.StartsWith("System.Net", StringComparison.Ordinal))
            .ShouldBeEmpty(
                $"the parse child must reference no System.Net assembly; '{assemblyPath}' references "
                + string.Join(", ", referencedAssemblies));

        var networkNamespaces = reader.TypeReferences
            .Select(handle => reader.GetTypeReference(handle))
            .Select(reference => reader.GetString(reference.Namespace))
            .Concat(reader.TypeDefinitions
                .Select(handle => reader.GetTypeDefinition(handle))
                .Select(definition => reader.GetString(definition.Namespace)))
            .Where(name => name.Equals("System.Net", StringComparison.Ordinal)
                || name.StartsWith("System.Net.", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        networkNamespaces.ShouldBeEmpty(
            "a type from System.Net (HttpClient, Socket, TcpClient, Dns, …) is what an outbound DNS or TCP "
            + $"connection needs, and the parse child must hold none of them; found: {string.Join(", ", networkNamespaces)}");

        output.WriteLine(
            $"child assembly: {Path.GetFileName(assemblyPath)}; assembly references: {referencedAssemblies.Length}; "
            + $"type references: {reader.TypeReferences.Count}; System.Net assembly references: 0; "
            + "System.Net type namespaces: 0");
    }

    /// <summary>
    /// AC4, environment half (unit): the allow-list is exactly the three documented variables, so no proxy, CA
    /// bundle, resolver override or <c>DOTNET_SYSTEM_NET*</c> switch can be inherited from the worker's own
    /// environment into the process that reads untrusted bytes.
    /// </summary>
    [Fact]
    public void Runner_Child_Environment_Holds_Only_The_Allow_List()
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "evgt-net-env-" + Guid.NewGuid().ToString("N"));

        var environment = ChildEnvironment.Build(
            ChildRunner.ChildAssemblyPath(),
            512L * 1024 * 1024,
            tempDirectory);

        string[] allowList =
        [
            ChildEnvironment.HeapLimitVariable,
            ChildEnvironment.InvariantGlobalizationVariable,
            ChildEnvironment.TempDirectoryVariable,
        ];

        var unexpected = environment.Keys
            .Where(key => !allowList.Contains(key, StringComparer.Ordinal))
            .ToArray();

        unexpected.ShouldBeEmpty("the child's environment must hold only the allow-list: " + string.Join(", ", unexpected));
        environment[ChildEnvironment.HeapLimitVariable].ShouldBe("0x20000000");
        environment[ChildEnvironment.TempDirectoryVariable].ShouldBe(tempDirectory);

        AssertNoNetworkEnvironment(environment);

        output.WriteLine(
            "allow-list: " + string.Join(", ", environment.Select(pair => pair.Key + "=" + pair.Value)));
    }

    /// <summary>
    /// AC4, environment half (end to end): what the child's own process actually receives when the runner starts
    /// it. A shell child dumps its environment exactly the way the T1.3 suite does, so this asserts the applied
    /// process environment and not only <see cref="ChildEnvironment.Build"/>'s return value.
    /// </summary>
    [Fact]
    public async Task Spawned_Child_Environment_Carries_No_Network_Configuration()
    {
        ParseRunnerHarness.RequirePosixShell("a scripted child that dumps its environment");

        // Make the assertion non-vacuous: variables that really are set in this process must not reach the child, so a
        // passing run proves the environment was scrubbed and not merely that the host happened to set no proxy. The
        // runner clears the inherited environment before applying its allow-list, so this is safe for the child and
        // the restore below keeps it invisible to the rest of the suite.
        var previous = ProxyVariables.ToDictionary(
            name => name,
            name => Environment.GetEnvironmentVariable(name),
            StringComparer.Ordinal);

        foreach (var name in ProxyVariables)
        {
            Environment.SetEnvironmentVariable(name, "http://127.0.0.1:9/");
        }

        try
        {
            foreach (var name in ProxyVariables)
            {
                Environment.GetEnvironmentVariable(name).ShouldBe("http://127.0.0.1:9/");
            }

            using var harness = new ParseRunnerHarness();
            using var runner = harness.Script(
                """
                /usr/bin/env > ../env.txt
                printf '{"done":true,"count":0}\n'
                """);

            var result = await ParseRunnerHarness.RunAsync(
                runner,
                ParseRunnerProfile.Xml,
                maxBytes: 4096,
                input: [],
                cancellationToken: TestContext.Current.CancellationToken);

            result.ShouldBeOfType<ParseRunSucceeded>();

            var environment = harness.ReadEnvironmentDump("env.txt");

            environment[ChildEnvironment.HeapLimitVariable].ShouldBe("0x20000000");

            // The controlled variables were set in the parent and are absent from the child's own environment dump.
            AssertNoNetworkEnvironment(environment);

            output.WriteLine(
                "spawned child variables: " + string.Join(", ", environment.Keys.Order(StringComparer.Ordinal)));
        }
        finally
        {
            foreach (var (name, value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    /// <summary>
    /// AC4, runtime half: a process started through the runner — the child's executable path, its scrubbed
    /// environment, its working directory and its pipes — can still reach a loopback listener (the positive
    /// control: the connect itself works) but has no egress path for DNS or TCP.
    /// <para>
    /// This is only meaningful where the runtime really denies egress, so the test first asks the test process
    /// itself, bounded: a host with its own egress path skips with the reason and names the deployment item
    /// (10.2.1-T1.1), because "the container has no network" is not something a unit-test host can assert about
    /// itself. In the container the child ships in, started with <c>--network none</c>, the assertions are: loopback
    /// reached, DNS and TCP attempts failed, and the child's environment carries no network configuration.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_Process_Started_Like_The_Child_Reaches_Loopback_But_Has_No_Egress_Path()
    {
        ParseRunnerHarness.RequirePosixShell("a scripted child that attempts outbound connections");

        if (!OperatingSystem.IsLinux())
        {
            SkipOrFail(
                "the child's runtime is the Linux chiseled image (DECISIONS F14), and the no-egress property is a "
                + $"property of the container that runs it; this host is '{Environment.OSVersion.Platform}'. "
                + "Probe result: skipped, not passed.");
        }

        var bash = FindExecutable("/bin/bash", "/usr/bin/bash") ?? string.Empty;
        if (bash.Length == 0)
        {
            SkipOrFail("'bash' is not at /bin/bash or /usr/bin/bash, so the /dev/tcp connect probe cannot run");
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var (canConnect, dnsOutcome) = await ProbeParentEgressAsync(cancellation.Token);
        output.WriteLine($"parent egress check: tcp {EgressAddress}:{EgressPort} {(canConnect ? "connected" : "failed")}; {dnsOutcome}");

        if (canConnect)
        {
            SkipOrFail(
                $"this environment has an egress path (a TCP connect to {EgressAddress}:{EgressPort} succeeded), so a "
                + "successful connect from a spawned child would prove nothing about the deny. The no-network "
                + "guarantee is the deployment's egress deny (receiver 10.2.1-T1.1) and is asserted only in a "
                + "container started with --network none. Probe result: skipped, not passed.");
        }

        using var harness = new ParseRunnerHarness();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
            var egressPort = EgressPort.ToString(CultureInfo.InvariantCulture);
            var script = $$"""
                /usr/bin/env > ../env.txt
                ( exec 3<>/dev/tcp/127.0.0.1/{{port}} ) >/dev/null 2>&1 && echo loopback=ok >> ../probe.txt || echo loopback=fail >> ../probe.txt
                ( exec 3<>/dev/tcp/{{EgressAddress}}/{{egressPort}} ) >/dev/null 2>&1 && echo tcp=ok >> ../probe.txt || echo tcp=fail >> ../probe.txt
                ( exec 3<>/dev/tcp/{{EgressHost}}/{{egressPort}} ) >/dev/null 2>&1 && echo dns=ok >> ../probe.txt || echo dns=fail >> ../probe.txt
                printf '{"done":true,"count":0}\n'
                """;

            using var runner = harness.Spawn(new ChildCommand(bash, ["-c", script], string.Empty));

            var result = await ParseRunnerHarness.RunAsync(
                runner,
                ParseRunnerProfile.Xml,
                maxBytes: 4096,
                input: [],
                cancellationToken: cancellation.Token);

            result.ShouldBeOfType<ParseRunSucceeded>();

            var probe = harness.SideLines("probe.txt");
            output.WriteLine("probe: " + string.Join(", ", probe));

            // The positive control: the same connect primitive, from the same process, does reach a listener — so a
            // failing egress attempt is the environment's deny and not a broken script.
            probe.ShouldContain("loopback=ok");

            // The parent's own attempt above already failed, so a success here would be a contradiction worth
            // failing on rather than skipping: it would mean the child's environment reaches further than the
            // parent's.
            probe.ShouldContain("tcp=fail");
            probe.ShouldContain("dns=fail");

            AssertNoNetworkEnvironment(harness.ReadEnvironmentDump("env.txt"));
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Probes this process for an egress path, bounded to a few seconds. The <b>TCP connect is the only authoritative
    /// signal</b>: a connect needs a route out of this network namespace, while a name resolution can succeed
    /// through a caching or forwarding resolver that has one even when this process has none (Docker's embedded
    /// resolver forwards over the host's network whenever the container has any). So DNS is measured and returned as
    /// printed evidence, never as the verdict.
    /// </summary>
    /// <param name="cancellationToken">The bounded budget for both attempts.</param>
    /// <returns>Whether a TCP connect out succeeded, and a one-line description of the DNS attempt.</returns>
    private static async Task<(bool CanConnect, string DnsOutcome)> ProbeParentEgressAsync(
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));

        var dnsOutcome = "dns example.com: failed";
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(EgressHost, budget.Token);
            dnsOutcome = addresses.Length > 0
                ? "dns example.com: resolved (evidence only, not a verdict)"
                : "dns example.com: no address";
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            // A resolution that fails or times out is the expected outcome without egress; it decides nothing.
        }

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Parse(EgressAddress), EgressPort, budget.Token);

            return (true, dnsOutcome);
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException
            or ObjectDisposedException)
        {
            return (false, dnsOutcome);
        }
    }

    /// <summary>
    /// Asserts that no variable in <paramref name="environment"/> can point the process at a proxy, a CA bundle, a
    /// resolver or a .NET networking switch. Neither the allow-list nor the child's ability to reach a network
    /// depends on these being absent — the allow-list is the primary control — but a host that set one would make
    /// the "no network" statement ambiguous, so the absence is asserted rather than assumed.
    /// </summary>
    /// <param name="environment">The variables the child receives.</param>
    private static void AssertNoNetworkEnvironment(IReadOnlyDictionary<string, string> environment)
    {
        string[] forbiddenNames =
        [
            "HTTP_PROXY",
            "HTTPS_PROXY",
            "ALL_PROXY",
            "NO_PROXY",
            "http_proxy",
            "https_proxy",
            "all_proxy",
            "no_proxy",
            "SSL_CERT_FILE",
            "SSL_CERT_DIR",
            "SSLKEYLOGFILE",
            "CURL_CA_BUNDLE",
            "RES_OPTIONS",
            "LOCALDOMAIN",
            "HOSTALIASES",
            "LD_PRELOAD",
            "LD_LIBRARY_PATH",
        ];

        var present = forbiddenNames
            .Where(name => environment.ContainsKey(name))
            .ToArray();

        present.ShouldBeEmpty(
            "the child must inherit no proxy, CA-bundle, resolver or loader override: " + string.Join(", ", present));

        var networkingSwitches = environment.Keys
            .Where(key => key.Contains("PROXY", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("DOTNET_SYSTEM_NET", StringComparison.Ordinal))
            .Where(key => !key.Equals(ChildEnvironment.HeapLimitVariable, StringComparison.Ordinal)
                && !key.Equals(ChildEnvironment.TempDirectoryVariable, StringComparison.Ordinal)
                && !key.Equals(ChildEnvironment.InvariantGlobalizationVariable, StringComparison.Ordinal))
            .ToArray();

        networkingSwitches.ShouldBeEmpty(
            "no proxy or DOTNET_SYSTEM_NET* variable may reach the child: " + string.Join(", ", networkingSwitches));
    }

    /// <summary>Skips the test, or fails it when the gate stage that needs it to run has set the variable.</summary>
    /// <param name="reason">Why the test cannot assert the property here.</param>
    [DoesNotReturn]
    private static void SkipOrFail(string reason)
    {
        if (Environment.GetEnvironmentVariable(RequireNoEgressVariable) == "1")
        {
            Assert.Fail($"{RequireNoEgressVariable}=1 requires this test to run, but it would skip: {reason}");
        }

        Assert.Skip(reason);
    }

    /// <summary>The first existing path of the candidates, or <see langword="null"/> when none exists.</summary>
    /// <param name="candidates">Absolute paths to check.</param>
    /// <returns>The first path that exists.</returns>
    private static string? FindExecutable(params string[] candidates) =>
        candidates.FirstOrDefault(File.Exists);
}
