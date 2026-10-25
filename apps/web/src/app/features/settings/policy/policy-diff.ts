// 09.7.1-T4.2 (plan 09.7.1 §2 AC4, §10 T4.2 and the T4 delivery notes; COORDINATOR DECISIONS
// S83 P2/P4): the field-level diff between two `policy_revision.body` snapshots. The revision
// diff component renders what this function returns and decides nothing itself, so the diff is a
// pure function over two JSON values: testable at every boundary, no Angular import, no signal,
// no clock.
//
// Shape of the answer — one row per difference, `PolicyDiffRow.before`/`after` are RENDERED text
// (`JSON.stringify`, clipped) and `null` only when that side has no value at all, so "the field
// was removed" is `before: '15', after: null` and never a guess about an empty string.
//
// Why the rules are these rules:
//
//  * The body is a closed JSON object (contracts/schemas/policy-revision.v1.json): a union of keys
//    recursed in a stable order, so two calls over the same pair always produce the same rows and
//    a row cap truncates the same way every time.
//  * `required_suites`, `required_cases`, `critical_cases`, `allowed_skips`, `required_artifacts`
//    and `quarantine_ids` are SETS on the wire (`uniqueItems: true`), so they are compared as
//    sets: inserting one case into a 1000-case list is ONE row at the array's pointer, not 1000
//    shifted indices. Reordering, which the canonical (RFC 8785) hashing does not care about
//    either, is therefore not a difference at all.
//  * Arrays that carry objects or arrays are not sets (index order is their identity), so those
//    compare by index.
//  * Every key is plain data: `__proto__` and `constructor` are read through `Object.hasOwn` and
//    subscript access and are NEVER assigned onto an object, so a hostile body cannot pollute a
//    prototype through the diff.
//  * Bounded: recursion stops at 32 container levels (a deeper pair is compared by deep equality
//    and reported as one row), at 2000 rows (`truncated: true`) and at 200 rendered characters
//    per value. The work is linear in the size of the two bodies — no repeated scan, no sort of
//    the array elements, no recursion beyond the cap — so the 64 KiB transport cap
//    (`x-max-bytes`) is not a performance question.

/** What happened to the value at {@link PolicyDiffRow.path}. */
export type PolicyDiffKind = 'added' | 'removed' | 'changed';

/** One difference between two policy bodies. */
export interface PolicyDiffRow {
  /** RFC 6901 JSON pointer from the body root (`''` is the root itself), `~0`/`~1` escaped. */
  readonly path: string;
  readonly kind: PolicyDiffKind;
  /** The `before` value rendered as JSON text and clipped; `null` when it is absent. */
  readonly before: string | null;
  /** The `after` value rendered as JSON text and clipped; `null` when it is absent. */
  readonly after: string | null;
}

/** The whole answer: the rows, in a stable order, and whether the row cap cut them short. */
export interface PolicyDiff {
  readonly rows: readonly PolicyDiffRow[];
  readonly truncated: boolean;
}

/** Rows never exceed this count; the 2001st difference sets `truncated` instead. */
export const MAX_DIFF_ROWS = 2000;

/** Container levels walked; a pair deeper than this is one deep-equality comparison. */
export const MAX_DIFF_DEPTH = 32;

/** Rendered characters per value: longer JSON text is cut and ends with {@link ELLIPSIS}. */
export const MAX_DIFF_VALUE_CHARS = 200;

const ELLIPSIS = '\u2026';
const HIGH_SURROGATE_MIN = 0xd800;
const HIGH_SURROGATE_MAX = 0xdbff;
const UNRENDERABLE = '[unrenderable]';

/** A JSON object: never `null`, never an array (`JSON.parse` output is the only real input). */
type JsonObject = Record<string, unknown>;

/** The rows collected so far plus the flag the cap sets. */
interface DiffSink {
  readonly rows: PolicyDiffRow[];
  truncated: boolean;
}

function isArrayValue(value: unknown): value is readonly unknown[] {
  return Array.isArray(value);
}

function isRecord(value: unknown): value is JsonObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/**
 * True when every member is a value `JSON.parse` can produce INSIDE an array (no object, no
 * nested array): only such a pair is compared as a set.
 */
function isPrimitiveArray(value: readonly unknown[]): boolean {
  for (const element of value) {
    const isPrimitive =
      element === null ||
      typeof element === 'string' ||
      typeof element === 'number' ||
      typeof element === 'boolean';
    if (!isPrimitive) {
      return false;
    }
  }
  return true;
}

/** Cut rendered text to the value budget without ever leaving half a surrogate pair behind. */
function clip(text: string): string {
  if (text.length <= MAX_DIFF_VALUE_CHARS) {
    return text;
  }
  let kept = text.slice(0, MAX_DIFF_VALUE_CHARS - ELLIPSIS.length);
  const lastUnit = kept.charCodeAt(kept.length - 1);
  if (lastUnit >= HIGH_SURROGATE_MIN && lastUnit <= HIGH_SURROGATE_MAX) {
    kept = kept.slice(0, -1);
  }
  return `${kept}${ELLIPSIS}`;
}

/** JSON text of a value: never throws, never `String(object)`. */
function serialise(value: unknown): string {
  try {
    const text: string | undefined = JSON.stringify(value);
    return text === undefined ? UNRENDERABLE : text;
  } catch {
    // A cyclic or BigInt value cannot come from `JSON.parse`; describe it rather than throw.
    return Object.prototype.toString.call(value);
  }
}

/**
 * A value as the diff shows it: JSON text, clipped to the value budget (never HTML, so the
 * component may interpolate it as text).
 */
function render(value: unknown): string {
  return clip(serialise(value));
}

function primitiveEquals(before: unknown, after: unknown): boolean {
  if (typeof before === 'number' && typeof after === 'number') {
    // `-0` and `0` canonicalise to the same body; `NaN` can only arrive from hand-built input,
    // where "identical" is still the honest answer.
    return before === after || (Number.isNaN(before) && Number.isNaN(after));
  }
  return before === after;
}

/**
 * Structural equality of two JSON values, iterative so a deeply nested pair cannot exhaust the
 * stack, and linear in the size of the two values. Key ORDER does not matter; array order does.
 */
function deepEqual(before: unknown, after: unknown): boolean {
  const pending: Array<readonly [unknown, unknown]> = [[before, after]];
  let pair = pending.pop();
  while (pair !== undefined) {
    const [left, right] = pair;
    if (!primitiveEquals(left, right)) {
      if (isRecord(left) && isRecord(right)) {
        const leftKeys = Object.keys(left);
        if (leftKeys.length !== Object.keys(right).length) {
          return false;
        }
        for (const key of leftKeys) {
          if (!Object.hasOwn(right, key)) {
            return false;
          }
          pending.push([left[key], right[key]]);
        }
      } else if (isArrayValue(left) && isArrayValue(right)) {
        if (left.length !== right.length) {
          return false;
        }
        for (let index = 0; index < left.length; index += 1) {
          pending.push([left[index], right[index]]);
        }
      } else {
        return false;
      }
    }
    pair = pending.pop();
  }
  return true;
}

/** True once the row budget is spent; from then on every walker stops and `truncated` is set. */
function isFull(sink: DiffSink): boolean {
  if (sink.rows.length < MAX_DIFF_ROWS) {
    return false;
  }
  sink.truncated = true;
  return true;
}

function pushRow(sink: DiffSink, row: PolicyDiffRow): void {
  if (isFull(sink)) {
    return;
  }
  sink.rows.push(row);
}

/** Two values that are not two containers of the same kind: equal, or one `changed` row. */
function diffLeaf(before: unknown, after: unknown, path: string, sink: DiffSink): void {
  if (primitiveEquals(before, after) || deepEqual(before, after)) {
    return;
  }
  pushRow(sink, { path, kind: 'changed', before: render(before), after: render(after) });
}

/**
 * Two arrays of primitives under set semantics: one row per element added and per element
 * removed, both at the ARRAY's pointer. Removals come first (before-order), then additions
 * (after-order), so the rows always read as before → after and never depend on the element count.
 */
function diffPrimitiveSets(
  before: readonly unknown[],
  after: readonly unknown[],
  path: string,
  sink: DiffSink,
): void {
  const beforeKeys = new Set<string>();
  for (const element of before) {
    beforeKeys.add(serialise(element));
  }
  const afterKeys = new Set<string>();
  for (const element of after) {
    afterKeys.add(serialise(element));
  }
  // A repeated element is one row, not one per occurrence.
  const reported = new Set<string>();

  for (const element of before) {
    if (isFull(sink)) {
      return;
    }
    const key = serialise(element);
    if (afterKeys.has(key) || reported.has(key)) {
      continue;
    }
    reported.add(key);
    pushRow(sink, { path, kind: 'removed', before: render(element), after: null });
  }
  for (const element of after) {
    if (isFull(sink)) {
      return;
    }
    const key = serialise(element);
    if (beforeKeys.has(key) || reported.has(key)) {
      continue;
    }
    reported.add(key);
    pushRow(sink, { path, kind: 'added', before: null, after: render(element) });
  }
}

/** Two arrays whose order is their identity: compare element by element, by index. */
function diffArraysByIndex(
  before: readonly unknown[],
  after: readonly unknown[],
  path: string,
  depth: number,
  sink: DiffSink,
): void {
  const length = Math.max(before.length, after.length);
  for (let index = 0; index < length; index += 1) {
    if (isFull(sink)) {
      return;
    }
    const itemPath = `${path}/${index}`;
    if (index >= after.length) {
      pushRow(sink, {
        path: itemPath,
        kind: 'removed',
        before: render(before[index]),
        after: null,
      });
    } else if (index >= before.length) {
      pushRow(sink, { path: itemPath, kind: 'added', before: null, after: render(after[index]) });
    } else {
      walk(before[index], after[index], itemPath, depth + 1, sink);
    }
  }
}

/** Ascending by UTF-16 code unit: locale-independent, so the row order never moves. */
function compareKeys(left: string, right: string): number {
  if (left < right) {
    return -1;
  }
  if (left > right) {
    return 1;
  }
  return 0;
}

/** Two objects: the union of their keys in ascending order, recursed. */
function diffObjects(
  before: JsonObject,
  after: JsonObject,
  path: string,
  depth: number,
  sink: DiffSink,
): void {
  const keys = new Set<string>(Object.keys(before));
  for (const key of Object.keys(after)) {
    keys.add(key);
  }
  const sorted = [...keys].sort(compareKeys);

  for (const key of sorted) {
    if (isFull(sink)) {
      return;
    }
    // RFC 6901: escape `~` first, then `/` (`a~/b` is `a~0~1b` in a pointer segment).
    const keyPath = `${path}/${key.replaceAll('~', '~0').replaceAll('/', '~1')}`;
    // `Object.hasOwn` + subscript access: an own `__proto__` key is plain data here.
    const inBefore = Object.hasOwn(before, key);
    const inAfter = Object.hasOwn(after, key);
    if (inBefore && inAfter) {
      walk(before[key], after[key], keyPath, depth + 1, sink);
    } else if (inAfter) {
      pushRow(sink, { path: keyPath, kind: 'added', before: null, after: render(after[key]) });
    } else {
      pushRow(sink, { path: keyPath, kind: 'removed', before: render(before[key]), after: null });
    }
  }
}

/**
 * Compare one pair of values at `path`, `depth` container levels below the root.
 *
 * Set comparison runs at any depth because it does not recurse (it is two linear passes over the
 * elements); the depth cap applies to the walk that DOES recurse — object keys and array indices.
 * At the cap, two containers are compared by deep equality and, if they differ, reported as one
 * `changed` row at that container's pointer.
 */
function walk(before: unknown, after: unknown, path: string, depth: number, sink: DiffSink): void {
  if (isFull(sink)) {
    return;
  }
  if (isArrayValue(before) && isArrayValue(after)) {
    if (isPrimitiveArray(before) && isPrimitiveArray(after)) {
      diffPrimitiveSets(before, after, path, sink);
      return;
    }
    if (depth >= MAX_DIFF_DEPTH) {
      diffLeaf(before, after, path, sink);
      return;
    }
    diffArraysByIndex(before, after, path, depth, sink);
    return;
  }
  if (isRecord(before) && isRecord(after)) {
    if (depth >= MAX_DIFF_DEPTH) {
      diffLeaf(before, after, path, sink);
      return;
    }
    diffObjects(before, after, path, depth, sink);
    return;
  }
  diffLeaf(before, after, path, sink);
}

/**
 * The field-level difference between two policy bodies, as rendered rows.
 *
 * Identical bodies (including two lists that merely hold the same elements in another order) give
 * zero rows. Neither argument is read for writing: the inputs are never mutated, and a key named
 * `__proto__` or `constructor` is compared like any other key.
 */
export function diffPolicyBodies(before: unknown, after: unknown): PolicyDiff {
  const sink: DiffSink = { rows: [], truncated: false };
  walk(before, after, '', 0, sink);
  return { rows: sink.rows, truncated: sink.truncated };
}
