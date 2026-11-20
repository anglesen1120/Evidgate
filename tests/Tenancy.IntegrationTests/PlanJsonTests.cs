using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// Sorts anywhere in a statement must be checked against their own row input, not detached producers.
/// Literal PostgreSQL-shaped plans distinguish bounded CTE output ordering from sorting attempt rows.
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
                  "Node Type": "Limit",
                  "Parent Relationship": "InitPlan",
                  "Subplan Name": "CTE page",
                  "Plans": [
                    {
                      "Node Type": "Sort",
                      "Parent Relationship": "Outer",
                      "Plans": [
                        {
                          "Node Type": "Index Scan",
                          "Parent Relationship": "Outer",
                          "Relation Name": "case_attempt",
                          "Index Name": "case_attempt_run_execution_attempt_key"
                        }
                      ]
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

    [Fact]
    public void SortsScanOf_DetachedProducerSort_IsDetectedAlongsideItsIndexRead()
    {
        PlanJson.SortsScanOf(DetachedProducer, "case_attempt").ShouldBeTrue(
            "the CTE producer must read attempt rows in index order without sorting them");

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

    [Theory]
    [InlineData("InitPlan")]
    [InlineData("SubPlan")]
    public void SortsScanOf_OuterCteSort_DoesNotSortDetachedProducersRows(string relationship)
    {
        var plan = $$"""
            [{
              "Plan": {
                "Node Type": "Sort",
                "Plans": [
                  {
                    "Node Type": "Limit",
                    "Parent Relationship": "{{relationship}}",
                    "Subplan Name": "CTE page",
                    "Plans": [{
                      "Node Type": "Index Scan",
                      "Parent Relationship": "Outer",
                      "Relation Name": "case_attempt",
                      "Index Name": "case_attempt_run_execution_attempt_key"
                    }]
                  },
                  { "Node Type": "CTE Scan", "Parent Relationship": "Outer", "CTE Name": "page" }
                ]
              }
            }]
            """;

        PlanJson.SortsScanOf(plan, "case_attempt").ShouldBeFalse(
            "sorting bounded CTE output does not sort the producer's attempt scan");
        PlanJson.UsesIndex(PlanJson.Nodes(plan), "case_attempt_run_execution_attempt_key").ShouldBeTrue();
    }

    [Theory]
    [InlineData("Seq Scan")]
    [InlineData("Index Scan")]
    public void SortsScanOf_SubqueryScanInput_IsIncluded(string scan)
    {
        var plan = $$"""
            [{
              "Plan": {
                "Node Type": "Sort",
                "Plans": [{
                  "Node Type": "Subquery Scan",
                  "Parent Relationship": "Outer",
                  "Plans": [{
                    "Node Type": "{{scan}}",
                    "Parent Relationship": "Subquery",
                    "Relation Name": "case_attempt"
                  }]
                }]
              }
            }]
            """;

        PlanJson.SortsScanOf(plan, "case_attempt").ShouldBeTrue(
            "a Subquery Scan's child supplies the rows the parent sorts");
        PlanJson.SortsScanOf(plan, "artifact").ShouldBeFalse();
    }
}
