// The `ext` allow-list. Only keys this reporter declares reach `ext`; everything else is
// dropped, and a key whose name looks like a credential is dropped and counted separately, so
// a leak attempt is observable rather than silent.
//
// `attachments` is set ONLY from our own `AttachmentRecord` rows, never from a Playwright
// member: `stepExt` deletes a raw `TestStep.attachments`, whose `{path, body}` would put an
// absolute path and the file bytes straight into the spool.
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

/** A key name that looks like a credential, so it is dropped and counted separately. */
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
