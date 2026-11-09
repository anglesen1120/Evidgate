# Fillfactor audit of in-place-updated tables

Why: a heap-only-tuple (HOT) update needs free room on the same page **and** no changed column that an index
covers (an index key column, or a column used in a partial-index predicate). Page headroom (`fillfactor`)
only pays off where the second condition already holds. Migrations that set it: `session` (0045),
`usage_rollup` (0047), `quota_counter` (0086).

Method: every `UPDATE ... SET` and `ON CONFLICT DO UPDATE` in `src/` and `migrations/` was listed, then each
table's indexes were read from the migration that creates them. Evidence is static; no `pg_stat_user_tables`
(`n_tup_hot_upd` vs `n_tup_upd`) run was taken, so every verdict is `[INFERENCE]`. The method for a later
measurement: run the owning integration class on a dedicated database and compare the two counters for the table.

Every table also has the shared `set_updated_at_and_version` trigger; it writes `updated_at`/`version`, which are
never indexed, so it does not defeat HOT.

Rate classes: **request** = at least once per API call; **task** = once or more per queued task or ingest batch;
**rare** = human or admin action, or once per run lifecycle.

| Table | Updated columns | Indexed column updated? | Rate | Recommendation |
|---|---|---|---|---|
| `task` | `state`, `locked_until`, `available_at`, `attempts`, `last_error` | yes: all in `task_claim_idx`, `task_active_idx` or their predicates (0008) | task | none: HOT impossible |
| `outbox` | `dispatched_at` | yes: predicate of `outbox_undispatched_idx` (0006) | task | none: HOT impossible |
| `producer_slot` | `last_sequence`, `bound_token_jti`, `bound_ci_run_ref`, `closed_at`, `declared_*` | no: indexes are `(org_id, run_id)`, `(org_id, run_id, producer_id)`, `(org_id, run_id, shard_index)`, PK (0029) | task (per ingest batch) | **candidate: fillfactor 80**, needs a migration |
| `idempotency_record` | `response_status`, `response_body` | no (`expires_at` is not written) (0007) | request, exactly once per row | candidate, low value: one update per row would need roughly half-empty pages; measure first |
| `ingest_token` | `last_used_at` | no `[INFERENCE]`: no secondary index found in migrations | rare (guarded to one write per interval) | none |
| `run` | `state` | no `[INFERENCE]`: `run_project_created_idx` is the only secondary index (0031) | rare (few transitions per run) | none |
| `healing_llm_budget` | `used_cost`, `used_tokens` | no (keyed by org, project, period) | request once its consumer ships (no consumer code yet) | add `fillfactor=80` in the migration that adds the consumer, same shape as `quota_counter` |
| `healing_switch` | `mode`, `reason`, `set_by`, `at` | no `[INFERENCE]` | rare (operator action) | none |
| `retention_policy` | `artifact_days`, `metadata_days` | no `[INFERENCE]` | rare | none |
| `usage_duplicate_attempt` | `attempts`, `last_seen_at` (upsert on `(org_id, billing_key)`) | no `[INFERENCE]` | task | none until measured |
| `waiver` | `state`, `approved_by` | yes: `state` in `waiver_state_expiry_idx` (0027) | rare | none |
| `quarantine_entry` | `state` | yes: `quarantine_entry_state_expiry_idx` (0030) | rare | none |
| `policy_revision` | `state` | `[INFERENCE]` | rare (activation) | none |
| `membership`, `project`, `release`, `suite`, `suite_revision`, `test_case`, `run_cancel_request` | status/state, head pointer, taxonomy | mixed | rare | none |
| `feature_node`, `checklist_item`, `manual_test_case`, `authoring_change` | title, position, state, `deleted_at`, apply stamps | `[INFERENCE]` | rare (human edits) | none |

Outcome: one table qualifies today (`producer_slot`), and one qualifies when its writer lands
(`healing_llm_budget`). `idempotency_record` needs a measurement before a decision. Applying any of these
needs a migration, which this audit does not add.
