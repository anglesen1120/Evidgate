using System.Globalization;

namespace Isolation.Tests;

internal sealed partial class TenantRowFactory
{
    /// <summary>One value for a NOT NULL column the database cannot fill; the store type decides.</summary>
    private static object Value(string table, ScannedColumn column, Guid? forcedId)
    {
        if (forcedId is { } id && string.Equals(column.Name, TenantFkScan.TenantKeyColumn, StringComparison.Ordinal))
        {
            return id;
        }

        // audit_log.actor_type is constrained to a closed list (migration 0015).
        if (string.Equals(table, "public.audit_log", StringComparison.Ordinal)
            && string.Equals(column.Name, "actor_type", StringComparison.Ordinal))
        {
            return "system";
        }

        // Columns whose CHECK constraints name a closed vocabulary or a shape (migrations 0016-0031).
        var special = (table, column.Name) switch
        {
            ("public.ingest_token", "prefix") => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12],
            ("public.ingest_token", "scopes") => new[] { "run:create" },
            // api_token's prefix is one platform-wide public lookup key: a closed 12-character grammar plus a
            // unique constraint, so the generic 13-character text candidate would trip the CHECK before the
            // composite FK under test; a fresh value per row keeps two generated rows (a valid one and its
            // corrupt sibling) from colliding on the prefix key first.
            ("public.api_token", "prefix") => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12],
            // The token's grant: a fresh row names a real catalogue code rather than the generic text
            // candidate, so the shape stays readable next to the permission set the row is created with.
            ("public.api_token", "permissions") => new[] { "run:read" },
            ("public.test_case", "first_seen_commit") => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12],
            ("public.case_identity_source", "source") => "annotation",
            ("public.test_revision", "commit_sha") => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12],
            ("public.test_revision", "revision_hash" or "content_sha256" or "assertion_hash") => "sha256:" + new string('a', 64),
            ("public.suite_revision", "revision_hash") or ("public.variant", "variant_hash") => "sha256:" + new string('a', 64),
            ("public.suite", "slug") => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12],
            ("public.fixture", "fixture_key") or ("public.fixture_revision", "fixture_key") =>
                "fixture" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12],
            ("public.fixture_revision", "revision_hash") => "sha256:" + new string('a', 64),
            ("public.fixture_revision", "seed_version") => "v1",
            ("public.fixture_revision", "cleanup_policy") => "drop",
            ("public.fixture_revision", "resource_prefix") => "fx_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..16],
            ("public.fixture_revision", "body_ref") => "seeds/" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8] + ".json",
            ("public.environment_revision", "revision_no") => Interlocked.Increment(ref _revisionNo),
            ("public.manifest", "manifest_hash") or ("public.environment_revision", "revision_hash") =>
                "sha256:" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            ("public.policy_revision", "hash") =>
                "sha256:" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            ("public.adapter_capability", "mode") => "full",
            ("public.waiver", "state") => "requested",
            ("public.waiver", "scope") => "case",
            ("public.quarantine_entry", "state") => "active",
            // role.kind is closed to member|service|staff, and membership_role (0097) only accepts a member role.
            ("public.role", "kind") => "member",
            ("public.artifact", "kind") => "trace",
            ("public.artifact", "execution_key") => new string('a', 64),
            ("public.artifact", "checksum") => "sha256:" + new string('b', 64),
            ("public.artifact", "sensitivity") => "normal",
            ("public.artifact", "scan_state") => "pending",
            ("public.artifact", "state") => "pending_upload",
            ("public.artifact_kind_policy", "kind") => "trace",
            ("public.artifact_kind_policy", "default_sensitivity") => "normal",
            // redaction_rule (0071) closes `kind` to the eight contract kinds and `rule_type` to the four-type
            // allowlist, so the generic text candidate is rejected by both CHECKs; `name_pattern` stays generic on
            // purpose — 13 lowercase alphanumerics satisfy the 1..128 bounded-charset CHECK, and a value unique per
            // generated row keeps the (org_id, project_id, kind, rule_type, name_pattern) key from colliding with the
            // valid row the corrupt one is compared to.
            ("public.redaction_rule", "kind") => "trace",
            ("public.redaction_rule", "rule_type") => "header",
            ("public.producer_slot", "shard_index") => 1,
            // draft.kind and draft.base_commit carry checks: a declared kind and a 7 to 64 char lowercase hex sha.
            ("public.draft", "kind") => "ai",
            ("public.draft", "base_commit") => "abcdef1",
            ("public.feature_node", "kind") => "module",
            ("public.feature_node", "position") or ("public.checklist_item", "position") =>
                Interlocked.Increment(ref _authoringPosition),
            ("public.checklist_item" or "public.manual_test_case", "priority") => "high",
            ("public.checklist_item" or "public.manual_test_case", "state") => "draft",
            ("public.checklist_item" or "public.manual_test_case", "source") or ("public.authoring_version", "source") => "manual",
            ("public.manual_test_case", "steps") => "{}",
            ("public.manual_test_case", "code") =>
                "PROBE-" + Interlocked.Increment(ref _authoringCode).ToString("D3", CultureInfo.InvariantCulture),
            ("public.authoring_version", "entity_kind") => "feature_node",
            ("public.authoring_version", "change_kind") => "create",
            ("public.authoring_version", "content_hash") => "sha256:" + new string('c', 64),
            // requirement and requirement_version (0132) close their vocabulary in CHECKs the generic text
            // candidate cannot satisfy; every other member is generic or defaulted.
            ("public.requirement", "kind") => "epic",
            ("public.requirement_version", "source") => "manual",
            ("public.requirement_version", "content_hash") => "sha256:" + new string('e', 64),
            // authoring_change (0067) closes change_type to four tokens and wants a non-empty
            // affected_versions object, so the generic text and '{}' candidates are both rejected before
            // a composite FK under test is reached. A comment payload needs its body and no other member,
            // and a comment is the one type whose pending row never takes the structural-overlap lock.
            ("public.authoring_change", "change_type") => "comment",
            ("public.authoring_change", "payload") => "{\"body\":\"probe\"}",
            ("public.authoring_change", "affected_versions") =>
                "{\"" + Guid.CreateVersion7().ToString("D", CultureInfo.InvariantCulture) + "\":1}",
            ("public.project", "case_prefix") =>
                "P" + Interlocked.Increment(ref _casePrefix).ToString("D4", CultureInfo.InvariantCulture),
            ("public.producer_slot", "producer_id") => "shard-1",
            // The usage ledger, roll-up and quota counter (0046/0047/0077) close their vocabulary in
            // CHECKs the generic text rule cannot satisfy: `metric` is the plan §4 list and `period` is
            // its `YYYY-MM` shape. `billing_key` stays generic on purpose — the generic candidate is 13
            // characters and unique per generated row, which is what `unique (org_id, billing_key)` needs
            // so the corrupt row never trips the dedupe key before the composite FK under test.
            ("public.usage_ledger_entry" or "public.usage_rollup" or "public.quota_counter", "metric") => "results",
            ("public.usage_ledger_entry" or "public.usage_rollup" or "public.quota_counter", "period") => "2026-10",
            // 0098 closes `payload_hash` to the 00 §7 content address, which the generic text candidate fails; no
            // unique key covers it, so one constant digest serves every generated ledger row.
            ("public.usage_ledger_entry", "payload_hash") => "sha256:" + new string('a', 64),
            // usage_price (0073) closes `metric` to the same list and its `currency` to three uppercase
            // letters, which the generic text candidate (13 chars) fails; every other constrained member is
            // either nullable (`effective_to`, `included_quota`) or satisfied by the generic candidate
            // (`unit_price`'s 1, the identity `id`, and `effective_from`'s timestamp).
            ("public.usage_price", "metric") => "results",
            ("public.usage_price", "currency") => "USD",
            // evidence_revision (0049) closes `completeness` to the 00 §8.5 pair, so the generic text
            // candidate is rejected there; `manifest_hash` is generic on purpose (13 characters is
            // inside the 1-200 CHECK) and `missing` keeps its '{}' default, which is what the
            // "complete has no missing codes" CHECK requires. `revision_no` is pinned to 1 because the
            // table's BEFORE INSERT trigger refuses anything but the run's max + 1: every generated
            // revision row is the first revision of a run this factory created for the same org, so a
            // shared counter (environment_revision above) would hand a fresh run revision 2 and the
            // trigger would answer 23514 before the composite FK under test is reached.
            ("public.evidence_revision", "completeness") => "complete",
            ("public.evidence_revision", "revision_no") => 1,
            // evaluation (0059) closes completeness, outcome, decision and mode to the evaluator's wire
            // tokens, and its two content addresses to the 00 §7 sha256 shape: the generic text candidate
            // satisfies none of them. `missing`/`reasons` keep their '{}' default (a CHECK rejects the
            // generic {"probe"} element), and the live key is satisfiable because every generated row gets
            // a freshly created run and policy revision.
            // release_decision (0129) closes completeness and decision, wants a JSON array of components and a
            // content address that is unique per row (the dedupe key is (org, release, policy revision, hash)).
            ("public.release_decision", "completeness") => "complete",
            ("public.release_decision", "decision") => "PASS",
            ("public.release_decision", "component_evaluations") => "[]",
            ("public.release_decision", "inputs_hash") =>
                "sha256:" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            ("public.evaluation", "completeness") => "complete",
            ("public.evaluation", "outcome") => "all_passed",
            ("public.evaluation", "decision") => "PASS",
            ("public.evaluation", "mode") => "live",
            // A fresh frozen revision per generated row: evaluation's live key is
            // (org_id, run_id, evidence_revision) where mode = 'live', and the corrupt row reuses the valid
            // row's run, so a constant would collide on that index (23505) before the composite FK under test
            // is reached.
            ("public.evaluation", "evidence_revision") => Interlocked.Increment(ref _evaluationRevision),
            ("public.evaluation", "policy_hash" or "exceptions_hash") =>
                "sha256:" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            // The LLM gateway tables (0054) close `purpose`, `status` and `period` in CHECKs the generic
            // text candidate cannot satisfy. `purpose` is deliberately the one value whose row needs NO
            // incident: `incident_id`/`candidate_id` are nullable and therefore left unset, so
            // healing_llm_call_incident_required_check holds (it demands an incident only for the other
            // purposes) together with the authoring CHECK that wants both attribution columns null.
            ("public.healing_llm_call", "purpose") => "authoring_generation",
            ("public.healing_llm_call", "status") => "ok",
            ("public.healing_llm_budget", "period") => "daily",
            // planned_execution's execution_key is the 00 §7 sha256 hex and its unique key is
            // (org_id, run_id, execution_key), so a constant would collide with the valid row the
            // corrupt attempt inserts just before it: a fresh 64-hex value per generated row.
            ("public.planned_execution", "execution_key") =>
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            ("public.planned_execution", "variant_hash") => "sha256:" + new string('a', 64),
            ("public.planned_execution", "shard_id") => "shard-1",
            // case_attempt's execution_key is the 00 §7 sha256 hex and its unique key is
            // (org_id, run_id, execution_key, attempt_no), so a constant would collide with the valid
            // row the corrupt attempt inserts just before it: a fresh 64-hex value per generated row.
            // Its composite FK to planned_execution fills the column from the parent, so this rule is
            // only reached when the parent lookup did not set it.
            ("public.case_attempt", "execution_key") =>
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            // case_attempt's unique key is (org_id, run_id, execution_key, attempt_no) and the corrupt
            // row reuses the valid row's run_id and execution_key, so a constant attempt_no would
            // collide on that key (23505) before the composite FK under test is reached: a fresh
            // number per generated row, like planned_execution's execution_key above.
            ("public.case_attempt", "attempt_no") => Interlocked.Increment(ref _attemptNo),
            ("public.case_attempt", "raw_status") => "passed",
            ("public.case_attempt", "normalized_status") => "passed",
            // incident's execution_key is the sha256 hex (CHECK ^[0-9a-f]{64}$) and part of its run key, so
            // a fresh value per generated row keeps the corrupt sibling from colliding on that key first.
            ("public.incident", "execution_key") =>
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            ("public.incident_attempt", "result") => "failed",
            // event's execution_key is the 00 §7 sha256 hex (0034 CHECK ^[0-9a-f]{64}$), its type is the
            // envelope's dotted lower_snake shape and its payload_hash is the 00 §7 RFC 8785 content
            // address (0034 CHECK ^sha256:[0-9a-f]{64}$): the generic text rule satisfies none of the three.
            ("public.event", "execution_key") => new string('a', 64),
            ("public.event", "type") => "test.step",
            ("public.event", "payload_hash") => "sha256:" + new string('a', 64),
            // event's unique key is (org_id, run_id, producer_id, sequence) and the corrupt row reuses the
            // valid row's run_id and producer_id, so a constant sequence would collide on that key (23505)
            // before the composite FK under test is reached: a fresh number per generated row, like
            // case_attempt's attempt_no above.
            ("public.event", "sequence") => Interlocked.Increment(ref _sequence),
            // project_ai_settings (0064) is the append-only guidance chain. Its `content_hash` is the 00 §7 content
            // address (CHECK ^sha256:[0-9a-f]{64}$) and `languages` is closed to the two G1 locales with a
            // cardinality 1..2 / `<@ {vi,en}` CHECK, so the generic text and text[] candidates are both rejected by
            // the table before a composite FK under test is reached. Every other constrained member has a default
            // (`description`, `writing_style`, … are `not null default ''`), and `template_code`/`template_version`
            // are nullable and written as a pair, so leaving them unset satisfies `project_ai_settings_template_check`.
            // `version_no` stays the generic 1: the unique key is (org_id, project_id, version_no) and the corrupt row
            // differs from the valid one in its org and parent, never only in the version.
            ("public.project_ai_settings", "content_hash") => "sha256:" + new string('d', 64),
            ("public.project_ai_settings", "languages") => new[] { "en" },
            // masking_event (0066) closes `pattern_class` to the scanner's three classes and `action` to the scan's
            // two decisions, so the generic text candidate satisfies neither CHECK; `hit_count`'s generic 1 already
            // satisfies `hit_count > 0`. The composite FK fills `artifact_id` from the parent's tenant.
            ("public.masking_event", "pattern_class") => "aws_access_key",
            ("public.masking_event", "action") => "flagged",
            // release_component (0072) closes `digest` to the content-address grammar (CHECK
            // ^sha256:[0-9a-f]{64}$), which the generic text candidate satisfies nowhere. `component_key`'s
            // candidate is 13 lowercase alphanumerics and fits the key grammar; the composite FKs fill
            // `release_id` and `environment_revision_id` from parents of the same org, and the trigger allows
            // the insert because every generated parent release is a draft.
            ("public.release_component", "digest") => "sha256:" + new string('e', 64),
            // billing_statement closes `kind` to the one statement kind, `period` to the `YYYY-MM` shape,
            // `currency` to three uppercase letters and `content_hash` to the lowercase sha256 hex, so the
            // generic text candidate satisfies none of them. The generic `{}` candidate is an object and
            // inside the document's size cap, and `total_amount`/`line_count` accept the generic numbers.
            ("public.billing_statement", "kind") => "invoice",
            ("public.billing_statement", "period") => "2026-10",
            ("public.billing_statement", "currency") => "USD",
            ("public.billing_statement", "content_hash") => new string('a', 64),
            // The commit-registration tables (0106) close three shapes the generic text candidate fails: the
            // provider enumeration, the RFC 8785 payload address and the `secret:<name>` reference. Each value
            // is inside its own CHECK and none is part of a unique key, so a constant serves every generated row.
            ("public.webhook_delivery", "provider") => "github",
            ("public.webhook_delivery", "payload_hash") => "sha256:" + new string('a', 64),
            // webhook_endpoint (0131) closes `url` to https, `secret_hint` to exactly four characters and holds a
            // partial unique key on (org_id, project_id, url): a fresh URL per row keeps a corrupt sibling from
            // colliding on that key before the composite FK under test is reached. The delivery's one event type
            // is a closed list.
            ("public.webhook_endpoint", "url") =>
                "https://probe" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12] + ".example/hook",
            ("public.webhook_endpoint", "secret_hint") => "abcd",
            ("public.webhook_outbound_delivery", "event_type") => "evaluation.created",
            ("public.git_repo_binding", "webhook_secret_ref") => "secret:probe",
            // commit_sha is a lowercase hex run; a fresh one per row keeps the (org_id, binding_id, commit_sha)
            // key from colliding with the valid row a corrupt sibling is generated next to.
            ("public.catalog_sync", "commit_sha") => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12],
            // The installation tables (0108) carry two global keys the generic rules would collide on: GitHub's
            // installation id is unique across every org, and the OAuth state's primary key is its 32-byte digest,
            // so both are fresh per row and the RLS suite's two orgs never collide on them.
            ("public.github_installation", "installation_id") =>
                8_000_000_000L + Interlocked.Increment(ref _githubInstallationId),
            // support_grant.scopes has a shape CHECK and expires_at must fall within 72 hours of creation, so
            // the generic "probe" and the 400-day timestamp are both refused.
            ("public.support_grant", "scopes") => new[] { "case:read" },
            ("public.support_grant", "expires_at") => DateTimeOffset.UtcNow.AddHours(1),
            ("public.github_install_state", "state_hash") =>
                (byte[])[.. Guid.NewGuid().ToByteArray(), .. Guid.NewGuid().ToByteArray()],
            _ => (object?)null,
        };

        if (special is not null)
        {
            return special;
        }

        return WithoutTypeModifier(column.StoreType) switch
        {
            "bytea" => new byte[32],
            "uuid" => Guid.CreateVersion7(),
            "text" or "character varying" => "probe" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8],
            "jsonb" => "{}",
            "timestamp with time zone" => DateTimeOffset.UtcNow.AddDays(400),
            "date" => DateOnly.FromDateTime(DateTime.UtcNow),
            "integer" => 1,
            "smallint" => (short)1,
            "bigint" => 1L,
            "numeric" => 1m,
            "boolean" => false,
            "text[]" => new[] { "probe" },
            _ => throw new InvalidOperationException(
                $"TenantRowFactory has no value rule for {table}.{column.Name} of type '{column.StoreType}', which is NOT NULL "
                + "without a default: add its store type to the generator instead of skipping the table"),
        };
    }

    /// <summary>
    /// The store type without its modifier, so one rule covers every precision and length:
    /// <c>numeric(12,6)</c> (0054's caps and costs) and the parameterised <c>character varying(64)</c> or
    /// <c>timestamp(3) with time zone</c> forms match the <c>numeric</c>/<c>character varying</c>/
    /// <c>timestamp with time zone</c> arms instead of falling through to the "no value rule" error.
    /// </summary>
    private static string WithoutTypeModifier(string storeType)
    {
        var open = storeType.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
        {
            return storeType;
        }

        var close = storeType.IndexOf(')', open);
        return close < 0
            ? storeType
            : (storeType[..open] + storeType[(close + 1)..]).Trim();
    }
}
