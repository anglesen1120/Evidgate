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
class HistoryQaHost {}
const version = versionDocument.body as AiSettingsVersion;
const page = (no: number, next: string | null) => ({
  items: [{ ...version, version_no: no }],
  next_cursor: next,
});
const denied = (kind: 'forbidden' | 'not_found' | 'unauthenticated') =>
  new ApiRequestError({
    kind,
    status: kind === 'forbidden' ? 403 : kind === 'not_found' ? 404 : 401,
    code: kind,
    message: kind,
    details: [],
    retryable: false,
    requestId: null,
    retryAfterSeconds: null,
  });

describe('AI history denial and reset boundaries', () => {
  const versions = vi.fn<AiSettingsDataService['versions']>();
  beforeEach(() => {
    versions.mockReset().mockResolvedValue(page(3, 'older'));
    TestBed.configureTestingModule({
      imports: [HistoryQaHost],
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
            currentOrg: signal({ id: 'org' }),
            currentProject: signal({ id: 'project' }),
            has: () => true,
          },
        },
        { provide: NarrowMutationPolicy, useValue: { allowsMutation: () => true } },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());

  it.each(['forbidden', 'not_found', 'unauthenticated'] as const)(
    'drops the retained page after %s on Previous and recovers at that exact cursor',
    async (kind) => {
      const fixture = TestBed.createComponent(HistoryQaHost);
      const store = TestBed.inject(AiSettingsStore);
      await fixture.whenStable();
      versions.mockResolvedValueOnce(page(2, null));
      store.nextPage();
      await fixture.whenStable();
      expect(store.page()).toBe(2);
      versions.mockRejectedValueOnce(denied(kind));
      store.previousPage();
      await fixture.whenStable();
      expect(store.historyPage()).toBeUndefined();
      expect(store.history.error()).toBeInstanceOf(ApiRequestError);
      versions.mockResolvedValueOnce(page(3, 'older'));
      store.history.reload();
      await fixture.whenStable();
      expect(versions).toHaveBeenLastCalledWith('project', { limit: 20 }, expect.any(AbortSignal));
      expect(store.page()).toBe(1);
      expect(store.historyPage()?.items[0]?.version_no).toBe(3);
    },
  );

  it('never falls back to a page from the old size when the new-size read fails', async () => {
    const fixture = TestBed.createComponent(HistoryQaHost);
    const store = TestBed.inject(AiSettingsStore);
    await fixture.whenStable();
    expect(store.historyPage()?.items).toHaveLength(1);
    versions.mockRejectedValueOnce(new Error('transport stopped'));
    store.setPageSize(100);
    await fixture.whenStable();
    expect(store.historyPage()).toBeUndefined();
    expect(store.pageSize()).toBe(100);
    versions.mockResolvedValueOnce(page(1, null));
    store.history.reload();
    await fixture.whenStable();
    expect(versions).toHaveBeenLastCalledWith('project', { limit: 100 }, expect.any(AbortSignal));
    expect(store.page()).toBe(1);
    expect(store.cursorPath()).toEqual([]);
  });
});
