// The rules the sidecar loader shares with the server's own case reader: the size, depth, node,
// scalar, warning and path caps, the clean-text test, the strict UTF-8 decode and the text
// renderings used in warnings. A refusal here must match what the server refuses, so these live
// in one module instead of one copy per reader.
import { resolve, sep } from 'node:path';

/** The file name that marks a sidecar. */
export const SIDECAR_FILE_NAME = 'evidgate.cases.yaml';

/** Hard cap on the sidecar text, mirroring the server reader's `MaxChars`. */
export const MAX_BYTES = 1024 * 1024;
/** Aggregate cap on the sidecar bytes read for one run; past it the remaining manifests are skipped. */
export const MAX_TOTAL_BYTES = 8 * 1024 * 1024;
/** Nesting cap, mirroring the server reader's `MaxDepth`. */
export const MAX_DEPTH = 8;
/** Node cap, mirroring the server reader's `MaxNodes`. */
export const MAX_NODES = 40_000;
/** Scalar cap, mirroring the server reader's `MaxScalarChars`. */
export const MAX_SCALAR_CHARS = 4096;
/** Warnings kept for one run, mirroring `CaseDiscovery.MaxProblems`. */
export const MAX_WARNINGS = 2000;
/** Longest repository-relative path, mirroring `RepoPath.MaxLength`. */
export const MAX_PATH_CHARS = 1024;
/** Control, format and lone-surrogate code points, refused in paths and names (`TextRules.IsClean`). */
export const UNCLEAN = /[\p{Cc}\p{Cf}\p{Cs}]/u;

// A second copy of this rule lives in `cli/src/caseId.ts` by design: the reporter loads the CLI
// lazily (a broken CLI install must not stop the run), so it cannot import it at runtime.
// `cli/src/caseId.table.json` drives both validators' tests so the copies cannot drift.
/** The 36-character hyphenated UUID form, any case (`AnnotationParser.IsCanonicalUuid`). */
const CANONICAL_UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
/** The nil UUID: a spelling the schema accepts and the server refuses (`Guid.Empty`). */
const NIL_UUID = '00000000-0000-0000-0000-000000000000';

/**
 * True for the strict canonical case id the server accepts (`AnnotationParser.IsCanonicalUuid`):
 * the 36-character hyphenated form in any case, and never the nil UUID. `urn:uuid:`-prefixed,
 * braced, unhyphenated and nil spellings are refused, so a sidecar id can never name a case the
 * server would resolve to a different (or no) identity. Exported so the annotation path can reuse
 * the same rule.
 */
export function isCanonicalCaseId(value: string): boolean {
  return CANONICAL_UUID.test(value) && value.toLowerCase() !== NIL_UUID;
}

/**
 * Strict UTF-8: malformed bytes are refused instead of being repaired to U+FFFD (which would
 * silently map a different, replacement-character filename). A leading BOM is kept, exactly as
 * `readFileSync(path, 'utf8')` returned it before this loader owned the decode.
 */
export const UTF8 = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true });

/** A short, single-line, control-free rendering of untrusted text for a message (`TextRules.Brief`). */
export function brief(text: string, maxChars = 120): string {
  const truncated = truncate(text, maxChars);
  let out = '';
  for (const character of truncated) {
    out += UNCLEAN.test(character) ? '?' : character;
  }
  return truncated.length < text.length ? `${out}…` : out;
}

/** The longest prefix that ends on a rune boundary (`TextRules.Truncate`). */
function truncate(text: string, maxChars: number): string {
  if (text.length <= maxChars) {
    return text;
  }
  let end = maxChars;
  const last = text.charCodeAt(end - 1);
  if (last >= 0xd800 && last <= 0xdbff) {
    end -= 1;
  }
  return text.slice(0, end);
}

/** The absolute, forward-slashed form of a path, used for both index keys and lookups. */
export function normalizePath(path: string): string {
  return resolve(path).split(sep).join('/');
}

/** The repository path rules (`RepoPath.IsValid`): relative, forward slashes, no `.`/`..` segment. */
export function isRepoPath(path: string): boolean {
  if (path.length === 0 || path.length > MAX_PATH_CHARS) {
    return false;
  }
  if (path.startsWith('/') || path.endsWith('/') || path.includes('\\')) {
    return false;
  }
  if (UNCLEAN.test(path)) {
    return false;
  }
  return path
    .split('/')
    .every((segment) => segment.length > 0 && segment !== '.' && segment !== '..');
}
