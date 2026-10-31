import type { SuiteEdgeDto, SuiteMemberDto } from '@evidgate/sdk';
import draftDocument from '../../../../../../../fixtures/web-api/suite-revision/input.json';
import emptyDocument from '../../../../../../../fixtures/web-api/suite-revision-empty/input.json';
import {
  compositionView,
  MATRIX_CELL_LIMIT,
  strategySummary,
  variantParameters,
  type CompositionSource,
} from './suite-composition.model';

// The served documents themselves, so the view is tested against the server's shape rather than a
// hand-typed echo of it.
const draft: CompositionSource = draftDocument.body;
const empty: CompositionSource = emptyDocument.body;

/**
 * A served entry the assertion needs, or a failed test. The fixtures are the wire shape, so a
 * missing entry means this spec is out of date, never that the view may read `undefined`: failing
 * here names the entry instead of letting an assertion compare against nothing.
 */
function defined<T>(value: T | undefined, what: string): T {
  if (value === undefined) throw new Error(`The served fixture no longer holds ${what}`);
  return value;
}

/** A revision whose members fill a rows × columns grid, one member per cell. */
function grid(rows: number, columns: number): CompositionSource {
  const members: SuiteMemberDto[] = [];
  for (let row = 0; row < rows; row += 1) {
    for (let column = 0; column < columns; column += 1) {
      members.push({
        id: `member-${row}-${column}`,
        case_revision_id: `case-${String(row).padStart(3, '0')}`,
        variant_hash: `variant-${column}`,
        params: { column },
        required: column % 2 === 0,
        ordinal: column,
      });
    }
  }
  return { members, edges: [], shard_strategy: { kind: 'none' } };
}

describe('composition view', () => {
  it('orders columns by member arrival, not by variant hash', () => {
    // Reordering the fixture is what makes this an ordering test: the served order would agree
    // with a sorted column list, so only arrived-out-of-hash-order members can tell them apart.
    const byOrdinal = (ordinal: number) =>
      defined(
        draft.members.find((member) => member.ordinal === ordinal),
        `the member at ordinal ${ordinal}`,
      );
    const c3 = byOrdinal(3);
    const a1First = byOrdinal(0);
    const a1Second = byOrdinal(1);
    const b2 = byOrdinal(2);
    const arrival = [c3, a1First, a1Second, b2];
    const view = compositionView({ ...draft, members: arrival });
    const hashes = view.columns.map((column) => column.variantHash);

    expect(hashes).toEqual([c3.variant_hash, a1First.variant_hash, b2.variant_hash]);
    expect(view.columns.length).toBe(3);
    expect(hashes).not.toEqual([...new Set(arrival.map((member) => member.variant_hash))].sort());
    // The column carries the parameters of the first member that arrived under its hash.
    expect(defined(view.columns[1], 'the second variant column').params).toEqual([
      'browser=chromium',
      'locale=vi',
    ]);
    expect(defined(view.columns[0], 'the first variant column').params).toEqual([]);
  });

  it('groups one row per case revision, sorted, with the served member in its cell', () => {
    const view = compositionView(draft);
    const caseIds = view.rows.map((row) => row.caseRevisionId);

    expect(caseIds).toEqual([...caseIds].sort());
    expect(view.rows.length).toBe(3);
    const first = defined(view.rows[0], 'the first matrix row');
    const third = defined(view.rows[2], 'the third matrix row');
    const firstColumn = defined(view.columns[0], 'the first variant column');
    expect(first.byVariant.get(firstColumn.variantHash)?.ordinal).toBe(0);
    expect(first.byVariant.get(firstColumn.variantHash)?.required).toBe(true);
    // A case revision with no member in a variant has an empty cell, never a fabricated one.
    expect(third.byVariant.get(firstColumn.variantHash)).toBeUndefined();
    const thirdColumn = defined(view.columns[2], 'the third variant column');
    expect(third.byVariant.get(thirdColumn.variantHash)?.required).toBe(false);
  });

  it('keeps the served ordinal and required flag of every member of one row', () => {
    const view = compositionView(draft);
    const row = defined(view.rows[1], 'the second matrix row');
    const members = [...row.byVariant.values()].sort((left, right) => left.ordinal - right.ordinal);

    expect(members.map((member) => member.ordinal)).toEqual([1, 2]);
    expect(members.map((member) => member.required)).toEqual([true, true]);
    expect(new Set(members.map((member) => member.id)).size).toBe(2);
  });

  it('renders no rows, no columns and no dependencies for a revision with no members', () => {
    const view = compositionView(empty);

    expect(view.rows).toEqual([]);
    expect(view.columns).toEqual([]);
    expect(view.layout).toBe('matrix');
    expect(view.flatMembers).toEqual([]);
    expect(view.dependencies).toEqual([]);
    expect(view.strategy).toEqual({ kind: 'none' });
  });

  it('keeps the served parent and child ids of every edge, in arrival order', () => {
    const view = compositionView(draft);

    expect(view.dependencies).toEqual(
      draft.edges.map((edge) => ({
        parentMemberId: edge.parent_member_id,
        childMemberId: edge.child_member_id,
      })),
    );
    expect(view.dependencies.length).toBeGreaterThan(0);
  });

  it('keeps both ids of an edge naming a member the revision does not hold', () => {
    const served = defined(draft.members[0], 'the first served member');
    const edge: SuiteEdgeDto = {
      parent_member_id: served.id,
      child_member_id: '018f0000-0000-7000-8000-00000000c3ff',
    };
    const view = compositionView({ ...draft, edges: [edge] });

    expect(view.dependencies).toEqual([
      { parentMemberId: edge.parent_member_id, childMemberId: edge.child_member_id },
    ]);
  });
});

describe('member layout', () => {
  it('keeps the matrix below the cell limit and builds no flat rows', () => {
    const view = compositionView(grid(3, 3));

    expect(view.layout).toBe('matrix');
    expect(view.rows).toHaveLength(3);
    expect(view.columns).toHaveLength(3);
    expect(view.flatMembers).toEqual([]);
  });

  it('keeps the matrix at exactly the cell limit and goes flat one cell past it', () => {
    // 50 × 50 fills the limit; 61 × 41 is one cell over, so the product and not the member count
    // is what decides.
    expect(compositionView(grid(50, 50)).layout).toBe('matrix');
    const overLimit = compositionView(grid(61, 41));

    expect(overLimit.layout).toBe('flat');
    expect(overLimit.flatMembers).toHaveLength(61 * 41);
  });

  it('lists every member of an oversized grid flat, in arrival order, with its served fields', () => {
    const view = compositionView(grid(60, 60));

    expect(MATRIX_CELL_LIMIT).toBe(2500);
    expect(view.layout).toBe('flat');
    expect(view.flatMembers).toHaveLength(3600);
    expect(defined(view.flatMembers[0], 'the first flat row')).toEqual({
      memberId: 'member-0-0',
      caseRevisionId: 'case-000',
      params: ['column=0'],
      required: true,
      ordinal: 0,
    });
    expect(view.flatMembers.at(-1)?.memberId).toBe('member-59-59');
  });
});

describe('shard strategy summary', () => {
  it('reports the served count and balance of the draft revision', () => {
    expect(compositionView(draft).strategy).toEqual({
      kind: 'count',
      count: 2,
      balanceBy: 'duration',
    });
  });

  it('reports none for a document declaring no sharding, ignoring count and balance', () => {
    expect(strategySummary({ kind: 'none' })).toEqual({ kind: 'none' });
    expect(strategySummary({ kind: 'none', count: 4, balance_by: 'duration' })).toEqual({
      kind: 'none',
    });
  });

  it('reports a count without declared balancing as count with no balance', () => {
    expect(strategySummary({ kind: 'count', count: 4 })).toEqual({
      kind: 'count',
      count: 4,
      balanceBy: null,
    });
    expect(strategySummary({ kind: 'count', count: 4, balance_by: 'count' })).toEqual({
      kind: 'count',
      count: 4,
      balanceBy: 'count',
    });
  });

  it.each([1, 256])('accepts the schema boundary shard count %i', (count) => {
    // The schema allows 1 to 256 inclusive, so both ends must be a count and not unavailable.
    expect(strategySummary({ kind: 'count', count, balance_by: 'duration' })).toEqual({
      kind: 'count',
      count,
      balanceBy: 'duration',
    });
    expect(strategySummary({ kind: 'count', count, balance_by: 'count' })).toEqual({
      kind: 'count',
      count,
      balanceBy: 'count',
    });
    expect(strategySummary({ kind: 'count', count })).toEqual({
      kind: 'count',
      count,
      balanceBy: null,
    });
  });

  it('reports a missing strategy document as unavailable instead of throwing', () => {
    // `compositionView` hands the served document straight to this function, so the guard here is
    // what keeps a `null` or absent `shard_strategy` from throwing anywhere on the screen.
    expect(strategySummary(null)).toEqual({ kind: 'unavailable' });
    expect(strategySummary(undefined)).toEqual({ kind: 'unavailable' });
  });

  it('reports unavailable for every document it cannot read as the schema defines', () => {
    const unreadable: Record<string, unknown>[] = [
      {},
      { kind: '' },
      { kind: 'shard' },
      { kind: 'count' },
      { kind: 'count', count: 0 },
      { kind: 'count', count: 257 },
      { kind: 'count', count: 1.5 },
      { kind: 'count', count: '4' },
      { kind: 'count', count: 2, balance_by: 'weight' },
    ];

    expect(unreadable.map((document) => strategySummary(document))).toEqual(
      unreadable.map(() => ({ kind: 'unavailable' })),
    );
  });
});

describe('variant parameters', () => {
  it('renders a flat scalar document as sorted key=value text', () => {
    expect(
      variantParameters({ locale: 'vi', browser: 'chromium', touch: true, width: 360 }),
    ).toEqual(['browser=chromium', 'locale=vi', 'touch=true', 'width=360']);
  });

  it('renders a value the schema does not allow as its JSON text instead of hiding it', () => {
    expect(variantParameters({ nested: { depth: 1 } })).toEqual(['nested={"depth":1}']);
  });

  it('renders no parameters for a document that is not an object', () => {
    const notDocuments = [null, undefined, 'browser=chromium', ['browser'], 7];

    expect(notDocuments.map((value) => variantParameters(value))).toEqual(
      notDocuments.map(() => []),
    );
  });
});
