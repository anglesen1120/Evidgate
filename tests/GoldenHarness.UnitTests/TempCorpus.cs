namespace GoldenHarness.UnitTests;

/// <summary>
/// Writes a throwaway corpus for the layout and runner contracts: one temporary root per test,
/// case directories below it, and the two files a valid case holds. Every test deletes the root
/// it created, so the helper only ever creates.
/// </summary>
internal static class TempCorpus
{
    internal const string CaseJson =
        """{"producer_id":"junit-pytest","parser_id":"junit_pytest","parser_version":"3.1","rationale":"captured from a real run"}""";

    internal const string ExpectedJson =
        """{"raw_report_status":"parsed","declared_count":1,"events":[{"producer_id":"junit-pytest","sequence":0,"case_key":"tests/test_a.py::test_a","raw_status":"passed","normalised_status":"passed"}]}""";

    internal static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "evidgate-golden-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    internal static string CaseDirectory(string root, string producer, string caseName)
    {
        var directory = Path.GetFullPath(Path.Combine(root, producer, caseName));
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal static void WriteCaseFiles(
        string directory,
        string? caseJson = CaseJson,
        string? expectedJson = ExpectedJson,
        params string[] reports)
    {
        if (caseJson is not null)
        {
            File.WriteAllText(Path.Combine(directory, "case.json"), caseJson);
        }

        if (expectedJson is not null)
        {
            File.WriteAllText(Path.Combine(directory, "expected.json"), expectedJson);
        }

        foreach (var report in reports)
        {
            File.WriteAllText(Path.Combine(directory, report), "<testsuite/>");
        }
    }
}
