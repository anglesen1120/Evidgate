// 04.3.1-T1.2: the 04 §6 `ext` allow-list. Only keys this reporter declares reach `ext`;
// everything else is dropped, and a key whose name looks like a credential is dropped
// and counted separately so a leak attempt is observable rather than silent.
//
// The declared set (04 §6 names the test-derived subset — test.title, test.file,
// test.location, project name, engine version):
//   schema     — required by contracts/schemas/event-envelope.v1.json on every `ext`
//   title      — test.title / step.title
//   file       — test.location.file
//   location   — test.location / step.location as {file, line, column}
//   project    — the Playwright project name (test.parent.project()?.name)
//   engine     — the @playwright/test version carried in the capability document
//   marker     — run.log marker: run_begin | run_end
//   capability — the capability document carrier on the first run.log envelope (AC3)
//   category   — step.category (test.step vs pw:api vs expect …)
//   case_id    — the resolved catalog case id (04.3.2-T1.2)
//   variant_hash — sha256 over the canonical normalised params (04.3.2-T1.3)
//   target_env — EVIDGATE_TARGET_ENV, else the Playwright project name (04.3.2-T1.4)
//   identity_conflict — true when annotation and sidecar disagree (04.3.2 AC2)
//   warn       — a warning payload on a run.log envelope (04.3.2 §5)
//   normalized — the 00 §8.3 normalised status of a test.finished (04.3.2-T2.1, AC6)
//   skip_reason — "fixme" for a skipped test whose annotations include fixme (AC6)
//   xfail      — true when expectedStatus is "failed" and the attempt passed or failed (AC6)
//   error_summary — first line of the first error message, ANSI-stripped, ≤ 256 chars;
//              never a stack line (04.3.2-T2.1)
//   attachments — the attempt's attachment records (base name, kind, sensitive, outcome,
//              code, artifact id) on a test.finished that carried attachments (04.3.3-T2.2)
export const EXT_ALLOWED_KEYS = [
  'schema',
  'title',
  'file',
  'location',
  'project',
  'engine',
  'marker',
  'capability',
  'category',
  'case_id',
  'variant_hash',
  'target_env',
  'identity_conflict',
  'warn',
  'normalized',
  'skip_reason',
  'xfail',
  'error_summary',
  'attachments',
] as const;

export type ExtAllowedKey = (typeof EXT_ALLOWED_KEYS)[number];

/** A key name that looks like a credential is dropped and counted (04 §6, S3). */
export const SECRET_KEY_PATTERN = /token|secret|password/i;

export interface ExtFilterResult {
  /** The allow-listed members, in the candidate's own order. */
  ext: Record<string, unknown>;
  /** Non-secret keys that were not allow-listed. */
  dropped: string[];
  /** Keys whose name matched {@link SECRET_KEY_PATTERN}. */
  secretDropped: string[];
}

const allowed: Record<string, true> = Object.fromEntries(
  EXT_ALLOWED_KEYS.map((key) => [key, true] as const),
);

/**
 * Keeps only the allow-listed keys of `candidate`. An `undefined` value is skipped
 * without being counted: it is an absent member, not a dropped one.
 */
export function filterExt(candidate: Record<string, unknown>): ExtFilterResult {
  const ext: Record<string, unknown> = {};
  const dropped: string[] = [];
  const secretDropped: string[] = [];
  for (const [key, value] of Object.entries(candidate)) {
    if (value === undefined) continue;
    if (allowed[key] === true) {
      ext[key] = value;
      continue;
    }
    if (SECRET_KEY_PATTERN.test(key)) secretDropped.push(key);
    else dropped.push(key);
  }
  return { ext, dropped, secretDropped };
}
