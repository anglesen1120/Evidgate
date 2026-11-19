import { CASE_ROWS_CAP, retainKeysetRows } from './keyset-retention';

describe('retained keyset rows', () => {
  it('retains the original array below the cap without a copy', () => {
    const rows = ['first'];
    const result = retainKeysetRows(rows, 'next');
    expect(result).toEqual({ rows, nextCursor: 'next', capped: false });
    expect(result.rows).toBe(rows);
  });

  it('does not mark an exactly complete cap-sized result as capped', () => {
    const rows = Array.from({ length: CASE_ROWS_CAP }, (_, index) => index);
    const result = retainKeysetRows(rows, null);
    expect(result.capped).toBe(false);
    expect(result.nextCursor).toBeNull();
    expect(result.rows).toBe(rows);
  });

  it('ends an exactly cap-sized walk that advertises another page', () => {
    const rows = Array.from({ length: CASE_ROWS_CAP }, (_, index) => index);
    const result = retainKeysetRows(rows, 'beyond-cap');
    expect(result.capped).toBe(true);
    expect(result.nextCursor).toBeNull();
    expect(result.rows).toBe(rows);
  });

  it('retains only the first cap rows when one extra terminal row arrives', () => {
    const rows = Array.from({ length: CASE_ROWS_CAP + 1 }, (_, index) => index);
    const result = retainKeysetRows(rows, null);
    expect(result.capped).toBe(true);
    expect(result.nextCursor).toBeNull();
    expect(result.rows).toHaveLength(CASE_ROWS_CAP);
    expect(result.rows.at(-1)).toBe(CASE_ROWS_CAP - 1);
  });
});
