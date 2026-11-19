// Contract vocabulary of the fixture-leak gate (check-no-fixture-code.mjs): data only, no logic.

/**
 * The closed enums of `contracts/openapi.yaml` that a corpus document carries under an ordinary
 * data member, as `member -> the words the contract enumerates for it`. With 09.2.1-T2 the
 * step-2 page names the CI provider (`github_actions`) in its create request and renders the
 * binding/token status words, so those strings live in production code as well; skipping the
 * members outright would also exempt every *other* word a fixture ever puts there, so the
 * exemption is per value: `ENUM_MEMBERS.get(member).has(value)` (09.2.1-T7).
 */
const MISSING_CODES = new Set([
  'missing_shard',
  'count_mismatch',
  'artifact_missing',
  'build_mismatch',
  'unknown_status',
  'setup_error',
  'stale_epoch',
  'missing_result',
]);

export const ENUM_MEMBERS = new Map([
  // The export document's discriminator: `contracts/schemas/authoring-import.v1.json`
  // `properties.format.const`, which the authoring export screen checks.
  ['format', new Set(['authoring-import'])],
  ['provider', new Set(['github_actions'])],
  [
    'status',
    new Set(['pending_verification', 'active', 'disabled', 'rotated', 'expired', 'revoked']),
  ],
  // The decision body's closed vocabularies: the run decision view names them (outcome labels and
  // the eight missing-evidence codes), so production code carries the same words.
  ['outcome', new Set(['all_passed', 'has_failures', 'no_results'])],
  ['missing', MISSING_CODES],
  // A pending-evidence reason is one of the same codes.
  ['reasons', MISSING_CODES],
  // The create-run dialog's ref kind select and the `ref.trigger_event` it sends name the
  // contract's closed vocabularies, so those words occur in production too (`pull_request`).
  ['kind', new Set(['branch', 'tag', 'pull_request'])],
  ['trigger_event', new Set(['push', 'pull_request', 'schedule', 'manual', 'dispatch'])],
  // The security view renders the closed SecurityEventItem.type vocabulary, so these protocol
  // words occur in production too; an unlisted type remains fixture data.
  [
    'type',
    new Set([
      'auth_failure',
      'cross_tenant_probe',
      'secret_scanner_alert',
      'kill_switch_used',
      'support_access',
    ]),
  ],
]);

/**
 * `body.error.details[].reason` is a free string in `contracts/openapi.yaml` (ErrorDetail, the
 * `reason: type: string` member), so its words are listed here from the operation prose that names
 * them; the authoring import/export screen maps each to a message. Exempt only at that trail
 * position and only for these words; any other word, or any `reason` elsewhere, stays a needle.
 */
export const ERROR_DETAIL_REASONS = new Set([
  'duplicate_key', // openapi.yaml importAuthoring prose, ~8303
  'code_conflict', // ~8303
  'value_invalid', // ~8303
  'steps_invalid', // ~8303-8305
  'invalid_text', // ~8303, ~8310
  'unsupported_format', // ~8299, ~8309, ~9516
  'unknown_parent', // importAuthoring prose, ~8303
  'body_too_large', // exportAuthoring prose, ~8400
]);
