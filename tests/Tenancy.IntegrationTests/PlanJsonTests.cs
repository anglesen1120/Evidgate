using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The reader of <c>EXPLAIN (FORMAT JSON)</c> documents the plan cases assert with: an ordering assertion
/// must not attribute a detached subplan's <c>Sort</c> (an <c>InitPlan</c>, <c>SubPlan</c> or <c>Subquery</c>
/// child — a CTE producer, a scalar subplan) to the query it belongs to, while index and sequential-scan
/// assertions must still see every relation the whole statement reads. Both fixtures are literal plan
/// documents in the shape PostgreSQL emits, so the two rules are pinned without a database.
/// </summary>
public sealed class PlanJsonTests
{
    /// <summary>The page's own <c>Sort</c> over the CTE scan; the CTE producer (detached) sorts <c>case_attempt</c>.</summary>
    private const string DetachedProducer =
        """
        [
          {
            "Plan": {
              "Node Type": "Limit",
              "Plans": [
                {
                  "Node Type": "Sort",
                  "Parent Relationship": "Outer",
                  "Plans": [
                    { "Node Type": "CTE Scan", "Parent Relationship": "Outer", "CTE Name": "page" }
                  ]
                },
                {
                  "Node Type": "Sort",
                  "Parent Relationship": "InitPlan",
                  "Subplan Name": "CTE page",
                  "Plans": [
                    {
                      "Node Type": "Index Scan",
                      "Parent Relationship": "Outer",
                      "Relation Name": "case_attempt",
                      "Index Name": "case_attempt_run_execution_attempt_key"
                    }
                  ]
                },
                {
                  "Node Type": "Aggregate",
                  "Parent Relationship": "InitPlan",
                  "Subplan Name": "CTE art",
                  "Plans": [
                    {
                      "Node Type": "Index Only Scan",
                      "Parent Relationship": "Outer",
                      "Relation Name": "artifact",
                      "Index Name": "artifact_run_slot_ready_idx"
                    }
                  ]
                }
              ]
            }
          }
        ]
        """;

    /// <summary>The query's own <c>Sort</c> directly over the scan of <c>case_attempt</c>.</summary>
    private const string SortsTheRun =
        """
        [
          {
            "Plan": {
              "Node Type": "Sort",
              "Plans": [
                {
                  "Node Type": "Index Scan",
                  "Parent Relationship": "Outer",
                  "Relation Name": "case_attempt",
                  "Index Name": "case_attempt_run_status_idx"
                }
              ]
            }
          }
        ]
        """;

    /// <summary>A sort inside a detached <c>InitPlan</c> is that subplan's ordering, not the query's.</summary>
    [Fact]
    public void SortsScanOf_IgnoresADetachedSubplanSort_ButKeepsItsIndexRead()
    {
        PlanJson.SortsScanOf(DetachedProducer, "case_attempt").ShouldBeFalse(
            "the CTE producer's sort is an InitPlan's, not the page query's ordering");

        PlanJson.UsesIndex(PlanJson.Nodes(DetachedProducer), "artifact_run_slot_ready_idx").ShouldBeTrue(
            "index assertions read the whole plan, detached subplans included");
        PlanJson.UsesIndex(PlanJson.Nodes(DetachedProducer), "case_attempt_run_execution_attempt_key").ShouldBeTrue();
        PlanJson.ScansSequentially(PlanJson.Nodes(DetachedProducer), "case_attempt").ShouldBeFalse();
    }

    /// <summary>A sort the query itself puts over the run's rows is the in-memory ordering the plan case forbids.</summary>
    [Fact]
    public void SortsScanOf_SeesTheQuerysOwnSortOverTheRun()
    {
        PlanJson.SortsScanOf(SortsTheRun, "case_attempt").ShouldBeTrue();
        PlanJson.SortsScanOf(SortsTheRun, "artifact").ShouldBeFalse();
        PlanJson.ScansSequentially(PlanJson.Nodes(SortsTheRun), "case_attempt").ShouldBeFalse();
        PlanJson.UsesIndex(PlanJson.Nodes(SortsTheRun), "case_attempt_run_status_idx").ShouldBeTrue();
    }
}
