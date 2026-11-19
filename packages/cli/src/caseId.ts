// The canonical case-id rule for the CLI. The Playwright reporter keeps its own copy in
// `adapter-playwright/src/sidecarRules.ts` on purpose: it loads the CLI lazily so a broken CLI
// install only degrades attachments, and a static import would make the CLI a hard start-up
// dependency. `caseId.table.json` is read by both test suites so the two copies cannot drift.

/** The 36-character hyphenated UUID form, any case. */
const CANONICAL_UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
/** The nil UUID: a spelling the schema accepts and the server refuses. */
const NIL_UUID = '00000000-0000-0000-0000-000000000000';

/** True for the strict canonical case id the server accepts: hyphenated, any case, never nil. */
export function isCanonicalCaseId(value: string): boolean {
  return CANONICAL_UUID.test(value) && value.toLowerCase() !== NIL_UUID;
}
