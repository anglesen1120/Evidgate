// 09.2.1-T5.1 (plan 09.2.1 §2 AC5, §5 "Step 5 calls OnboardingDataService", §10 T5.1): the ONE
// advisory policy body step 5 knows, its read-only preview projection, and the label sanitiser the
// step uses on server-provided text.
//
// Why a constant and not a read: `contracts/openapi.yaml` has no read that answers a project's
// policy revision or its activation — 07.1.1 declares only `POST /v1/projects/{p}/policies`
// (07.1.1-T3) and `POST /v1/projects/{p}/policy-activation` (07.1.1-T4). So step 5 cannot preview
// a stored body; it shows the default advisory policy this module owns and sends exactly that body.
// The missing read is queue row F18 (plan 09.2.1 line 148, receiver 09.2.1-T5.2's follow-up), and
// until it exists a reload lands on step 5 again where a repeat confirm is idempotent
// (`changed: false`), never a locally remembered "done".
//
// Ruling T62 (`plans/features/07-policy-decisions/07.5.1-late-evidence-and-immutability.md:36`)
// fixes the late-evidence window of the default policy template at 24 h = 1440 minutes; that is the
// only value here that is a decision rather than an omission. Everything else is deliberately the
// neutral default of the contract: no required suite, no required case, no critical case, no
// allowed skip, no quarantine entry, and no gated branch — a project that has never configured a
// policy must not be told that something is required of it.
//
// Every member below exists in `contracts/schemas/policy-revision.v1.json`, the schema
// `POST /v1/projects/{p}/policies` takes as its body (07.1.1-T3), which the schema itself enforces
// with `additionalProperties: false`. `default-policy.spec.ts` validates this exact object against
// that file with the repository's one Ajv configuration, and asserts that the operation's
// requestBody is that file — so the constant cannot drift away from the wire contract unnoticed.
//
// The body is never mutated and never rebuilt per attempt: `JSON.stringify` of the same object
// yields the same bytes in the same key order, which is what makes the content hash — and therefore
// the server's own idempotency for this append — identical on every retry (07.1.1-T3: "the content
// hash is the idempotency key").

import type { PolicyRevisionV1 } from '@evidgate/sdk';

/**
 * The one policy body step 5 appends: the neutral default, advisory by activation, with the T62
 * late-evidence window. Typed as the SDK's `PolicyRevisionV1` so a contract change that drops or
 * renames a member fails the type check here instead of at runtime.
 */
export const DEFAULT_ADVISORY_POLICY: Readonly<PolicyRevisionV1> = {
  required_suites: [],
  required_cases: [],
  late_window_minutes: 1440,
  gating_refs: { default_branch: false },
};

/** The `sha256:` prefix the contract gives a revision hash (07.1.1-T2 canonical hashing). */
const HASH_PREFIX = 'sha256:';

/**
 * How much of the digest a chip shows. The full hash stays available in the response body; the
 * screen only needs enough to tell two revisions apart, and the run list uses the same kind of
 * abbreviation (a chip, never a claimed complete digest — state sheet "Hash chip").
 */
const HASH_ABBREVIATION_LENGTH = 12;

/**
 * The characters a server-provided label must not carry into the page: Unicode controls
 * (`Cc` — C0/C1, which includes the ESC character a CI caller can smuggle into a name), format
 * characters (`Cf` — the bidi overrides and isolates, zero-width joiners and tag characters) and
 * the two line separators (`U+2028`/`U+2029`). A `Cf` character is invisible and a right-to-left
 * override reorders everything rendered after it, which is how a label can make one revision read
 * as another (trojan-source style).
 *
 * `apps/web/src/app/features/runs/list/ref-name-display.ts` solves the same problem for run ref
 * names by ESCAPING each `Cf` into a visible `⟨U+202E⟩` token. That module cannot be imported here
 * — apps/web/lint/rules/no-feature-cross-import.cjs:103 refuses any features/onboarding →
 * features/runs import (W1/T47, the same rule this feature obeys everywhere else) — and step 5
 * does not need the forensic form: the hash and the instant are displayed as identifying metadata,
 * so a format character carries no information here and is removed. The value is still rendered by
 * interpolation only, never as markup.
 */
const INVISIBLE_CHARACTERS = /[\p{Cc}\p{Cf}\u2028\u2029]/gu;

/**
 * The text of a server-provided label with every control/format character removed. Pure, so the
 * page can call it from a `computed` and the spec can assert the category directly.
 */
export function sanitizeLabel(value: string): string {
  return value.replace(INVISIBLE_CHARACTERS, '');
}

/**
 * A revision hash as the terminal panel shows it: `sha256:` plus the first 12 hex characters and
 * an ellipsis, or the whole (sanitised) value when it is already that short. Sanitising happens
 * first, so an invisible character can never be counted as one of the shown characters.
 */
export function abbreviatePolicyHash(hash: string): string {
  const clean = sanitizeLabel(hash).trim();
  const prefix = clean.startsWith(HASH_PREFIX) ? HASH_PREFIX : '';
  const digest = prefix === '' ? clean : clean.slice(HASH_PREFIX.length);
  return digest.length <= HASH_ABBREVIATION_LENGTH
    ? clean
    : `${prefix}${digest.slice(0, HASH_ABBREVIATION_LENGTH)}…`;
}

/**
 * What the read-only preview of a policy body renders. Deliberately a value projection, not
 * markup: the template interpolates each field, so no list member is ever parsed as HTML.
 */
export interface DefaultPolicyPreview {
  /**
   * The late-evidence window in minutes, as the body carries it (T62: 1440), or `null` when the
   * body sets none — absent is not the same fact as `0`, which the contract defines as "no late
   * evidence is accepted" (07.1.1 Q4).
   */
  readonly lateWindowMinutes: number | null;
  /** Suites whose evidence a run must carry (empty in the default body). */
  readonly requiredSuites: readonly string[];
  /** Cases whose evidence a run must carry (empty in the default body). */
  readonly requiredCases: readonly string[];
  /** Whether the repository's default branch is gated (false in the default body). */
  readonly gatesDefaultBranch: boolean;
}

/**
 * Project one policy body onto what the preview shows. Missing arrays read as empty, because a
 * body that omits `required_suites` requires none — the schema's arrays are optional and the
 * server treats an absent list as "nothing required" (00 §8.6).
 */
export function previewDefaultPolicy(body: PolicyRevisionV1): DefaultPolicyPreview {
  return {
    lateWindowMinutes: body.late_window_minutes ?? null,
    requiredSuites: body.required_suites ?? [],
    requiredCases: body.required_cases ?? [],
    gatesDefaultBranch: body.gating_refs?.default_branch === true,
  };
}
