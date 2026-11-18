import type { SecurityEventList } from '@evidgate/sdk';
import { FIXTURE_CATALOG } from './fixture-catalog';
import { corpusDocument } from './fixture-contract.corpus';

describe('Security-event cursor contract', () => {
  it('continues with distinct older rows and ends at the terminal page', () => {
    const first = corpusDocument('security-events-list').body as SecurityEventList;
    const second = corpusDocument('security-events-list-page-2').body as SecurityEventList;
    const next = FIXTURE_CATALOG.find((entry) => entry.name === 'security-events-list-page-2');
    expect(first.next_cursor).toBe(next?.cursor);
    expect(first.next_cursor).not.toBeNull();
    expect(second.next_cursor).toBeNull();
    expect(first.items.length).toBeGreaterThan(0);
    expect(second.items.length).toBeGreaterThan(0);
    const all = [...first.items, ...second.items];
    expect(new Set(all.map((item) => item.id)).size).toBe(all.length);
    const times = all.map((item) => Date.parse(item.created_at));
    expect(times).toEqual([...times].sort((a, b) => b - a));
  });
});
