import { expect, test } from 'vitest';
import {
  browseCapFixturePage,
  findBrowseCapFixture,
  type BrowseCapOperation,
} from './fixture-browse-cap';
import { isRecord, VALIDATOR } from './fixture-contract.support';

for (const operation of ['listCases', 'listManualCases', 'listAuthoringTree'] as const) {
  test(`${operation} cap specimens validate, have distinct rows and continue beyond 10,000`, () => {
    const keys = new Set<string>();
    let rows = 0;
    for (let offset = 0; offset <= 10_000; offset += 200) {
      const entry = browseCapFixturePage(operation, offset, 200);
      const body = entry.document.body;
      expect(VALIDATOR.validateResponse(operation, 200, body).errors).toEqual([]);
      if (!isRecord(body) || !Array.isArray(body['items'])) throw new Error('Missing fixture page.');
      expect(body['items']).toHaveLength(offset === 10_000 ? 1 : 200);
      for (const row of body['items']) {
        if (!isRecord(row)) throw new Error('Missing fixture row.');
        const key = row['id'] ?? row['case_id'] ?? row['node_id'];
        expect(typeof key).toBe('string');
        expect(keys.has(String(key))).toBe(false);
        keys.add(String(key));
      }
      rows += body['items'].length;
      expect(body['next_cursor']).toBe(
        offset === 10_000 ? null : `browse-cap-${operation}-${offset + 200}`,
      );
    }
    expect(rows).toBe(10_001);
    expect(keys.size).toBe(10_001);
    expect(
      VALIDATOR.validateResponse(operation, 200, { items: [{}], next_cursor: null }).errors.length,
    ).toBeGreaterThan(0);
  });
}

test('the cap seam requires its variant and refuses malformed cursors, limits and addressed reads', () => {
  const paths: readonly [string, BrowseCapOperation][] = [
    ['cases', 'listCases'],
    ['authoring/manual-cases', 'listManualCases'],
    ['authoring/tree', 'listAuthoringTree'],
  ];
  for (const [suffix, operation] of paths) {
    const path = `/projects/018f0000-0000-7000-8000-000000000011/${suffix}`;
    const variant = (requested: string) => (requested === operation ? 'browse-cap' : 'ok');
    expect(findBrowseCapFixture('GET', path, `${path}?limit=200`, variant)).toBeDefined();
    expect(findBrowseCapFixture('GET', path, path, () => 'ok')).toBeUndefined();
    expect(findBrowseCapFixture('POST', path, path, variant)).toBeUndefined();
    for (const query of [
      'cursor=unrelated',
      `cursor=browse-cap-${operation}-NaN`,
      `cursor=browse-cap-${operation}--1`,
      `cursor=browse-cap-${operation}-10001`,
      'limit=0',
      'limit=201',
      'node_id=018f0000-0000-7000-8000-000000000101',
    ]) {
      expect(findBrowseCapFixture('GET', path, `${path}?${query}`, variant)).toBeUndefined();
    }
  }
});
