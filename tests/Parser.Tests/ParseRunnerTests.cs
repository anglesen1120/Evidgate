using System.IO.Compression;
using System.Text;
using Evidgate.Modules.Evidence;
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
            "--media-type",
            "application/xml",
            "--cpu-seconds",
            "20",
            "--max-file-bytes",
            "0",
        ]);

        var environment = harness.ReadEnvironmentDump("env.txt");

        // The allow-list, and only the three variables /bin/sh adds for itself before it runs `env`: this child is a
        // shell script, so what the runner set and what the shell added are the whole environment it can dump.
        environment.Keys.Order(StringComparer.Ordinal).ToArray().ShouldBe(
        [
            ChildEnvironment.HeapLimitVariable,
            ChildEnvironment.InvariantGlobalizationVariable,
            "PWD",
            "SHLVL",
            ChildEnvironment.TempDirectoryVariable,
            "_",
        ]);

        // 512 MiB as the runtime parses it (hexadecimal bytes), spelled out here rather than round-tripped through
        // the runner's own formatter.
        environment[ChildEnvironment.HeapLimitVariable].ShouldBe("0x20000000");
        environment[ChildEnvironment.InvariantGlobalizationVariable].ShouldBe("1");
        environment[ChildEnvironment.TempDirectoryVariable].ShouldStartWith(harness.TempRoot);
        environment[ChildEnvironment.TempDirectoryVariable].ShouldNotBe(harness.TempRoot);

        // The scrubbing is not vacuous: the test host does have a PATH, and the child does not.
        Environment.GetEnvironmentVariable("PATH").ShouldNotBeNullOrEmpty();
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
        harness.ReadEnvironmentDump("env.txt").ShouldContainKey(ChildEnvironment.HeapLimitVariable);
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
        options.EffectiveMaxOutputBytes(1024).ShouldBe(ParseRunnerOptions.OutputBytesFloor);
        options.EffectiveMaxOutputBytes(1024 * 1024).ShouldBe(24L * 1024 * 1024);
        options.EffectiveMaxOutputBytes(long.MaxValue).ShouldBe(ParseRunnerOptions.OutputBytesCeiling);

        // Record cap: floor, linear at the measured density, ceiling — and the ceiling admits the ~10M records a
        // 16 MiB token-dense input measured in the plan Notes, so a real dense artifact is never a false breach.
        options.EffectiveMaxOutputRecords(1024).ShouldBe(ParseRunnerOptions.RecordCountFloor);
        options.EffectiveMaxOutputRecords(1024 * 1024)
            .ShouldBe((int)(1024L * 1024 * ParseRunnerOptions.RecordsPerThousandInputBytes / 1000));
        options.EffectiveMaxOutputRecords(16L * 1024 * 1024).ShouldBe(ParseRunnerOptions.RecordCountCeiling);
        options.EffectiveMaxOutputRecords(16L * 1024 * 1024).ShouldBeGreaterThan(10_000_000);

        // Per-line cap: four times the input cap below the knee, the line floor from there on, never above the byte
        // cap. A 1 KiB input cannot honestly claim a 1 MiB line, so the floor starts only once the input is large.
        options.EffectiveMaxRecordBytes(1024).ShouldBe((int)(ParseRunnerOptions.RecordLineFactor * 1024));
        options.EffectiveMaxRecordBytes(4L * 1024 * 1024).ShouldBe((int)ParseRunnerOptions.RecordLineFloor);

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
        using var off = Compose(new Dictionary<string, string?>());

        off.GetService<IParseRunner>().ShouldBeNull();

        var bound = off.GetRequiredService<IOptions<ParseRunnerOptions>>().Value;
        bound.OutOfProcess.ShouldBeFalse();
        bound.ChildPath.ShouldBe(ParseRunnerOptions.DefaultChildPath);
        bound.CpuSeconds.ShouldBe(ParseRunnerOptions.DefaultCpuSeconds);

        using var on = Compose(new Dictionary<string, string?>
        {
            [$"{ParseRunnerOptions.SectionName}:{nameof(ParseRunnerOptions.OutOfProcess)}"] = "true",
            [$"{ParseRunnerOptions.SectionName}:{nameof(ParseRunnerOptions.CpuSeconds)}"] = "11",
            [$"{ParseRunnerOptions.SectionName}:{nameof(ParseRunnerOptions.ChildPath)}"] = "/srv/parser/Evidgate.Parser.dll",
        });

        on.GetRequiredService<IParseRunner>().ShouldBeOfType<ChildProcessParseRunner>();

        var configured = on.GetRequiredService<IOptions<ParseRunnerOptions>>().Value;
        configured.OutOfProcess.ShouldBeTrue();
        configured.CpuSeconds.ShouldBe(11);
        configured.ChildPath.ShouldBe("/srv/parser/Evidgate.Parser.dll");
    }

    private static ServiceProvider Compose(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();

        // The hosts register the configuration in DI; the module's BindConfiguration resolves it from there.
        services.AddSingleton<IConfiguration>(configuration);
        services.AddEvidenceModule(configuration);

        return services.BuildServiceProvider();
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
