import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import runList250 from '../../../../../../fixtures/web-api/run-list-250/input.json';
import fullPage from '../../../../../../fixtures/web-api/run-list-page-1-full/input.json';
import lastPage from '../../../../../../fixtures/web-api/run-list-page-2/input.json';
import { AuditDataService } from '../data/audit-data.service';
import { RunsDataService } from '../data/runs-data.service';
import { provideApi } from '../http/api-client';
import { API_BASE_PATH } from '../http/api-origin';
import { provideFixtureBackend } from './fixture-backend';
import { FixtureToggles } from './fixture-toggles';

describe('Fixture cursor paging through the generated SDK', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideApi(`${location.origin}${API_BASE_PATH}`),
        provideFixtureBackend(),
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());

  it.each([20, 50, 100])('serves the 250-run fixture in requested pages of %s', async (limit) => {
    TestBed.inject(FixtureToggles).setVariant('listRuns', '250-rows');
    const service = TestBed.inject(RunsDataService);
    const first = await service.list('p-1', { limit });
    expect(first.items).toEqual(runList250.body.items.slice(0, limit));
    expect(first.next_cursor).toEqual(expect.any(String));
    if (!first.next_cursor) throw new Error('Expected continuation');
    const next = await service.list('p-1', { limit, cursor: first.next_cursor });
    expect(next.items).toEqual(runList250.body.items.slice(limit, limit * 2));
    expect(await service.list('p-1', { limit })).toEqual(first);
  });

  for (const operation of ['listRuns', 'listAudit'] as const) {
    const list = (limit: number, cursor?: string) =>
      operation === 'listRuns'
        ? TestBed.inject(RunsDataService).list('p-1', { limit, cursor })
        : TestBed.inject(AuditDataService).list('o-1', { limit }, cursor);

    it(`${operation} serves 20 then 13 rows and terminates`, async () => {
      TestBed.inject(FixtureToggles).setVariant(operation, 'paging-33');
      const first = await list(20);
      expect(first.items).toHaveLength(20);
      if (!first.next_cursor) throw new Error('Expected continuation');
      const last = await list(20, first.next_cursor);
      expect(last.items).toHaveLength(13);
      expect(last.next_cursor).toBeNull();
      expect(await list(20)).toEqual(first);
      expect((await list(50)).items).toHaveLength(33);
      expect((await list(100)).next_cursor).toBeNull();
      await expect(list(20, 'not-a-fixture-cursor')).rejects.toBeDefined();
      await expect(list(20, 'fixture-page.' + btoa('{}'))).rejects.toMatchObject({
        error: { status: 400 },
      });
      await expect(list(20, 'fixture-page.' + 'x'.repeat(513))).rejects.toMatchObject({
        error: { status: 400 },
      });
    });

    it(`${operation} fails the later page once, then serves that same cursor`, async () => {
      TestBed.inject(FixtureToggles).setVariant(operation, 'paging-33-retry');
      const first = await list(20);
      if (!first.next_cursor) throw new Error('Expected continuation');
      await expect(list(20, first.next_cursor)).rejects.toMatchObject({
        error: { kind: 'unavailable' },
      });
      const retried = await list(20, first.next_cursor);
      expect(retried.items).toHaveLength(13);
      expect(retried.next_cursor).toBeNull();
      expect((await list(20)).items).toEqual(first.items);
    });
  }

  it('filters run decisions before sizing pages and never changes a refusal into success', async () => {
    const toggles = TestBed.inject(FixtureToggles);
    const service = TestBed.inject(RunsDataService);
    toggles.setVariant('listRuns', '250-rows');
    const page = await service.list('p-1', { limit: 20, decision: 'pending' });
    const expected = runList250.body.items.filter((row) => row.decision === null);
    expect(page.items).toEqual(expected.slice(0, 20));
    toggles.setVariant('listRuns', 'forbidden');
    await expect(service.list('p-1', { limit: 20, decision: 'pending' })).rejects.toMatchObject({
      error: { kind: 'forbidden' },
    });
  });

  it('continues from a split corpus page to its original opaque continuation', async () => {
    TestBed.inject(FixtureToggles).setVariant('listRuns', 'page-1-full');
    const service = TestBed.inject(RunsDataService);
    const ids: string[] = [];
    let cursor: string | undefined;
    for (let turn = 0; turn < 4; turn++) {
      const page = await service.list('p-1', { limit: 20, cursor });
      expect(page.items.length).toBeLessThanOrEqual(20);
      ids.push(...page.items.map((row) => row.run_id));
      cursor = page.next_cursor ?? undefined;
      if (cursor === undefined) break;
    }
    expect(cursor).toBeUndefined();
    expect(ids).toEqual([...fullPage.body.items, ...lastPage.body.items].map((row) => row.run_id));
  });

  it('keeps computed audit filters on the SDK path before paging', async () => {
    TestBed.inject(FixtureToggles).setVariant('listAudit', 'paging-33');
    const service = TestBed.inject(AuditDataService);
    const first = await service.list('o-1', { limit: 100 });
    const action = first.items[0]?.action;
    if (!action) throw new Error('The audit specimen needs an action');
    const filtered = await service.list('o-1', { limit: 20, action });
    expect(filtered.items).toEqual(first.items.filter((row) => row.action === action).slice(0, 20));
    expect(filtered.items.length).toBeGreaterThan(0);
    expect(filtered.items.length).toBeLessThan(first.items.length);
  });
});
