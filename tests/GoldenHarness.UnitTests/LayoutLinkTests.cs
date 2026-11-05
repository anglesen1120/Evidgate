using Evidgate.GoldenHarness;
using Shouldly;
using Xunit;
using static GoldenHarness.UnitTests.TempCorpus;

namespace GoldenHarness.UnitTests;

/// <summary>
/// A case only ever reads its own bytes: an entry that is a symbolic link is refused by its
/// path instead of being followed, whether the link is the case directory itself, one of the
/// files the scan reads, or a directory nested inside the case.
/// </summary>
public sealed partial class LayoutTests
{
    [Fact]
    public void Fixture_SymlinkedCaseDirectory_IsRefusedByName()
    {
        var root = NewRoot();
        var externalRoot = NewRoot();
        try
        {
            var outside = CaseDirectory(externalRoot, "junit-pytest", "outside");
            WriteCaseFiles(outside, reports: "report.xml");

            Directory.CreateDirectory(Path.Combine(root, "junit-pytest"));
            var link = Path.Combine(root, "junit-pytest", "linked");
            Directory.CreateSymbolicLink(link, outside);

            var error = Should.Throw<FixtureLayoutException>(() => FixtureLayout.Scan(root));

            // The bytes live outside the corpus, so the case is refused by its own path
            // instead of being read through the link.
            error.Fixture.ShouldBe("junit-pytest/linked");
            error.Message.ShouldContain(link);
            error.Message.ShouldContain("symbolic link");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(externalRoot, recursive: true);
        }
    }

    [Fact]
    public void Fixture_SymlinkedCaseFile_IsRefusedByName()
    {
        var root = NewRoot();
        var externalRoot = NewRoot();
        try
        {
            var outside = CaseDirectory(externalRoot, "junit-pytest", "outside");
            WriteCaseFiles(outside, reports: "report.xml");

            var directory = CaseDirectory(root, "junit-pytest", "happy");
            WriteCaseFiles(directory, caseJson: null, reports: "report.xml");
            var link = Path.Combine(directory, "case.json");
            File.CreateSymbolicLink(link, Path.Combine(outside, "case.json"));

            var error = Should.Throw<FixtureLayoutException>(() => FixtureLayout.Scan(root));

            error.Fixture.ShouldBe("junit-pytest/happy");
            error.Message.ShouldContain(link);
            error.Message.ShouldContain("symbolic link");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(externalRoot, recursive: true);
        }
    }

    [Fact]
    public void Fixture_SymlinkedReportFile_IsRefusedByName()
    {
        var root = NewRoot();
        var externalRoot = NewRoot();
        try
        {
            var outside = CaseDirectory(externalRoot, "junit-pytest", "outside");
            WriteCaseFiles(outside, reports: "report.xml");

            var directory = CaseDirectory(root, "junit-pytest", "happy");
            WriteCaseFiles(directory);
            var link = Path.Combine(directory, "linked.xml");
            File.CreateSymbolicLink(link, Path.Combine(outside, "report.xml"));

            var error = Should.Throw<FixtureLayoutException>(() => FixtureLayout.Scan(root));

            error.Fixture.ShouldBe("junit-pytest/happy");
            error.Message.ShouldContain(link);
            error.Message.ShouldContain("symbolic link");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(externalRoot, recursive: true);
        }
    }

    [Fact]
    public void Fixture_SymlinkedReadmeFile_IsRefusedByName()
    {
        var root = NewRoot();
        var externalRoot = NewRoot();
        try
        {
            var outside = CaseDirectory(externalRoot, "junit-pytest", "outside");
            File.WriteAllText(Path.Combine(outside, "README.md"), "documented outside the corpus");

            var directory = CaseDirectory(root, "junit-pytest", "happy");
            WriteCaseFiles(directory, reports: "report.xml");
            var link = Path.Combine(directory, "README.md");
            File.CreateSymbolicLink(link, Path.Combine(outside, "README.md"));

            var error = Should.Throw<FixtureLayoutException>(() => FixtureLayout.Scan(root));

            // README.md is never opened as evidence, but a case holds its own entries, so a
            // link named README.md is refused like any other.
            error.Fixture.ShouldBe("junit-pytest/happy");
            error.Message.ShouldContain(link);
            error.Message.ShouldContain("symbolic link");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(externalRoot, recursive: true);
        }
    }

    [Fact]
    public void Fixture_SymlinkedNestedDirectoryInsideACase_IsRefusedByName()
    {
        var root = NewRoot();
        var externalRoot = NewRoot();
        try
        {
            var outside = CaseDirectory(externalRoot, "junit-pytest", "outside");
            WriteCaseFiles(outside, reports: "report.xml");

            var directory = CaseDirectory(root, "junit-pytest", "happy");
            WriteCaseFiles(directory, reports: "report.xml");
            var link = Path.Combine(directory, "nested");
            Directory.CreateSymbolicLink(link, outside);

            var error = Should.Throw<FixtureLayoutException>(() => FixtureLayout.Scan(root));

            // The scan stops at a case, so the case itself has to check the entries below it.
            error.Fixture.ShouldBe("junit-pytest/happy");
            error.Message.ShouldContain(link);
            error.Message.ShouldContain("symbolic link");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(externalRoot, recursive: true);
        }
    }
}
