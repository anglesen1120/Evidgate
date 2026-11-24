import { signal } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { rowsDuringFetch } from './rows-during-fetch';

describe('rowsDuringFetch', () => {
  it('holds the served rows only until the replacement request settles', () => {
    const served = ['first'];
    const rows = signal<readonly string[]>(served);
    const pending = signal(false);
    const display = rowsDuringFetch(() => 'project', pending, rows);
    expect(display()).toBe(served);
    pending.set(true);
    rows.set([]);
    expect(display()).toBe(served);
    pending.set(false);
    expect(display()).toEqual([]);
  });

  it('does not carry rows across scopes or show a late result after permission loss', () => {
    const scope = signal<string | null>('a');
    const rows = signal<readonly string[]>(['private']);
    const pending = signal(false);
    const display = rowsDuringFetch(scope, pending, rows);
    expect(display()).toEqual(['private']);
    scope.set('b');
    pending.set(true);
    rows.set([]);
    expect(display()).toEqual([]);
    scope.set(null);
    rows.set(['late']);
    pending.set(false);
    expect(display()).toEqual([]);
  });
});
