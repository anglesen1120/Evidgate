import {
  ChangeDetectionStrategy,
  Component,
  provideZonelessChangeDetection,
  signal,
} from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { AiSettingsVersion, AiSettingsView } from '@evidgate/sdk';
import { AiSettingsDataService } from '../../../core/data/ai-settings-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { ApiRequestError } from '../../../core/http/api-error';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import headDocument from '../../../../../../../fixtures/web-api/ai-settings-head/input.json';
import versionDocument from '../../../../../../../fixtures/web-api/ai-settings-version-1/input.json';
import { AiSettingsStore } from './ai-settings.store';

@Component({ template: '', changeDetection: ChangeDetectionStrategy.OnPush })
class HistoryHost {}

const version = versionDocument.body as AiSettingsVersion;
const row = (no: number) => ({ ...version, version_no: no });
const unavailable = new ApiRequestError({
  kind: 'unavailable',
  status: 503,
  code: 'unavailable',
  message: 'Unavailable',
  details: [],
  retryable: true,
  requestId: null,
  retryAfterSeconds: null,
});

describe('AiSettingsStore history paging', () => {
  const project = signal<{ id: string } | null>({ id: 'p1' });
  const versions = vi.fn();
  beforeEach(() => {
    project.set({ id: 'p1' });
    versions.mockReset().mockResolvedValue({ items: [row(3)], next_cursor: 'older' });
    TestBed.configureTestingModule({
      imports: [HistoryHost],
      providers: [
        provideZonelessChangeDetection(),
        AiSettingsStore,
        {
          provide: AiSettingsDataService,
          useValue: {
            read: vi
              .fn()
              .mockResolvedValue({ view: headDocument.body as AiSettingsView, etag: '"1"' }),
            versions,
            templates: vi.fn().mockResolvedValue({ items: [], next_cursor: null }),
            version: vi.fn().mockResolvedValue(version),
          },
        },
        {
          provide: SessionStore,
          useValue: {
            currentOrg: signal({ id: 'o1' }),
            currentProject: project,
            has: () => true,
          },
        },
        { provide: NarrowMutationPolicy, useValue: { allowsMutation: () => true } },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());
  async function mount() {
    const fixture = TestBed.createComponent(HistoryHost);
    const store = TestBed.inject(AiSettingsStore);
    await fixture.whenStable();
    return { fixture, store };
  }

  it.each([0, 1])(
    'keeps an empty or one-row terminal page navigable only by size (%s)',
    async (count) => {
      versions.mockResolvedValue({
        items: Array.from({ length: count }, () => row(1)),
        next_cursor: null,
      });
      const { fixture, store } = await mount();
      expect(store.historyPage()?.items).toHaveLength(count);
      expect(store.page()).toBe(1);
      expect(store.pageSize()).toBe(20);
      store.previousPage();
      store.nextPage();
      await fixture.whenStable();
      expect(versions).toHaveBeenCalledTimes(1);
      expect(versions).toHaveBeenLastCalledWith('p1', { limit: 20 }, expect.any(AbortSignal));
    },
  );

  it('replaces first, middle and last pages and re-fetches Previous using its original cursor', async () => {
    versions.mockImplementation((_project: string, query: { cursor?: string }) =>
      Promise.resolve(
        query.cursor === 'last'
          ? { items: [row(1)], next_cursor: null }
          : query.cursor === 'older'
            ? { items: [row(2)], next_cursor: 'last' }
            : { items: [row(3)], next_cursor: 'older' },
      ),
    );
    const { fixture, store } = await mount();
    store.nextPage();
    await fixture.whenStable();
    expect(store.page()).toBe(2);
    expect(store.historyPage()?.items.map((entry) => entry.version_no)).toEqual([2]);
    store.nextPage();
    await fixture.whenStable();
    expect(store.page()).toBe(3);
    expect(store.historyPage()?.next_cursor).toBeNull();
    store.previousPage();
    await fixture.whenStable();
    expect(store.page()).toBe(2);
    expect(versions).toHaveBeenLastCalledWith(
      'p1',
      { limit: 20, cursor: 'older' },
      expect.any(AbortSignal),
    );
    store.previousPage();
    await fixture.whenStable();
    expect(store.page()).toBe(1);
    expect(store.historyPage()?.items.map((entry) => entry.version_no)).toEqual([3]);
  });

  it.each([50, 100])(
    'resets the cursor for selected size %s and rejects unsupported sizes',
    async (size) => {
      const { fixture, store } = await mount();
      store.nextPage();
      await fixture.whenStable();
      store.setPageSize(size);
      await fixture.whenStable();
      expect(store.page()).toBe(1);
      expect(store.cursorPath()).toEqual([]);
      expect(versions).toHaveBeenLastCalledWith('p1', { limit: size }, expect.any(AbortSignal));
      const calls = versions.mock.calls.length;
      store.setPageSize(25);
      store.setPageSize(size);
      await fixture.whenStable();
      expect(versions).toHaveBeenCalledTimes(calls);
    },
  );

  it('retains the displayed page on failure and retries the requested cursor without losing selection', async () => {
    versions
      .mockResolvedValueOnce({ items: [row(3)], next_cursor: 'older' })
      .mockRejectedValueOnce(unavailable)
      .mockResolvedValueOnce({ items: [row(2)], next_cursor: null });
    const { fixture, store } = await mount();
    store.selectVersion(3);
    store.nextPage();
    await fixture.whenStable();
    expect(store.page()).toBe(1);
    expect(store.historyPage()?.items.map((entry) => entry.version_no)).toEqual([3]);
    expect(store.history.error()).toBe(unavailable);
    store.history.reload();
    await fixture.whenStable();
    expect(store.page()).toBe(2);
    expect(store.historyPage()?.items.map((entry) => entry.version_no)).toEqual([2]);
    expect(store.selectedVersion()).toBe(3);
    expect(versions).toHaveBeenLastCalledWith(
      'p1',
      { limit: 20, cursor: 'older' },
      expect.any(AbortSignal),
    );
  });

  it('refuses a repeated cursor and clears refusal when returning to the first page', async () => {
    const { fixture, store } = await mount();
    store.nextPage();
    await fixture.whenStable();
    expect(store.cursorCycle()).toBe(true);
    const calls = versions.mock.calls.length;
    store.nextPage();
    await fixture.whenStable();
    expect(versions).toHaveBeenCalledTimes(calls);
    store.firstPage();
    await fixture.whenStable();
    expect(store.page()).toBe(1);
    expect(store.cursorCycle()).toBe(false);
  });

  it('clears the cursor, size and selection on project change without retaining foreign rows', async () => {
    const { fixture, store } = await mount();
    store.setPageSize(50);
    await fixture.whenStable();
    store.nextPage();
    await fixture.whenStable();
    store.selectVersion(3);
    versions.mockResolvedValue({ items: [], next_cursor: null });
    project.set({ id: 'p2' });
    await fixture.whenStable();
    expect(store.page()).toBe(1);
    expect(store.pageSize()).toBe(20);
    expect(store.historyPage()?.items).toEqual([]);
    expect(store.selectedVersion()).toBe(0);
    expect(versions).toHaveBeenLastCalledWith('p2', { limit: 20 }, expect.any(AbortSignal));
  });
  it.each(['forbidden', 'not_found', 'unauthenticated'] as const)(
    'does not retain visible history after a %s response',
    async (kind) => {
      const { fixture, store } = await mount();
      expect(store.historyPage()?.items).toHaveLength(1);
      versions.mockRejectedValueOnce(
        new ApiRequestError({
          ...unavailable.error,
          kind,
          code: kind,
          status: kind === 'forbidden' ? 403 : kind === 'not_found' ? 404 : 401,
          retryable: false,
        }),
      );
      store.nextPage();
      await fixture.whenStable();
      expect(store.historyPage()).toBeUndefined();
    },
  );
});
