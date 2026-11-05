using System.IO.Compression;
using System.Text;
using Evidgate.Modules.Evidence;
using Evidgate.Modules.Evidence.Application;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Parser.Tests;

/// <summary>
/// 06.3.6-T1.3 behaviour 11(a): the parent runner over the real built child. The child's own protocol, profile and
/// limits are proven by the T1.2/T1.1 suites and the child-side T1.3 tests; what these cases add is the parent's
/// half — that the production command reaches the child with the profile vector and the limit switches, that the
/// streamed records come back with the marker accepted, that a cap rejection travels as a streamed rejection rather
/// than a parent failure, and that the options, the command resolution and the module registration behave as the
/// rollout flag describes.
/// </summary>
public sealed class ParseRunnerTests
{
    private const string JunitFixture = "reports/junit/sample/input.xml";

    private const long OneMebibyte = 1024 * 1024;

    /// <summary>Dumps its own environment and argument vector beside the run directory, then streams the marker.</summary>
    private const string DumpScript = """
        /usr/bin/env > ../env.txt
        printf '%s\n' "$0" "$@" > ../args.txt
        printf '{"done":true,"count":0}\n'
        """;

    [Fact]
    public async Task Xml_Report_Streams_Through_The_Child_And_The_Marker_Closes_It()
    {
        using var harness = new ParseRunnerHarness();
        using var runner = harness.RealChild();
        var content = await File.ReadAllBytesAsync(RepoPaths.Fixtures(JunitFixture), TestContext.Current.CancellationToken);

        var result = await ParseRunnerHarness.RunAsync(runner, ParseRunnerProfile.Xml, OneMebibyte, content, cancellationToken: TestContext.Current.CancellationToken);

        var success = result.ShouldBeOfType<ParseRunSucceeded>();

        success.ChildExitCode.ShouldBe(0);
        success.OutputBytes.ShouldBeGreaterThan(0);
        success.Records.ShouldNotBeEmpty();

        // The marker counted every record before it, and the reader accepted it: the records are the document's.
        success.OutputRecords.ShouldBe(success.Records.Count);
        RecordsText(success).ShouldContain(text => text.Contains("\"testsuites\"", StringComparison.Ordinal));
        RecordsText(success).ShouldContain(text => text.Contains("Sample.Tests.Case_Fails()", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Json_And_Archive_Profiles_Run_Through_The_Same_Surface()
    {
        using var harness = new ParseRunnerHarness();
        using var runner = harness.RealChild();

        var json = await ParseRunnerHarness.RunAsync(
            runner,
            ParseRunnerProfile.Json,
            OneMebibyte,
            Encoding.UTF8.GetBytes("""{"a":1,"b":[1,2,3]}"""),
            cancellationToken: TestContext.Current.CancellationToken);

        var jsonSuccess = json.ShouldBeOfType<ParseRunSucceeded>();
        jsonSuccess.ChildExitCode.ShouldBe(0);
        jsonSuccess.Records.Count.ShouldBe(1);

        var archive = await ParseRunnerHarness.RunAsync(
            runner,
            ParseRunnerProfile.Archive,
            OneMebibyte,
            Zip("a.txt", "hello"),
            cancellationToken: TestContext.Current.CancellationToken);

        var archiveSuccess = archive.ShouldBeOfType<ParseRunSucceeded>();
        archiveSuccess.ChildExitCode.ShouldBe(0);
        archiveSuccess.Records.ShouldNotBeEmpty();
        RecordsText(archiveSuccess).ShouldContain(text => text.Contains("\"a.txt\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// 06.3.6-T1.3 behaviour 3, plan Notes :146: the child wraps a whole validated document in one record, so the
    /// reader's depth cap has to admit the profile's own depth plus that wrapper. A document nested 60 levels is
    /// well inside <c>ParsingLimits.MaxDepth</c> and must come back as a record, not as malformed output.
    /// </summary>
    [Fact]
    public async Task A_Deeply_Nested_Json_Document_Streams_As_One_Record()
    {
        using var harness = new ParseRunnerHarness();
        using var runner = harness.RealChild();
        var document = Encoding.UTF8.GetBytes(new string('[', 60) + new string(']', 60));

        var result = await ParseRunnerHarness.RunAsync(
            runner,
            ParseRunnerProfile.Json,
            OneMebibyte,
            document,
            cancellationToken: TestContext.Current.CancellationToken);

        var success = result.ShouldBeOfType<ParseRunSucceeded>();

        success.ChildExitCode.ShouldBe(0);
        success.Records.Count.ShouldBe(1);
        Encoding.UTF8.GetString(success.Records[0].Utf8Json.Span)
            .ShouldContain(new string('[', 60) + new string(']', 60));
    }

    /// <summary>
    /// 06.3.6-T1.3 behaviour 3, plan Notes :146: one JSON record carries the whole document, so the per-line cap is
    /// six times the input cap above the 1 MiB floor. A ~2 MiB document under a 4 MiB input cap — the shape the
    /// floor-only derivation refused — has to stream as one record.
    /// </summary>
    [Fact]
    public async Task A_Two_Mebibyte_Json_Record_Passes_A_Four_Mebibyte_Input_Cap()
    {
        using var harness = new ParseRunnerHarness();
        using var runner = harness.RealChild();

        // The document is 2 MiB to the byte: the prefix, the filler and the closing quote and brace.
        var document = Encoding.UTF8.GetBytes($"{{\"a\":\"{new string('x', (2 * 1024 * 1024) - 8)}\"}}");

        var result = await ParseRunnerHarness.RunAsync(
            runner,
            ParseRunnerProfile.Json,
            4 * OneMebibyte,
            document,
            cancellationToken: TestContext.Current.CancellationToken);

        var success = result.ShouldBeOfType<ParseRunSucceeded>();

        success.ChildExitCode.ShouldBe(0);
        success.Records.Count.ShouldBe(1);
        success.OutputBytes.ShouldBeGreaterThan(2 * OneMebibyte);
    }

    /// <summary>
    /// 06.3.6-T1.3 behaviour 2 / plan Notes S117 <em>Stdin overflow</em>: the child rejects the first byte past the
    /// cap, writes its rejection and the marker, and exits without draining stdin. The parent's stdin write ends in
    /// EPIPE, which is normal completion: the verdict is the marker, and the result is a successful run carrying one
    /// rejection record.
    /// </summary>
    [Fact]
    public async Task Input_Over_The_Cap_Ends_As_A_Streamed_Size_Rejection_Not_A_Parent_Failure()
    {
        using var harness = new ParseRunnerHarness();
        using var runner = harness.RealChild();

        var input = new byte[4 * 1024 * 1024];
        Array.Fill(input, (byte)'<');

        var result = await ParseRunnerHarness.RunAsync(runner, ParseRunnerProfile.Xml, maxBytes: 1024, input, cancellationToken: TestContext.Current.CancellationToken);

        var success = result.ShouldBeOfType<ParseRunSucceeded>();

        success.ChildExitCode.ShouldBe(0);
        success.Records.Count.ShouldBe(1);
        Encoding.UTF8.GetString(success.Records[0].Utf8Json.Span)
            .ShouldContain("payload_too_large", Case.Insensitive);
    }

    /// <summary>
    /// 06.3.6 AC2, behaviours 1, 6 and 7: the child's argument vector carries the profile, the cap, the declared
    /// media type and the two limit switches; its environment is exactly the allow-list, with the heap ceiling in the
    /// runtime's hexadecimal form, its own temp directory, and neither a <c>PATH</c> nor the host's <c>DOTNET_*</c>.
    /// </summary>
    [Fact]
    public async Task Child_Receives_The_Limit_Arguments_Heap_Limit_And_A_Scrubbed_Environment()
    {
        ParseRunnerHarness.RequirePosixShell("a scripted child that dumps its vector and environment");
        using var harness = new ParseRunnerHarness();
        using var runner = harness.Script(DumpScript);

        var result = await ParseRunnerHarness.RunAsync(
            runner,
            ParseRunnerProfile.Xml,
            maxBytes: 12345,
            input: [1, 2, 3],
            mediaType: "application/xml",
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBeOfType<ParseRunSucceeded>();

        harness.SideLines("args.txt").ShouldBe(
        [
            "--profile",
            "xml",
            "--max-bytes",
            "12345",
            "--max-cases",
            "150000",
            "--media-type",
            "application/xml",
            "--cpu-seconds",
            "20",
            "--max-file-bytes",
            "0",
        ]);

        var environment = harness.ReadEnvironmentDump("env.txt");

        // The allow-list and the values the runner computes: the heap ceiling in the runtime's hexadecimal form, the
        // globalization switch a scripted child needs (its command is a shell, so it declares no runtimeconfig) and
        // this run's private temp directory. The shell adds PWD, SHLVL and _ for itself before it runs `env`, and
        // which of those appear differs per shell, so the dump is never asserted key for key.
        environment[ChildEnvironment.HeapLimitVariable].ShouldBe("0x20000000");
        environment[ChildEnvironment.InvariantGlobalizationVariable].ShouldBe("1");
        environment[ChildEnvironment.TempDirectoryVariable].ShouldStartWith(harness.TempRoot);
        environment[ChildEnvironment.TempDirectoryVariable].ShouldNotBe(harness.TempRoot);

        // Nothing else travels: no DOTNET_* beyond the allow-list (DOTNET_STARTUP_HOOKS, the gc family), nothing
        // COMPlus_* or LD_*, and neither HOME nor PATH — the host has all of them, the child gets none.
        string[] allowList =
        [
            ChildEnvironment.HeapLimitVariable,
            ChildEnvironment.InvariantGlobalizationVariable,
            ChildEnvironment.TempDirectoryVariable,
        ];

        environment.Keys
            .Where(key => !allowList.Contains(key, StringComparer.Ordinal))
            .Where(key => key.StartsWith("DOTNET_", StringComparison.Ordinal)
                || key.StartsWith("COMPlus_", StringComparison.Ordinal)
                || key.StartsWith("LD_", StringComparison.Ordinal)
                || key is "HOME" or "PATH")
            .ShouldBeEmpty();

        environment.ShouldNotContainKey("HOME");
        environment.ShouldNotContainKey("PATH");
    }

    /// <summary>
    /// 06.3.6 AC2: the configured memory limit is what the child's runtime reads, in the runtime's own notation, and
    /// the two output caps travel with the run's input cap rather than with a host default.
    /// </summary>
    [Fact]
    public async Task Configured_Memory_Limit_Reaches_The_Child_As_Hexadecimal_Bytes()
    {
        ParseRunnerHarness.RequirePosixShell("a scripted child that dumps its environment");
        using var harness = new ParseRunnerHarness();
        using var runner = harness.Script(DumpScript, options => options.MemoryLimitBytes = 256L * 1024 * 1024);

        await ParseRunnerHarness.RunAsync(runner, ParseRunnerProfile.Xml, maxBytes: 4096, input: [], cancellationToken: TestContext.Current.CancellationToken);

        harness.ReadEnvironmentDump("env.txt")[ChildEnvironment.HeapLimitVariable].ShouldBe("0x10000000");

        // A non-positive value never widens a limit: the documented default applies instead, and the child still
        // sees the heap ceiling as an environment value rather than inheriting the host's.
        using var fallback = harness.Script(DumpScript, options => options.MemoryLimitBytes = -1);

        await ParseRunnerHarness.RunAsync(fallback, ParseRunnerProfile.Xml, maxBytes: 4096, input: [], cancellationToken: TestContext.Current.CancellationToken);

        harness.ReadEnvironmentDump("env.txt")[ChildEnvironment.HeapLimitVariable].ShouldBe("0x20000000");
    }

    [Fact]
    public async Task A_Missing_Child_Assembly_Is_A_SpawnFailed()
    {
        using var harness = new ParseRunnerHarness();
        using var runner = new ChildProcessParseRunner(
            harness.Options(options => options.ChildPath = Path.Combine(harness.TempRoot, "not-published", "Evidgate.Parser.dll")),
            ChildCommandLine.ForOptions);

        var result = await ParseRunnerHarness.RunAsync(
            runner,
            ParseRunnerProfile.Xml,
            1024,
            [1],
            cancellationToken: TestContext.Current.CancellationToken);

        // Nothing ran: the verdict is the spawn, not the exit code of a process that never parsed anything.
        var failure = result.ShouldBeOfType<ParseRunFailed>();
        failure.Failure.ShouldBe(ParseRunFailureKind.SpawnFailed);
        failure.ChildExitCode.ShouldBeNull();
        harness.LeftoverRunDirectories().ShouldBeEmpty();
    }

    [Fact]
    public void Output_Caps_Derive_From_The_Input_Cap_And_Explicit_Values_Win()
    {
        var options = new ParseRunnerOptions();

        // Byte cap: floor for a tiny input, the measured-amplification factor in the middle, the ceiling at any size.
        // The derivation reaches the ceiling at 256 MiB / 24 input bytes, and at the measured 18.6x amplification a
        // stream passes that ceiling from 256 MiB / 18.6 (~13.8 MiB) of input, so above that cutoff the run ends as a
        // breach by design, not as a wider buffer.
        options.EffectiveMaxOutputBytes(1024).ShouldBe(ParseRunnerOptions.OutputBytesFloor);
        options.EffectiveMaxOutputBytes(1024 * 1024).ShouldBe(24L * 1024 * 1024);
        options.EffectiveMaxOutputBytes(long.MaxValue).ShouldBe(ParseRunnerOptions.OutputBytesCeiling);

        var ceilingKnee = ParseRunnerOptions.OutputBytesCeiling / ParseRunnerOptions.OutputAmplificationFactor;

        // The switch is the integer quotient, one byte under the exact 256 MiB / 24 break-even, so the last input
        // that still multiplies is one byte below it; the difference stays well inside the factor's headroom.
        options.EffectiveMaxOutputBytes(ceilingKnee - 1)
            .ShouldBe(ParseRunnerOptions.OutputAmplificationFactor * (ceilingKnee - 1));
        options.EffectiveMaxOutputBytes(ceilingKnee).ShouldBe(ParseRunnerOptions.OutputBytesCeiling);

        // Record cap: floor, one record per eight input bytes, ceiling — and the ceiling sits below the ~10M records
        // the measured 16 MiB token-dense input produced, so that shape is refused instead of buffered.
        options.EffectiveMaxOutputRecords(1024).ShouldBe(ParseRunnerOptions.RecordCountFloor);
        options.EffectiveMaxOutputRecords(1024 * 1024).ShouldBe(1024 * 1024 / ParseRunnerOptions.InputBytesPerRecord);
        options.EffectiveMaxOutputRecords(16L * 1024 * 1024).ShouldBe(ParseRunnerOptions.RecordCountCeiling);
        options.EffectiveMaxOutputRecords(16L * 1024 * 1024).ShouldBeLessThan(10_000_000);
        options.EffectiveMaxOutputRecords(long.MaxValue).ShouldBe(ParseRunnerOptions.RecordCountCeiling);

        // Per-line cap: the 1 MiB floor for a small input, then six times the input cap, never above the byte cap. A
        // 2 MiB record line (a whole JSON document) has to pass a 4 MiB input cap, which the floor alone refused.
        options.EffectiveMaxRecordBytes(1024).ShouldBe((int)ParseRunnerOptions.RecordLineFloor);
        options.EffectiveMaxRecordBytes(4L * 1024 * 1024)
            .ShouldBe((int)(ParseRunnerOptions.RecordLineFactor * 4L * 1024 * 1024));
        options.EffectiveMaxRecordBytes(1024L * 1024 * 1024).ShouldBe((int)ParseRunnerOptions.OutputBytesCeiling);

        var configured = new ParseRunnerOptions { MaxOutputBytes = 4096, MaxOutputRecords = 7 };

        configured.EffectiveMaxOutputBytes(1024 * 1024).ShouldBe(4096);
        configured.EffectiveMaxOutputRecords(1024 * 1024).ShouldBe(7);
        configured.EffectiveMaxRecordBytes(1024 * 1024).ShouldBe(4096);
    }

    [Fact]
    public void Limits_Default_And_A_Non_Positive_Value_Never_Widens_One()
    {
        var defaults = new ParseRunnerOptions();

        defaults.OutOfProcess.ShouldBeFalse();
        defaults.ChildPath.ShouldBe(ParseRunnerOptions.DefaultChildPath);
        defaults.CpuSeconds.ShouldBe(20);
        defaults.WallClockSeconds.ShouldBe(30);
        defaults.MemoryLimitBytes.ShouldBe(512L * 1024 * 1024);
        defaults.MaxOutputBytes.ShouldBe(0);
        defaults.MaxOutputRecords.ShouldBe(0);
        defaults.TempRoot.ShouldBeEmpty();

        var unset = new ParseRunnerOptions
        {
            CpuSeconds = 0,
            WallClockSeconds = -5,
            MemoryLimitBytes = -1,
        };

        unset.EffectiveCpuSeconds.ShouldBe(ParseRunnerOptions.DefaultCpuSeconds);
        unset.EffectiveWallClock.ShouldBe(TimeSpan.FromSeconds(ParseRunnerOptions.DefaultWallClockSeconds));
        unset.EffectiveMemoryLimitBytes.ShouldBe(ParseRunnerOptions.DefaultMemoryLimitBytes);
    }

    [Fact]
    public void Globalization_Variable_Is_Set_Only_When_The_RuntimeConfig_Does_Not_Declare_It()
    {
        using var harness = new ParseRunnerHarness();

        var declaring = Path.Combine(harness.TempRoot, "declaring.dll");
        File.WriteAllText(declaring, string.Empty);
        File.WriteAllText(
            Path.ChangeExtension(declaring, ".runtimeconfig.json"),
            """{"runtimeOptions":{"tfm":"net10.0","configProperties":{"System.Globalization.Invariant":true}}}""");

        ChildEnvironment.DeclaresInvariantGlobalization(declaring).ShouldBeTrue();
        ChildEnvironment.Build(declaring, 1024, harness.TempRoot)
            .ShouldNotContainKey(ChildEnvironment.InvariantGlobalizationVariable);

        var denying = Path.Combine(harness.TempRoot, "denying.dll");
        File.WriteAllText(denying, string.Empty);
        File.WriteAllText(
            Path.ChangeExtension(denying, ".runtimeconfig.json"),
            """{"runtimeOptions":{"configProperties":{"System.Globalization.Invariant":false}}}""");

        ChildEnvironment.DeclaresInvariantGlobalization(denying).ShouldBeFalse();
        ChildEnvironment.Build(denying, 1024, harness.TempRoot)
            [ChildEnvironment.InvariantGlobalizationVariable].ShouldBe("1");

        // A command that is not a managed assembly has no runtimeconfig, so the variable is set.
        ChildEnvironment.Build(string.Empty, 1024, harness.TempRoot)
            [ChildEnvironment.InvariantGlobalizationVariable].ShouldBe("1");

        // The missing file itself is a "no": the safe direction is the variable.
        ChildEnvironment.DeclaresInvariantGlobalization(Path.Combine(harness.TempRoot, "absent.dll"))
            .ShouldBeFalse();
    }

    [Fact]
    public void Built_Child_Declares_Invariant_Globalization_So_The_Variable_Is_Redundant()
    {
        // Directory.Build.props puts Evidgate.Parser in the Worker group, so its generated runtimeconfig carries
        // System.Globalization.Invariant; the runner must not set a variable the runtimeconfig already states.
        ChildEnvironment.DeclaresInvariantGlobalization(ChildRunner.ChildAssemblyPath()).ShouldBeTrue();
    }

    [Fact]
    public void Dotnet_Host_And_Child_Path_Resolve_Without_An_Inherited_Environment()
    {
        // The host process itself is the dotnet binary when the entrypoint is /usr/bin/dotnet (the chiseled image).
        ChildCommandLine.ResolveDotnetHost("/opt/dotnet/dotnet").ShouldBe("/opt/dotnet/dotnet");
        ChildCommandLine.ResolveDotnetHost("/opt/dotnet/dotnet.exe").ShouldBe("/opt/dotnet/dotnet.exe");

        // Anything else falls back to PATH resolution of the documented command name.
        ChildCommandLine.ResolveDotnetHost("/usr/bin/testhost").ShouldBe("dotnet");
        ChildCommandLine.ResolveDotnetHost(null).ShouldBe("dotnet");

        ChildCommandLine.ResolveChildAssembly("parser/Evidgate.Parser.dll", "/app")
            .ShouldBe(Path.GetFullPath(Path.Combine("/app", "parser", "Evidgate.Parser.dll")));
        ChildCommandLine.ResolveChildAssembly("/opt/child/Evidgate.Parser.dll", "/app")
            .ShouldBe(Path.GetFullPath("/opt/child/Evidgate.Parser.dll"));

        // The production command is the host plus one leading argument: the absolute child assembly.
        var command = ChildCommandLine.ForOptions(new ParseRunnerOptions());

        command.Executable.ShouldNotBeNullOrEmpty();
        command.LeadingArguments.Count.ShouldBe(1);
        Path.IsPathRooted(command.LeadingArguments[0]).ShouldBeTrue();
        command.LeadingArguments[0].ShouldEndWith("Evidgate.Parser.dll", Case.Sensitive);
        command.ChildAssemblyPath.ShouldBe(command.LeadingArguments[0]);
    }

    /// <summary>
    /// 06.3.6 §11 / behaviour 1: the flag decides registration, not a runtime branch. Off means no
    /// <see cref="IParseRunner"/> and the in-process path untouched; on means the one out-of-process implementation.
    /// </summary>
    [Fact]
    public void Registration_Follows_The_OutOfProcess_Flag()
    {
        var offServices = ComposeServices(new Dictionary<string, string?>());
        using var off = offServices.BuildServiceProvider();

        off.GetService<IParseRunner>().ShouldBeNull();

        var bound = off.GetRequiredService<IOptions<ParseRunnerOptions>>().Value;
        bound.OutOfProcess.ShouldBeFalse();
        bound.ChildPath.ShouldBe(ParseRunnerOptions.DefaultChildPath);
        bound.CpuSeconds.ShouldBe(ParseRunnerOptions.DefaultCpuSeconds);

        var onServices = ComposeServices(new Dictionary<string, string?>
        {
            [$"{ParseRunnerOptions.SectionName}:{nameof(ParseRunnerOptions.OutOfProcess)}"] = "true",
            [$"{ParseRunnerOptions.SectionName}:{nameof(ParseRunnerOptions.CpuSeconds)}"] = "11",
            [$"{ParseRunnerOptions.SectionName}:{nameof(ParseRunnerOptions.ChildPath)}"] = "/srv/parser/Evidgate.Parser.dll",
        });
        using var on = onServices.BuildServiceProvider();

        on.GetRequiredService<IParseRunner>().ShouldBeOfType<ChildProcessParseRunner>();

        // 06.3.6-T1.4: the `artifact_parse` task handler travels with the runner, so the consumer claims the type
        // exactly where the runner exists. Off means neither, and an `artifact_parse` row stays queued (the
        // in-process rollback of 06.3.6 §11).
        // The handler is asserted by its own registration, not by enumerating ITaskHandlerInvoker: resolving that set
        // materialises every module handler, and this composition registers no secret provider for the object-storage
        // options they need, so the enumeration would fail for reasons unrelated to this flag. The handler's
        // TaskType == EvidenceTaskTypes.Parse link is asserted where the handler is exercised
        // (Tenancy.IntegrationTests/EvidenceParseTaskTests).
        RegistersParseHandler(offServices).ShouldBeFalse("the flag is off: nothing claims an artifact_parse row");
        RegistersParseHandler(onServices).ShouldBeTrue("the handler is registered with the runner it needs");

        var configured = on.GetRequiredService<IOptions<ParseRunnerOptions>>().Value;
        configured.OutOfProcess.ShouldBeTrue();
        configured.CpuSeconds.ShouldBe(11);
        configured.ChildPath.ShouldBe("/srv/parser/Evidgate.Parser.dll");
    }

    /// <summary>
    /// 06.3.6-T1.4 / the security review's process-spawn finding: the Api composes the module and never
    /// <c>AddParseRunner</c>, so with the flag ON it still cannot resolve the runner and cannot claim the task type —
    /// a process-spawn capability never reaches the request host.
    /// </summary>
    [Fact]
    public void Module_Alone_Never_Exposes_The_Runner_Or_The_Parse_Handler()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{ParseRunnerOptions.SectionName}:{nameof(ParseRunnerOptions.OutOfProcess)}"] = "true",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddEvidenceModule(configuration);

        RegistersParseHandler(services).ShouldBeFalse("the parse handler is Worker-only, flag-on");

        using var provider = services.BuildServiceProvider();

        provider.GetService<IParseRunner>().ShouldBeNull(
            "the Api must never resolve a process-spawn capability");
    }

    /// <summary>
    /// Whether a composition registered the <c>artifact_parse</c> task handler. Read from the descriptors, before
    /// anything is built: <c>AddTaskHandler</c> registers the handler by its own type, and resolving the module's
    /// handlers here would materialise ones that need a secret provider this composition never registers — a failure
    /// that would say nothing about the parse capability. The handler's own
    /// <c>TaskType == EvidenceTaskTypes.Parse</c> link is asserted where the handler runs
    /// (<c>Tenancy.IntegrationTests/EvidenceParseTaskTests</c>).
    /// </summary>
    /// <param name="services">The collection a composition registered into.</param>
    /// <returns><see langword="true"/> when the parse handler is registered.</returns>
    private static bool RegistersParseHandler(IServiceCollection services) =>
        services.Any(descriptor => descriptor.ServiceType == typeof(ParseArtifactTaskHandler));

    /// <summary>
    /// The Worker's composition of the module and the runner seam, returned as the collection so a test can read what
    /// it registered without constructing anything.
    /// </summary>
    /// <param name="settings">The in-memory configuration.</param>
    /// <returns>The collection, ready to build.</returns>
    private static ServiceCollection ComposeServices(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();

        // The hosts register the configuration in DI; the module's BindConfiguration resolves it from there. The
        // runner itself is registered by the Worker-only seam, because spawning processes is a Worker capability.
        services.AddSingleton<IConfiguration>(configuration);
        services.AddEvidenceModule(configuration);
        services.AddParseRunner(configuration);

        return services;
    }

    private static string[] RecordsText(ParseRunSucceeded success) =>
        success.Records.Select(record => Encoding.UTF8.GetString(record.Utf8Json.Span)).ToArray();

    private static byte[] Zip(string name, string content)
    {
        using var stream = new MemoryStream();

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry(name).Open();
            using var writer = new StreamWriter(entry, Encoding.UTF8);

            writer.Write(content);
        }

        return stream.ToArray();
    }
}
