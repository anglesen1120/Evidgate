using System.Reflection;
using Evidgate.Modules.Evidence.Infrastructure.Parsing.Runner;
using Shouldly;
using Xunit;

namespace Parser.Tests;

/// <summary>
/// 06.3.6 §8 S3 and the redaction row of §9 (06.3.6-T1.4): a child's stderr is captured bounded, masked and turned
/// into one printable line on a failed run only — never stored, never on a success, and never logged by the runner
/// itself, because the runner holds no logger at all.
/// <para>
/// The secret shapes are the ones the shared <c>SecretRedactor</c> knows: an unquoted assignment and a quoted
/// connection-string style value. A leak of either into the excerpt, into a success result, or into anything the
/// runner emits would fail here.
/// </para>
/// </summary>
public sealed class ParseStderrRedactionTests
{
    /// <summary>The value that must never survive: distinctive, so an assertion cannot match by accident.</summary>
    private const string Secret = "s3cr3t-Pa55-Value-7d1f";

    /// <summary>
    /// The §9 redaction test: a child that writes a secret and megabytes of noise to stderr must leave a bounded,
    /// masked, single-line excerpt on the failure, expose nothing at all on a success, and the runner must have no
    /// logger that could ever carry the bytes anywhere.
    /// </summary>
    [Fact]
    public async Task ParseChildStderr_NotPersistedOrLogged()
    {
        ParseRunnerHarness.RequirePosixShell("scripted children that write stderr");

        using var harness = new ParseRunnerHarness();

        // The child reports a secret assignment and then floods its pipe, so the capture's bound is exercised by the
        // same run that must be masked.
        using var failing = harness.Script(
            """
            printf 'password=s3cr3t-Pa55-Value-7d1f\n' >&2
            printf 'connection=Host=db;Password=s3cr3t-Pa55-Value-7d1f;Database=ed\n' >&2
            i=1
            while [ $i -le 4096 ]; do
              printf '%01000d' 0 >&2
              i=$((i+1))
            done
            exit 3
            """);

        var failure = (await ParseRunnerHarness.RunAsync(
            failing,
            ParseRunnerProfile.Xml,
            1024,
            [1],
            cancellationToken: TestContext.Current.CancellationToken)).ShouldBeOfType<ParseRunFailed>();

        // Exit 3 is the class the launch contract uses for an internal error and for a closed stdout (the EPIPE
        // path), and it maps to a crash — the excerpt travels beside the reason, never instead of it.
        failure.ChildExitCode.ShouldBe(3);
        ParseRunRejection.TryMap(failure, out var reason).ShouldBeTrue();
        reason.ShouldBe(ParseRunRejection.Crashed);

        var excerpt = failure.StderrExcerpt.ShouldNotBeNull("the child wrote to stderr, so the failure carries it");

        // Bounded: more than four megabytes went into the pipe and the excerpt is one short log line.
        excerpt.Length.ShouldBeLessThanOrEqualTo(512);

        // Masked, by the shared redaction list and not by a second copy of it.
        excerpt.ShouldNotContain(Secret);
        excerpt.ShouldContain("***");

        // One printable line: nothing a terminal could act on, and nothing that could forge a second log record.
        excerpt.ShouldNotContain("\n");
        excerpt.ShouldNotContain("\r");
        excerpt.Any(char.IsControl).ShouldBeFalse();

        // The same child, but succeeding: the record stream is the only thing a success carries, and the type has no
        // stderr member at all — so no caller can log a success's stderr by accident.
        using var succeeding = harness.Script(
            """
            printf 'password=s3cr3t-Pa55-Value-7d1f\n' >&2
            printf '{"done":true,"count":0}\n'
            """);

        var success = await ParseRunnerHarness.RunAsync(
            succeeding,
            ParseRunnerProfile.Xml,
            1024,
            [1],
            cancellationToken: TestContext.Current.CancellationToken);

        success.ShouldBeOfType<ParseRunSucceeded>().Records.ShouldBeEmpty();
        typeof(ParseRunSucceeded)
            .GetProperty("StderrExcerpt", BindingFlags.Public | BindingFlags.Instance)
            .ShouldBeNull("a success result must have no stderr surface");

        // The runner cannot log what it captures: no logger is injected, held or exposed by it.
        LoggingMembersOf(typeof(ChildProcessParseRunner)).ShouldBeEmpty();
    }

    /// <summary>
    /// A child whose stderr is not text at all still yields a printable excerpt: an invalid UTF-8 sequence becomes
    /// the replacement character and a control byte is dropped, so the excerpt cannot carry either through.
    /// </summary>
    [Fact]
    public async Task Stderr_Bytes_That_Are_Not_Text_Become_One_Printable_Line()
    {
        ParseRunnerHarness.RequirePosixShell("a scripted child that writes raw bytes to stderr");

        using var harness = new ParseRunnerHarness();
        using var runner = harness.Script(
            """
            printf 'before\033[31mafter\000\377\376tail\n' >&2
            exit 3
            """);

        var failure = (await ParseRunnerHarness.RunAsync(
            runner,
            ParseRunnerProfile.Xml,
            1024,
            [1],
            cancellationToken: TestContext.Current.CancellationToken)).ShouldBeOfType<ParseRunFailed>();

        var excerpt = failure.StderrExcerpt.ShouldNotBeNull();

        excerpt.ShouldStartWith("before");
        excerpt.ShouldContain("after");
        excerpt.Any(char.IsControl).ShouldBeFalse("an escape byte from a child must not reach a terminal");
        excerpt.ShouldNotContain("\n");
    }

    /// <summary>Every member of a type whose declared type is a logging abstraction, as <c>member: type</c> names.</summary>
    /// <param name="type">The type to inspect.</param>
    /// <returns>The offenders; empty when the type cannot log.</returns>
    private static List<string> LoggingMembersOf(Type type)
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var offenders = new List<string>();

        foreach (var field in type.GetFields(All))
        {
            AddIfLogging(offenders, field.Name, field.FieldType);
        }

        foreach (var property in type.GetProperties(All))
        {
            AddIfLogging(offenders, property.Name, property.PropertyType);
        }

        foreach (var constructor in type.GetConstructors(All))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                AddIfLogging(offenders, parameter.Name ?? "?", parameter.ParameterType);
            }
        }

        return offenders;
    }

    /// <summary>Records <c>name: type</c> when the declared type is a logging abstraction.</summary>
    /// <param name="offenders">The accumulator.</param>
    /// <param name="name">The member's name.</param>
    /// <param name="type">The member's declared type.</param>
    private static void AddIfLogging(List<string> offenders, string name, Type type)
    {
        if (type.Namespace?.StartsWith("Microsoft.Extensions.Logging", StringComparison.Ordinal) == true)
        {
            offenders.Add($"{name}: {type.FullName}");
        }
    }
}
