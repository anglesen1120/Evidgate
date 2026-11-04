import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { RunsDataService } from '../data/runs-data.service';
import { provideApi } from '../http/api-client';
import { API_BASE_PATH } from '../http/api-origin';
import { provideFixtureBackend } from './fixture-backend';
import { FixtureToggles } from './fixture-toggles';

describe('Fixture run-list decision filtering', () => {
  let service: RunsDataService;
  let toggles: FixtureToggles;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideApi(`${location.origin}${API_BASE_PATH}`),
        provideFixtureBackend(),
      ],
    });
    service = TestBed.inject(RunsDataService);
    toggles = TestBed.inject(FixtureToggles);
    toggles.setVariant('listRuns', 'runs');
  });

  afterEach(() => TestBed.resetTestingModule());

  it.each(['pending', 'pass', 'fail', 'inconclusive'] as const)(
    'filters %s through the data service and SDK without mutating the fixture',
    async (decision) => {
      const all = await service.list('p-1');
      const expected = all.items.filter(
        (row) => row.decision === (decision === 'pending' ? null : decision.toUpperCase()),
      );
      // The corpus carries no INCONCLUSIVE row: that word is a leak-gate needle once a fixture holds it.
      if (decision !== 'inconclusive') expect(expected.length).toBeGreaterThan(0);
      expect(all.items.length).toBeGreaterThan(expected.length);
      const page = await service.list('p-1', { decision });
      expect(page.items).toEqual(expected);
      expect(page.next_cursor).toBe(all.next_cursor);
      expect(await service.list('p-1')).toEqual(all);
    },
  );

  it('preserves a continuation on a short filtered page and applies the filter on its next page', async () => {
    toggles.setVariant('listRuns', 'page-1');
    const all = await service.list('p-1');
    const page = await service.list('p-1', { decision: 'pending' });
    expect(page.next_cursor).toBe(all.next_cursor);
    expect(page.next_cursor).not.toBeNull();
    expect(page.items).toEqual(all.items.filter((row) => row.decision === null));
    if (page.next_cursor === null) throw new Error('The paging fixture must have a continuation');
    const nextAll = await service.list('p-1', { cursor: page.next_cursor });
    const next = await service.list('p-1', { cursor: page.next_cursor, decision: 'pending' });
    expect(next.items).toEqual(nextAll.items.filter((row) => row.decision === null));
    expect(next.next_cursor).toBe(nextAll.next_cursor);
  });

  it('keeps a failed list read an error even with a pending decision filter', async () => {
    toggles.setVariant('listRuns', 'forbidden');
    await expect(service.list('p-1', { decision: 'pending' })).rejects.toMatchObject({
      error: { kind: 'forbidden' },
    });
  });
});
