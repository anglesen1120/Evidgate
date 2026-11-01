using Architecture.Tests.Rules;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// N4 on the Policy module's persistence: the run, its plan, the pinned suite, the frozen revision, its attempts and the
/// case behind each planned revision belong to other modules and are read through their <c>.Contracts</c>, so no SQL
/// text under <c>Infrastructure/Persistence</c> may name a table Policy does not own. The rule runs on a synthetic
/// cross-module read and on every real persistence file, so a reintroduced join fails instead of passing vacuously.
/// </summary>
public class PolicyPersistenceSqlOwnershipTests
{
    /// <summary>
    /// The tables the Policy module owns: its revisions and activations, the verdicts it stores, and the waivers and
    /// quarantine entries it is the only writer of.
    /// </summary>
    private static readonly HashSet<string> OwnedTables = new(StringComparer.Ordinal)
    {
        "evaluation",
        "policy_revision",
        "quarantine_entry",
        "waiver",
    };

    /// <summary>The persistence directory whose SQL text is this module's own; SQL lives nowhere else (N3).</summary>
    private static string PersistenceDirectory =>
        Path.Combine(SolutionLayout.RepoRoot, "src", "Modules", "Evidgate.Modules.Policy", "Infrastructure", "Persistence");

    [Fact]
    public void Synthetic_Cross_Module_Read_Is_Detected()
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Modules/Evidgate.Modules.Policy/Bypass.cs", "select 1 from public.run where id = @run_id"),
        };

        PolicySqlTableRules.FindViolations(files, OwnedTables)
            .ShouldHaveSingleItem()
            .ShouldContain("public.run");
    }

    [Fact]
    public void Synthetic_Foreign_Tenant_Table_Read_Is_Detected()
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Modules/Evidgate.Modules.Policy/Bypass.cs", "select 1 from public.project where id = @project_id"),
        };

        PolicySqlTableRules.FindViolations(files, OwnedTables)
            .ShouldHaveSingleItem()
            .ShouldContain("public.project");
    }

    [Fact]
    public void Synthetic_Own_Table_Read_Is_Not_A_Violation()
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Modules/Evidgate.Modules.Policy/Clean.cs", "select 1 from public.evaluation where id = @id"),
        };

        PolicySqlTableRules.FindViolations(files, OwnedTables).ShouldBeEmpty();
    }

    [Fact]
    public void Persistence_Sql_Names_Only_Tables_Policy_Owns()
    {
        var files = Directory
            .EnumerateFiles(PersistenceDirectory, "*.cs", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => (Path: Path.GetRelativePath(PersistenceDirectory, path), Text: File.ReadAllText(path)))
            .ToList();

        // The statements must actually be in the scan, or a clean result proves nothing.
        files.Select(file => file.Path).ShouldContain("EvaluationSql.cs");
        files.Select(file => file.Path).ShouldContain("ExceptionsSnapshotSql.cs");

        var named = files
            .SelectMany(file => PolicySqlTableRules.TablesNamedIn(file.Text))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        named.ShouldBe(["evaluation", "policy_revision", "quarantine_entry", "waiver"]);
        PolicySqlTableRules.FindViolations(files, OwnedTables).ShouldBeEmpty();
    }
}
