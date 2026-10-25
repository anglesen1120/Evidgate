// 09.7.1-T4.2 (plan 09.7.1 §2 AC4, §10 T4.2 and the T4 delivery notes; COORDINATOR DECISIONS
// S83 P2/P4): the field-level diff between two `policy_revision.body` snapshots.
//
// Every case here is a rule the revision-diff component (and the 412 conflict dialog built on it)
// depends on, pinned as a boundary rather than a happy path: set semantics for the `uniqueItems`
// lists, index semantics for arrays that carry objects, RFC 6901 escaping, a hostile `__proto__`
// key, the row/depth/value caps (the row cap flags only a row it actually REFUSED, so 2000
// differences and no more is a complete answer), and the cost of the 64 KiB transport cap. Two
// rules matter most because they are invisible until they are wrong: one insertion into a
// 1000-case list is ONE row (not 1000 shifted indices), and reordering a list is not a difference
// at all (the server canonicalises the body with RFC 8785, where list order is not content).

import {
  MAX_DIFF_DEPTH,
  MAX_DIFF_ROWS,
  MAX_DIFF_VALUE_CHARS,
  diffPolicyBodies,
} from './policy-diff';

const UUID = '00000000-0000-4000-8000-000000000001';

/** A realistic body: every member of contracts/schemas/policy-revision.v1.json. */
const BASE_BODY = {
  required_suites: ['suite-a', 'suite-b'],
  required_cases: ['case-a', 'case-b'],
  critical_cases: ['case-crit'],
  allowed_skips: ['case-flaky'],
  required_artifacts: ['junit_xml', 'log'],
  quarantine_ids: [UUID],
  flaky_budget: 3,
  collection_timeout_minutes: 60,
  late_window_minutes: 15,
  freshness_minutes: 1440,
  gating_refs: { default_branch: true, release_branch_glob: 'release/*' },
};

/** A JSON round trip, so each test owns its own objects and no case can leak a mutation. */
function jsonCopy<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T;
}

/** Freeze every level: a write inside the diff would throw here (modules are strict). */
function deepFreeze(value: unknown): unknown {
  if (typeof value === 'object' && value !== null) {
    for (const key of Object.keys(value)) {
      deepFreeze((value as Record<string, unknown>)[key]);
    }
    Object.freeze(value);
  }
  return value;
}

/** `case-0000` … `case-<count-1>`: the shape the policy lists carry on the wire. */
function caseIds(count: number): string[] {
  return Array.from({ length: count }, (_, index) => `case-${String(index).padStart(4, '0')}`);
}

/** A string of exactly `length` characters, used to grow a body towards the 64 KiB cap. */
function padded(prefix: string, index: number, length: number): string {
  const stem = `${prefix}-${String(index).padStart(5, '0')}-`;
  const filler = '0123456789abcdefghijklmnopqrstuvwxyz'.repeat(2);
  return stem + filler.slice(0, Math.max(1, length - stem.length));
}

/** `count` ids of 48 characters each, plus their index suffix range. */
function paddedIds(prefix: string, count: number, from = 0): string[] {
  return Array.from({ length: count }, (_, index) => padded(prefix, from + index, 48));
}

function uuidIds(count: number): string[] {
  return Array.from(
    { length: count },
    (_, index) => `00000000-0000-4000-8000-${String(index).padStart(12, '0')}`,
  );
}

/** `depth` objects wrapping `leaf`. The container at depth 32 carries `depth - 32` of them. */
function nest(depth: number, leaf: unknown): unknown {
  let value = leaf;
  for (let level = 0; level < depth; level += 1) {
    value = { a: value };
  }
  return value;
}

/** True when the text contains half a UTF-16 surrogate pair (a cut through an astral character). */
function hasLoneSurrogate(text: string): boolean {
  for (let index = 0; index < text.length; index += 1) {
    const unit = text.charCodeAt(index);
    if (unit >= 0xd800 && unit <= 0xdbff) {
      const next = text.charCodeAt(index + 1);
      if (!(next >= 0xdc00 && next <= 0xdfff)) {
        return true;
      }
      index += 1;
    } else if (unit >= 0xdc00 && unit <= 0xdfff) {
      return true;
    }
  }
  return false;
}

describe('diffPolicyBodies (09.7.1-T4.2, S83 P2)', () => {
  it('reports nothing for two identical bodies', () => {
    const before = deepFreeze(jsonCopy(BASE_BODY));
    const after = deepFreeze(jsonCopy(BASE_BODY));
    expect(diffPolicyBodies(before, after)).toEqual({ rows: [], truncated: false });
    // Insertion order is not content: the same members in another order are the same body.
    const first = deepFreeze({ flaky_budget: 3, freshness_minutes: 1440 });
    const second = deepFreeze({ freshness_minutes: 1440, flaky_budget: 3 });
    expect(diffPolicyBodies(first, second)).toEqual({ rows: [], truncated: false });
  });

  it('does not mutate either input', () => {
    const before = deepFreeze(
      jsonCopy({
        ...BASE_BODY,
        required_cases: caseIds(50),
        gating_refs: { default_branch: true, release_branch_glob: 'release/*' },
      }),
    );
    const after = deepFreeze(
      jsonCopy({
        ...BASE_BODY,
        required_cases: caseIds(60),
        flaky_budget: 9,
        gating_refs: { default_branch: false, release_branch_glob: 'main' },
      }),
    );
    const beforeText = JSON.stringify(before);
    const afterText = JSON.stringify(after);

    const first = diffPolicyBodies(before, after);
    const second = diffPolicyBodies(before, after);

    expect(JSON.stringify(before)).toBe(beforeText);
    expect(JSON.stringify(after)).toBe(afterText);
    expect(second).toEqual(first);
  });

  it('reports a changed scalar and recurses into a nested object', () => {
    const before = jsonCopy(BASE_BODY);
    const after = jsonCopy(BASE_BODY);
    after.flaky_budget = 5;
    after.gating_refs.default_branch = false;

    expect(diffPolicyBodies(deepFreeze(before), deepFreeze(after))).toEqual({
      rows: [
        { path: '/flaky_budget', kind: 'changed', before: '3', after: '5' },
        { path: '/gating_refs/default_branch', kind: 'changed', before: 'true', after: 'false' },
      ],
      truncated: false,
    });
  });

  it('reports an added key and a removed key with a null counterpart, keys ascending', () => {
    const before = { flaky_budget: 3, late_window_minutes: 15 };
    const after = { flaky_budget: 3, freshness_minutes: 1440 };

    expect(diffPolicyBodies(deepFreeze(before), deepFreeze(after))).toEqual({
      rows: [
        { path: '/freshness_minutes', kind: 'added', before: null, after: '1440' },
        { path: '/late_window_minutes', kind: 'removed', before: '15', after: null },
      ],
      truncated: false,
    });
  });

  it('renders a whole added object as one row', () => {
    const before = { gating_refs: { default_branch: true } };
    const after = { gating_refs: { default_branch: true, release_branch_glob: 'main' } };

    expect(diffPolicyBodies(deepFreeze({}), deepFreeze(after))).toEqual({
      rows: [
        {
          path: '/gating_refs',
          kind: 'added',
          before: null,
          after: '{"default_branch":true,"release_branch_glob":"main"}',
        },
      ],
      truncated: false,
    });
    // The same pair one level in: the union of keys is walked, not rendered as a whole.
    expect(diffPolicyBodies(deepFreeze(before), deepFreeze(after)).rows).toEqual([
      { path: '/gating_refs/release_branch_glob', kind: 'added', before: null, after: '"main"' },
    ]);
  });

  it('reports one row for one case inserted into a 1000-case list, and one for one removal', () => {
    const cases = caseIds(1000);
    const inserted = [...cases.slice(0, 500), 'case-new', ...cases.slice(500)];

    const added = diffPolicyBodies(
      deepFreeze({ required_cases: cases }),
      deepFreeze({ required_cases: inserted }),
    );
    expect(added).toEqual({
      rows: [{ path: '/required_cases', kind: 'added', before: null, after: '"case-new"' }],
      truncated: false,
    });

    const removed = diffPolicyBodies(
      deepFreeze({ required_cases: cases }),
      deepFreeze({ required_cases: cases.filter((item) => item !== 'case-0500') }),
    );
    expect(removed).toEqual({
      rows: [{ path: '/required_cases', kind: 'removed', before: '"case-0500"', after: null }],
      truncated: false,
    });
  });

  it('treats a reorder-only list as no difference at all', () => {
    const cases = caseIds(1000);
    const reversed = [...cases].reverse();
    const rotated = cases.slice(1).concat(cases.slice(0, 1));
    const forward = deepFreeze({ required_suites: cases });
    const backward = deepFreeze({ required_suites: reversed });
    const rotateFrom = deepFreeze({ allowed_skips: cases });
    const rotateTo = deepFreeze({ allowed_skips: rotated });

    expect(diffPolicyBodies(forward, backward)).toEqual({ rows: [], truncated: false });
    expect(diffPolicyBodies(rotateFrom, rotateTo)).toEqual({ rows: [], truncated: false });
  });

  it('reports a list element only once even when the list holds it twice', () => {
    const before = { required_artifacts: ['log', 'log'] };
    const after = { required_artifacts: ['log', 'junit_xml', 'junit_xml'] };

    expect(diffPolicyBodies(deepFreeze(before), deepFreeze(after))).toEqual({
      rows: [{ path: '/required_artifacts', kind: 'added', before: null, after: '"junit_xml"' }],
      truncated: false,
    });
  });

  it('compares arrays that carry objects by index', () => {
    // v1's lists are all `uniqueItems` string arrays; the rule exists so a body that carries an
    // array of objects (a later revision, or a hostile payload) still diffs predictably.
    const before = { steps: [{ id: 'a' }, { id: 'b' }] };
    const after = { steps: [{ id: 'a' }, { id: 'new' }, { id: 'b' }] };

    expect(diffPolicyBodies(deepFreeze(before), deepFreeze(after))).toEqual({
      rows: [
        { path: '/steps/1/id', kind: 'changed', before: '"b"', after: '"new"' },
        { path: '/steps/2', kind: 'added', before: null, after: '{"id":"b"}' },
      ],
      truncated: false,
    });

    // Set semantics need BOTH sides to be primitive: a mixed pair falls back to index order.
    const mixed = diffPolicyBodies(
      deepFreeze({ required_cases: ['a'] }),
      deepFreeze({ required_cases: [{ case: 'a' }] }),
    );
    expect(mixed.rows).toEqual([
      { path: '/required_cases/0', kind: 'changed', before: '"a"', after: '{"case":"a"}' },
    ]);
  });

  it('escapes `/` and `~` in a pointer segment, `~` first', () => {
    const before = { 'release/branch~glob': 'release/*', 'a/b': { 'c~d': 1 } };
    const after = { 'release/branch~glob': 'main', 'a/b': { 'c~d': 2 } };

    const diff = diffPolicyBodies(deepFreeze(before), deepFreeze(after));
    expect(diff.rows.map((row) => row.path)).toEqual(['/a~1b/c~0d', '/release~1branch~0glob']);
  });

  it('treats `__proto__` and `constructor` as plain data and pollutes nothing', () => {
    const hostile = '{"__proto__":{"polluted":true},"constructor":{"prototype":{"polluted":true}}}';
    const before = deepFreeze(JSON.parse(hostile) as unknown);
    const after = deepFreeze(JSON.parse('{}') as unknown);

    expect(diffPolicyBodies(before, after)).toEqual({
      rows: [
        { path: '/__proto__', kind: 'removed', before: '{"polluted":true}', after: null },
        {
          path: '/constructor',
          kind: 'removed',
          before: '{"prototype":{"polluted":true}}',
          after: null,
        },
      ],
      truncated: false,
    });

    // Both keys recurse like any other key when the other side has them too.
    const nested = diffPolicyBodies(
      deepFreeze(JSON.parse('{"__proto__":{"polluted":true}}') as unknown),
      deepFreeze(JSON.parse('{"__proto__":{"polluted":false}}') as unknown),
    );
    expect(nested.rows).toEqual([
      { path: '/__proto__/polluted', kind: 'changed', before: 'true', after: 'false' },
    ]);

    expect(Object.hasOwn(Object.prototype, 'polluted')).toBe(false);
    expect(({} as Record<string, unknown>)['polluted']).toBeUndefined();
  });

  it('flags the cut at the 2001st difference only', () => {
    const after: Record<string, unknown> = {};
    for (let index = 0; index < MAX_DIFF_ROWS + 1; index += 1) {
      after[`k${String(index).padStart(5, '0')}`] = index;
    }

    const diff = diffPolicyBodies(deepFreeze({}), deepFreeze(after));

    expect(diff.rows).toHaveLength(MAX_DIFF_ROWS);
    expect(diff.truncated).toBe(true);
    expect(diff.rows[0]?.path).toBe('/k00000');
    expect(diff.rows[diff.rows.length - 1]?.path).toBe('/k01999');
  });

  it('does not flag a body with exactly the cap of differences', () => {
    const after: Record<string, unknown> = {};
    for (let index = 0; index < MAX_DIFF_ROWS; index += 1) {
      after[`k${String(index).padStart(5, '0')}`] = index;
    }

    const diff = diffPolicyBodies(deepFreeze({}), deepFreeze(after));

    expect(diff.rows).toHaveLength(MAX_DIFF_ROWS);
    expect(diff.truncated).toBe(false);
  });

  it('does not flag the cap when 2000 differences are followed by an equal key', () => {
    // `shared` sorts after every `k…` key, so the walk reaches it with the row budget already
    // spent: nothing is dropped there, and reaching the cap is not the same as cutting a row.
    const before: Record<string, unknown> = { shared: 'same' };
    const after: Record<string, unknown> = { shared: 'same' };
    for (let index = 0; index < MAX_DIFF_ROWS; index += 1) {
      const key = `k${String(index).padStart(5, '0')}`;
      before[key] = index;
      after[key] = index + 1;
    }

    const diff = diffPolicyBodies(deepFreeze(before), deepFreeze(after));

    expect(diff.rows).toHaveLength(MAX_DIFF_ROWS);
    expect(diff.truncated).toBe(false);
    expect(diff.rows[0]?.kind).toBe('changed');
    expect(diff.rows[diff.rows.length - 1]?.path).toBe('/k01999');

    // An equal key between differences is not a stop either: the 2001st difference still cuts.
    const withTrailingDifference = diffPolicyBodies(
      deepFreeze({ ...before, head: 1 }),
      deepFreeze({ ...after, head: 1, tail: 1 }),
    );
    expect(withTrailingDifference.rows).toHaveLength(MAX_DIFF_ROWS);
    expect(withTrailingDifference.truncated).toBe(true);
  });

  it('caps a set difference only when an element is actually dropped', () => {
    const kept = 'case-kept';
    const removed = caseIds(MAX_DIFF_ROWS);

    // 2000 removals and then an element both sides hold: complete, so nothing is flagged.
    const atCap = diffPolicyBodies(deepFreeze([...removed, kept]), deepFreeze([kept]));
    expect(atCap.rows).toHaveLength(MAX_DIFF_ROWS);
    expect(atCap.truncated).toBe(false);
    expect(atCap.rows.every((row) => row.path === '' && row.kind === 'removed')).toBe(true);

    // The 2001st removal has no room, so that one is refused and the answer says it was cut.
    const overCap = diffPolicyBodies(
      deepFreeze([...removed, 'case-extra', kept]),
      deepFreeze([kept]),
    );
    expect(overCap.rows).toHaveLength(MAX_DIFF_ROWS);
    expect(overCap.truncated).toBe(true);
  });

  it('diffs a body pair at the 64 KiB transport cap inside the time budget', () => {
    const before = deepFreeze({
      required_suites: paddedIds('suite', 400),
      required_cases: paddedIds('case', 600),
      critical_cases: paddedIds('crit', 60),
      allowed_skips: paddedIds('skip', 120),
      required_artifacts: Array.from(
        { length: 40 },
        (_, index) => `artifact_kind_${String(index).padStart(3, '0')}`,
      ),
      quarantine_ids: uuidIds(100),
      flaky_budget: 3,
      collection_timeout_minutes: 60,
      late_window_minutes: 15,
      freshness_minutes: 1440,
      gating_refs: { default_branch: true, release_branch_glob: 'release/*' },
    });
    const after = deepFreeze({
      required_suites: paddedIds('suite', 400),
      required_cases: paddedIds('case', 600, 200),
      critical_cases: paddedIds('crit', 60),
      allowed_skips: paddedIds('skip', 120),
      required_artifacts: Array.from(
        { length: 40 },
        (_, index) => `artifact_kind_${String(index).padStart(3, '0')}`,
      ),
      quarantine_ids: uuidIds(100),
      flaky_budget: 5,
      collection_timeout_minutes: 60,
      late_window_minutes: 15,
      freshness_minutes: 1440,
      gating_refs: { default_branch: false, release_branch_glob: 'release/*' },
    });

    // The pair is in the 64 KiB regime the server accepts (`x-max-bytes`), not a toy body.
    expect(JSON.stringify(before).length).toBeGreaterThan(64000);
    expect(JSON.stringify(before).length).toBeLessThanOrEqual(65536);
    expect(JSON.stringify(after).length).toBeLessThanOrEqual(65536);

    const startedAt = performance.now();
    const diff = diffPolicyBodies(before, after);
    const elapsedMs = performance.now() - startedAt;

    // 200 shifted cases read as 200 removed + 200 added, plus the changed scalar and gating flag.
    expect(diff.rows).toHaveLength(402);
    expect(diff.truncated).toBe(false);
    expect(elapsedMs).toBeLessThan(1000);
  });

  it('stops recursing at the depth cap and compares the deeper pair as one leaf', () => {
    expect(MAX_DIFF_DEPTH).toBe(32);
    const cappedPath = `/${new Array<string>(MAX_DIFF_DEPTH).fill('a').join('/')}`;
    const belowCap = JSON.stringify(nest(8, 1));
    const capped = diffPolicyBodies(deepFreeze(nest(40, 1)), deepFreeze(nest(40, 2)));

    expect(capped).toEqual({
      rows: [
        {
          path: cappedPath,
          kind: 'changed',
          before: belowCap,
          after: JSON.stringify(nest(8, 2)),
        },
      ],
      truncated: false,
    });

    // Equal content below the cap is equal, however deep: no row, no stack growth.
    const deep = diffPolicyBodies(deepFreeze(nest(40, { x: 1 })), deepFreeze(nest(40, { x: 1 })));
    expect(deep).toEqual({ rows: [], truncated: false });

    // A body may nest thousands of levels (`[[[…]]]`): the capped comparison is iterative.
    const veryDeep = diffPolicyBodies(deepFreeze(nest(2000, 1)), deepFreeze(nest(2000, 'x')));
    expect(veryDeep.rows).toHaveLength(1);
    expect(veryDeep.rows[0]?.path).toBe(cappedPath);
    expect(
      diffPolicyBodies(deepFreeze(nest(2000, { x: 1 })), deepFreeze(nest(2000, { x: 1 }))),
    ).toEqual({ rows: [], truncated: false });
  });

  it('clips a rendered value to 200 characters with an ellipsis, never through a character', () => {
    expect(MAX_DIFF_VALUE_CHARS).toBe(200);
    const longAscii = 'x'.repeat(400);
    const longAstral = '😀'.repeat(200);

    const ascii = diffPolicyBodies(
      deepFreeze({ gating_refs: { release_branch_glob: 'a' } }),
      deepFreeze({ gating_refs: { release_branch_glob: longAscii } }),
    );
    const asciiRow = ascii.rows[0];
    expect(asciiRow?.before).toBe('"a"');
    expect(asciiRow?.after?.startsWith('"')).toBe(true);
    expect(asciiRow?.after?.endsWith('\u2026')).toBe(true);
    expect(asciiRow?.after?.length).toBe(MAX_DIFF_VALUE_CHARS);
    expect(hasLoneSurrogate(asciiRow?.after ?? '')).toBe(false);

    const astral = diffPolicyBodies(
      deepFreeze({ gating_refs: { release_branch_glob: 'a' } }),
      deepFreeze({ gating_refs: { release_branch_glob: longAstral } }),
    );
    const astralRow = astral.rows[0]?.after ?? '';
    expect(astralRow.length).toBeLessThanOrEqual(MAX_DIFF_VALUE_CHARS);
    expect(astralRow.endsWith('\u2026')).toBe(true);
    expect(hasLoneSurrogate(astralRow)).toBe(false);
  });

  it('compares a null or non-object root as a single row at the empty pointer', () => {
    expect(diffPolicyBodies(deepFreeze(null), deepFreeze({ flaky_budget: 3 }))).toEqual({
      rows: [{ path: '', kind: 'changed', before: 'null', after: '{"flaky_budget":3}' }],
      truncated: false,
    });
    expect(diffPolicyBodies(deepFreeze('draft'), deepFreeze('active'))).toEqual({
      rows: [{ path: '', kind: 'changed', before: '"draft"', after: '"active"' }],
      truncated: false,
    });
    expect(diffPolicyBodies(deepFreeze(null), deepFreeze(null))).toEqual({
      rows: [],
      truncated: false,
    });
    // An object against an array is not a pair of containers of the same kind.
    expect(diffPolicyBodies(deepFreeze([1]), deepFreeze({ a: 1 })).rows).toEqual([
      { path: '', kind: 'changed', before: '[1]', after: '{"a":1}' },
    ]);
  });
});
