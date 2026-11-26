using Architecture.Tests.Rules;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// The incident recorder reads runs, attempts and test revisions through the Orchestrator and Catalog
/// <c>.Contracts</c> seams, so no SQL text under Healing's <c>Infrastructure/Persistence</c> may name a table Healing
/// does not own. The rule runs on synthetic cross-module reads and on every real persistence file, so a reintroduced
/// join fails instead of passing vacuously.
/// </summary>
public class HealingPersistenceSqlOwnershipTests
{
    private static readonly HashSet<string> OwnedTables = new(StringComparer.Ordinal)
    {
        "ai_credit_account",
        "ai_credit_entry",
        "healing_llm_call",
        "incident",
        "incident_attempt",
    };

    private const string PersistenceDirectory = "src/Modules/Evidgate.Modules.Healing/Infrastructure/Persistence";

    [Theory]
    [InlineData("run")]
    [InlineData("case_attempt")]
    [InlineData("planned_execution")]
    [InlineData("test_revision")]
    [InlineData("evidence_revision")]
    public void Synthetic_Cross_Module_Read_Is_Detected(string table)
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Modules/Evidgate.Modules.Healing/Bypass.cs", $"select 1 from public.{table} where id = @id"),
        };

        HealingSqlRules.FindViolations(files).ShouldHaveSingleItem().ShouldContain($"public.{table}");
    }

    [Fact]
    public void Synthetic_Own_Table_Read_Is_Not_A_Violation()
    {
        var files = new (string Path, string Text)[]
        {
            ("src/Modules/Evidgate.Modules.Healing/Clean.cs", "select 1 from public.incident i join public.incident_attempt a on a.incident_id = i.id"),
        };

        HealingSqlRules.FindViolations(files).ShouldBeEmpty();
    }

    [Fact]
    public void Persistence_Sql_Names_Only_Tables_Healing_Owns()
    {
        var files = SolutionLayout.SourcesOf(PersistenceDirectory)
            .Select(file => (Path: file.Path[(PersistenceDirectory.Length + 1)..], file.Text))
            .ToList();

        // The statements must actually be in the scan, or a clean result proves nothing.
        files.Select(file => file.Path).ShouldContain("IncidentRecorder.cs");

        var named = files.SelectMany(file => PolicySqlTableRules.TablesNamedIn(file.Text)).Distinct(StringComparer.Ordinal);
        named.Order(StringComparer.Ordinal).ShouldBe(["ai_credit_account", "ai_credit_entry", "healing_llm_call", "incident", "incident_attempt"]);
        HealingSqlRules.FindViolations(files).ShouldBeEmpty();
    }

    // The rule itself is generic over the owner's table set; Policy's class owns it.
    private static class HealingSqlRules
    {
        public static IReadOnlyList<string> FindViolations(IEnumerable<(string Path, string Text)> files) =>
            PolicySqlTableRules.FindViolations(files, OwnedTables);
    }
}
