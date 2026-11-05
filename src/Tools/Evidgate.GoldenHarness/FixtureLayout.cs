namespace Evidgate.GoldenHarness;

/// <summary>
/// The layout contract: <c>fixtures/&lt;producer&gt;/&lt;case&gt;/</c> holds <c>case.json</c>,
/// <c>expected.json</c> and at least one report file, while the older <c>meta.json</c> corpus
/// and the protocol <c>contracts</c> namespace are skipped so neither corpus reads the other.
/// A symbolic link is refused rather than followed, so a case only ever reads its own bytes.
/// </summary>
public static class FixtureLayout
{
    private const string MetaFileName = "meta.json";
    private const string ReadmeFileName = "README.md";
    private const string ContractsDirectory = "contracts";

    // A README explains the case and is not evidence, so it never satisfies the report-file
    // requirement; a case holding only its README still fails the scan by name.
    private static readonly HashSet<string> ReservedFileNames =
        new(StringComparer.Ordinal) { FixtureReaders.CaseFileName, FixtureReaders.ExpectedFileName, ReadmeFileName };

    /// <summary>
    /// Reads every case below <paramref name="fixturesRoot"/>, or only the cases of
    /// <paramref name="producer"/>, in ordinal order of producer then case.
    /// </summary>
    /// <exception cref="FixtureLayoutException">
    /// A case is missing a required file, a file does not parse, or a case entry is a symbolic link.
    /// </exception>
    public static IReadOnlyList<FixtureCase> Scan(string fixturesRoot, string? producer = null)
    {
        ArgumentNullException.ThrowIfNull(fixturesRoot);
        if (string.IsNullOrWhiteSpace(fixturesRoot))
        {
            throw new FixtureLayoutException("<empty>", "fixtures root is empty");
        }

        var root = Path.GetFullPath(fixturesRoot);
        if (!Directory.Exists(root))
        {
            throw new FixtureLayoutException(root, "fixtures root does not exist");
        }

        var cases = new List<FixtureCase>();
        foreach (var producerDirectory in ProducerDirectories(root, producer))
        {
            var producerName = Path.GetFileName(producerDirectory);
            foreach (var caseDirectory in CaseDirectories(producerName, producerDirectory))
            {
                cases.Add(ReadCase(producerName, producerDirectory, caseDirectory));
            }
        }

        return
        [
            .. cases
                .OrderBy(fixture => fixture.Producer, StringComparer.Ordinal)
                .ThenBy(fixture => fixture.Name, StringComparer.Ordinal),
        ];
    }

    private static List<string> ProducerDirectories(string root, string? producer)
    {
        if (producer is not null)
        {
            if (producer.Length == 0
                || producer.Contains('/')
                || producer.Contains('\\')
                || producer is "." or "..")
            {
                throw new FixtureLayoutException(
                    producer,
                    "a producer name is one directory name below the fixtures root");
            }

            var selected = Path.Combine(root, producer);
            if (!Directory.Exists(selected))
            {
                throw new FixtureLayoutException(producer, "no producer directory below the fixtures root");
            }

            RefuseLink(new DirectoryInfo(selected), producer);
            return [selected];
        }

        var producers = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (IsSkippedDirectory(name))
            {
                continue;
            }

            RefuseLink(new DirectoryInfo(directory), name);
            producers.Add(directory);
        }

        producers.Sort(StringComparer.Ordinal);
        return producers;
    }

    /// <summary>
    /// Every directory below the producer that holds <c>case.json</c> or <c>expected.json</c>.
    /// A directory holding <c>meta.json</c> is the other corpus, so neither it nor anything
    /// below it is read.
    /// </summary>
    private static IEnumerable<string> CaseDirectories(string producerName, string producerDirectory)
    {
        var pending = new Stack<string>();
        pending.Push(producerDirectory);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if (File.Exists(Path.Combine(directory, MetaFileName)))
            {
                continue;
            }

            if (IsCaseDirectory(directory))
            {
                yield return directory;
                continue;
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (IsSkippedDirectory(name))
                {
                    continue;
                }

                RefuseLink(new DirectoryInfo(child), FixtureName(producerName, producerDirectory, child));
                pending.Push(child);
            }
        }
    }

    private static bool IsCaseDirectory(string directory) =>
        File.Exists(Path.Combine(directory, FixtureReaders.CaseFileName))
        || File.Exists(Path.Combine(directory, FixtureReaders.ExpectedFileName));

    private static FixtureCase ReadCase(string producerName, string producerDirectory, string directory)
    {
        var relative = Path.GetRelativePath(producerDirectory, directory);
        if (relative == ".")
        {
            throw new FixtureLayoutException(
                producerName,
                $"a case needs its own directory below the producer; {FixtureReaders.CaseFileName} and "
                + $"{FixtureReaders.ExpectedFileName} cannot sit directly in it");
        }

        var name = relative.Replace('\\', '/');
        var fixture = producerName + "/" + name;

        // Every entry is validated, not only the ones the reader opens: a link may be named
        // README.md, or be a directory the case scan stops at, and either would still make the
        // case read bytes that came from outside it.
        var reportFiles = CaseEntries(directory, fixture);

        if (!File.Exists(Path.Combine(directory, FixtureReaders.CaseFileName)))
        {
            throw new FixtureLayoutException(fixture, FixtureReaders.CaseFileName + " is missing");
        }

        if (!File.Exists(Path.Combine(directory, FixtureReaders.ExpectedFileName)))
        {
            throw new FixtureLayoutException(fixture, FixtureReaders.ExpectedFileName + " is missing");
        }

        if (reportFiles.Count == 0)
        {
            throw new FixtureLayoutException(
                fixture,
                "no original report file: a case holds "
                + $"{FixtureReaders.CaseFileName}, {FixtureReaders.ExpectedFileName} and at least one report file");
        }

        return new FixtureCase(
            producerName,
            name,
            directory,
            FixtureReaders.ReadMeta(fixture, directory),
            FixtureReaders.ReadExpected(fixture, directory),
            reportFiles);
    }

    /// <summary>
    /// Refuses a link wherever it sits in the case directory and returns the report file names,
    /// so the names a parser may open are the entries this pass already checked.
    /// </summary>
    private static List<string> CaseEntries(string directory, string fixture)
    {
        var names = new List<string>();
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var isDirectory = Directory.Exists(path);
            RefuseLink(isDirectory ? new DirectoryInfo(path) : new FileInfo(path), fixture);

            var name = Path.GetFileName(path);
            if (!isDirectory && !ReservedFileNames.Contains(name))
            {
                names.Add(name);
            }
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static string FixtureName(string producerName, string producerDirectory, string path)
    {
        var relative = Path.GetRelativePath(producerDirectory, path);
        return producerName + "/" + relative.Replace('\\', '/');
    }

    /// <summary>
    /// A link makes a case read bytes that live outside the corpus, so the corpus stops pinning
    /// what it appears to pin; the link is refused by path instead of being followed.
    /// </summary>
    private static void RefuseLink(FileSystemInfo entry, string fixture)
    {
        if (!IsLink(entry))
        {
            return;
        }

        throw new FixtureLayoutException(
            fixture,
            $"'{entry.FullName}' is a symbolic link, which a fixture case must not contain");
    }

    private static bool IsLink(FileSystemInfo entry) =>
        entry.LinkTarget is not null
        || (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0);

    private static bool IsSkippedDirectory(string? name) =>
        name is null || string.Equals(name, ContractsDirectory, StringComparison.Ordinal);
}
