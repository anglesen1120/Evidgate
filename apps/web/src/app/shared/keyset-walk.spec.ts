import type { ApiError } from '../core/http/api-error';
import { takeKeysetPage, type KeysetStep } from './keyset-walk';

interface Row {
  readonly id: string;
}

const CURSOR_REPEATED: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: 'test_cursor_repeated',
  message: 'The rows list returned the same page cursor again.',
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};
const PAGE_WITHOUT_NEW_ROWS: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: 'test_page_without_new_rows',
  message: 'The rows list returned a follow-up page with no new rows.',
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};

function row(id: string): Row {
  return { id };
}

function step(
  cursor: string | null,
  items: readonly Row[],
  nextCursor: string | null,
  seen: ReadonlySet<string> = new Set(),
): KeysetStep<Row, string> {
  return takeKeysetPage({
    cursor,
    items,
    nextCursor,
    seen,
    keyOf: (item) => item.id,
    refusals: { cursorRepeated: CURSOR_REPEATED, pageWithoutNewRows: PAGE_WITHOUT_NEW_ROWS },
  });
}

function advanced(
  result: KeysetStep<Row, string>,
): Extract<KeysetStep<Row, string>, { outcome: 'advanced' }> {
  if (result.outcome !== 'advanced') throw new Error(`refused with ${result.error.code}`);
  return result;
}

function refused(result: KeysetStep<Row, string>): ApiError {
  if (result.outcome !== 'refused') throw new Error(`advanced past ${result.nextCursor}`);
  return result.error;
}

describe('keyset walk step', () => {
  it('treats an empty page as the ordinary end of the walk, whatever the cursor state', () => {
    const ended = advanced(step(null, [], null));
    expect(ended.fresh).toEqual([]);
    expect(ended.nextCursor).toBeNull();
    expect(ended.seen.size).toBe(0);
    // A first page that promises another is not a refusal: nothing has been walked yet.
    expect(advanced(step(null, [], 'c1')).nextCursor).toBe('c1');
  });

  it('returns a single page in server order and records the keys it showed', () => {
    const single = advanced(step(null, [row('a'), row('b')], null));
    expect(single.fresh).toEqual([row('a'), row('b')]);
    expect(single.nextCursor).toBeNull();
    expect([...single.seen]).toEqual(['a', 'b']);
  });

  it('walks several pages, appending only rows the earlier pages did not show', () => {
    const first = advanced(step(null, [row('a'), row('b')], 'c1'));
    const second = advanced(step('c1', [row('b'), row('c'), row('c')], 'c2', first.seen));
    expect(second.fresh).toEqual([row('c')]);
    expect(second.nextCursor).toBe('c2');
    expect([...second.seen]).toEqual(['a', 'b', 'c']);
    expect([...first.seen]).toEqual(['a', 'b']);
  });

  it('starts the walk over on a first page instead of reusing the caller seen set', () => {
    const restarted = advanced(step(null, [row('a')], null, new Set(['a', 'b'])));
    expect(restarted.fresh).toEqual([row('a')]);
    expect([...restarted.seen]).toEqual(['a']);
  });

  it('refuses the cursor it was given back, with the region error object itself', () => {
    expect(refused(step('c1', [row('a')], 'c1', new Set(['a'])))).toBe(CURSOR_REPEATED);
    // A terminal page may repeat the row set; only a cursor that repeats itself closes the loop.
    expect(advanced(step('c1', [row('a')], null, new Set(['a']))).nextCursor).toBeNull();
  });

  it('refuses a follow-up page that adds no fresh row while promising another page', () => {
    const seen = new Set(['a', 'b']);
    expect(refused(step('c1', [row('a'), row('b')], 'c2', seen))).toBe(PAGE_WITHOUT_NEW_ROWS);
    // The refusal holds no state: the caller's seen set is what the walk had before the page.
    expect([...seen]).toEqual(['a', 'b']);
  });

  it('ends a follow-up page with no fresh row when no further page is promised', () => {
    const ended = advanced(step('c1', [], null, new Set(['a'])));
    expect(ended.fresh).toEqual([]);
    expect(ended.nextCursor).toBeNull();
    expect([...ended.seen]).toEqual(['a']);
  });
});
