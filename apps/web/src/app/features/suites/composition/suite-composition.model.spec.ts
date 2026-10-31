import type { SuiteEdgeDto } from '@evidgate/sdk';
import draftDocument from '../../../../../../../fixtures/web-api/suite-revision/input.json';
import emptyDocument from '../../../../../../../fixtures/web-api/suite-revision-empty/input.json';
import {
  compositionView,
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

describe('composition view', () => {
  it('turns members into variant columns in ordinal order, one per distinct variant hash', () => {
    const view = compositionView(draft);
    const distinct = draft.members
      .map((member) => member.variant_hash)
      .filter((hash, index, all) => all.indexOf(hash) === index);

    expect(view.columns.map((column) => column.variantHash)).toEqual(distinct);
    expect(view.columns.length).toBe(3);
    const firstColumn = defined(view.columns[0], 'the first variant column');
    const thirdColumn = defined(view.columns[2], 'the third variant column');
    expect(firstColumn.params).toEqual(['browser=chromium', 'locale=vi']);
    expect(thirdColumn.params).toEqual([]);
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
    expect(view.dependencies).toEqual([]);
    expect(view.strategy).toEqual({ kind: 'none' });
  });

  it('resolves both ends of a dependency edge to their members', () => {
    const view = compositionView(draft);
    const served = defined(draft.edges[0], 'a served dependency edge');
    const dependency = defined(view.dependencies[0], 'the first dependency row');

    expect(view.dependencies.length).toBe(draft.edges.length);
    expect(dependency).toEqual({
      parentMemberId: served.parent_member_id,
      parent: draft.members.find((member) => member.id === served.parent_member_id),
      childMemberId: served.child_member_id,
      child: draft.members.find((member) => member.id === served.child_member_id),
    });
  });

  it('keeps the id and a null member when an edge names a member the revision does not hold', () => {
    const served = defined(draft.members[0], 'the first served member');
    const edge: SuiteEdgeDto = {
      parent_member_id: served.id,
      child_member_id: '018f0000-0000-7000-8000-00000000c3ff',
    };
    const view = compositionView({ ...draft, edges: [edge] });
    const dependency = defined(view.dependencies[0], 'the first dependency row');

    expect(dependency.parent).toEqual(served);
    expect(dependency.child).toBeNull();
    expect(dependency.childMemberId).toBe(edge.child_member_id);
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
