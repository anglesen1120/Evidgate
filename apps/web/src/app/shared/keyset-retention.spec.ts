import { TestBed } from '@angular/core/testing';
import { DEFAULT_RETAINED_ROWS_CAP, RETAINED_ROWS_CAP, retainKeysetRows } from './keyset-retention';

describe('retained keyset rows', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('defaults the injectable retained-row cap to 10,000', () => {
    expect(DEFAULT_RETAINED_ROWS_CAP).toBe(10_000);
    expect(TestBed.inject(RETAINED_ROWS_CAP)).toBe(10_000);
  });

  it('uses an overridden cap without changing the terminal-page boundary', () => {
    TestBed.configureTestingModule({
      providers: [{ provide: RETAINED_ROWS_CAP, useValue: 2 }],
    });
    const cap = TestBed.inject(RETAINED_ROWS_CAP);
    expect(retainKeysetRows(['first', 'second'], null, cap).capped).toBe(false);
    expect(retainKeysetRows(['first', 'second'], 'next', cap)).toEqual({
      rows: ['first', 'second'],
      nextCursor: null,
      capped: true,
    });
    expect(retainKeysetRows(['first', 'second', 'third'], null, cap).rows).toEqual([
      'first',
      'second',
    ]);
  });
  it('retains the original array below the cap without a copy', () => {
    const rows = ['first'];
    const result = retainKeysetRows(rows, 'next', TestBed.inject(RETAINED_ROWS_CAP));
    expect(result).toEqual({ rows, nextCursor: 'next', capped: false });
    expect(result.rows).toBe(rows);
  });

  it('does not mark an exactly complete cap-sized result as capped', () => {
    const cap = TestBed.inject(RETAINED_ROWS_CAP);
    const rows = Array.from({ length: cap }, (_, index) => index);
    const result = retainKeysetRows(rows, null, cap);
    expect(result.capped).toBe(false);
    expect(result.nextCursor).toBeNull();
    expect(result.rows).toBe(rows);
  });

  it('ends an exactly cap-sized walk that advertises another page', () => {
    const cap = TestBed.inject(RETAINED_ROWS_CAP);
    const rows = Array.from({ length: cap }, (_, index) => index);
    const result = retainKeysetRows(rows, 'beyond-cap', cap);
    expect(result.capped).toBe(true);
    expect(result.nextCursor).toBeNull();
    expect(result.rows).toBe(rows);
  });

  it('retains only the first cap rows when one extra terminal row arrives', () => {
    const cap = TestBed.inject(RETAINED_ROWS_CAP);
    const rows = Array.from({ length: cap + 1 }, (_, index) => index);
    const result = retainKeysetRows(rows, null, cap);
    expect(result.capped).toBe(true);
    expect(result.nextCursor).toBeNull();
    expect(result.rows).toHaveLength(cap);
    expect(result.rows.at(-1)).toBe(cap - 1);
  });
});
