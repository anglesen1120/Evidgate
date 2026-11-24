import { ComponentHarness, manualChangeDetection } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type {
  AiSettingsVersion,
  AiSettingsView,
  ListProjectAiSettingsVersionsResponse,
} from '@evidgate/sdk';
import { firstValueFrom, Subject } from 'rxjs';
import { AiSettingsDataService } from '../../../core/data/ai-settings-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { apiError } from '../../../shared/testing/http-data.testing';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import headDocument from '../../../../../../../fixtures/web-api/ai-settings-head/input.json';
import versionDocument from '../../../../../../../fixtures/web-api/ai-settings-version-1/input.json';
import { AiSettingsHistoryComponent } from './ai-settings-history.component';
import { AiSettingsStore } from './ai-settings.store';

class HistoryHarness extends ComponentHarness {
  static hostSelector = 'app-ai-settings-history';
  async filter(value: string) {
    const field = await this.locatorFor('#ai-history-filter')();
    await field.setInputValue(value);
    await field.dispatchEvent('input');
  }
  async ids() {
    return Promise.all(
      (await this.locatorForAll('[data-row-id]')()).map((row) => row.getAttribute('data-row-id')),
    );
  }
}

provideLayoutTestStubs();
describe('AiSettingsHistoryComponent filter paging', () => {
  const version = versionDocument.body as AiSettingsVersion;
  const row = (no: number, reason: string) => ({ ...version, version_no: no, reason });
  const versions = vi.fn<AiSettingsDataService['versions']>();
  beforeEach(() => {
    versions
      .mockReset()
      .mockResolvedValue({ items: [row(3, 'Reason latest')], next_cursor: 'older' });
    TestBed.configureTestingModule({
      imports: [AiSettingsHistoryComponent],
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
            currentProject: signal({ id: 'p1' }),
            has: () => true,
          },
        },
        {
          provide: NarrowMutationPolicy,
          useValue: { canMutate: signal(true), narrow: signal(false), allowsMutation: () => true },
        },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());

  it.each(['success', 'retry'] as const)(
    'keeps one first-page request while typing and applies the latest filter on %s without losing selection',
    async (outcome) => {
      const fixture = TestBed.createComponent(AiSettingsHistoryComponent);
      const store = TestBed.inject(AiSettingsStore);
      await fixture.whenStable();
      const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, HistoryHarness);
      versions.mockResolvedValueOnce({ items: [row(2, 'Older')], next_cursor: null });
      store.nextPage();
      store.selectVersion(3);
      await fixture.whenStable();
      const pending = new Subject<ListProjectAiSettingsVersionsResponse>();
      versions.mockReturnValueOnce(firstValueFrom(pending));
      await manualChangeDetection(async () => {
        for (const value of ['R', 'Re', 'Reason latest']) {
          await harness.filter(value);
          fixture.detectChanges();
          await vi.waitFor(() => expect(versions).toHaveBeenCalledTimes(3));
        }
        expect(store.page()).toBe(2);
        expect(store.historyPage()?.items[0]?.version_no).toBe(2);
        expect(store.selectedVersion()).toBe(3);
        expect(versions.mock.lastCall?.[1]).toEqual({ limit: 20 });
        if (outcome === 'retry') pending.error(apiError('unavailable', 503));
        else
          pending.next({
            items: [row(3, 'Reason latest'), row(1, 'Reason other')],
            next_cursor: null,
          });
      });
      await fixture.whenStable();
      if (outcome === 'retry') {
        expect(store.page()).toBe(2);
        versions.mockResolvedValueOnce({
          items: [row(3, 'Reason latest'), row(1, 'Reason other')],
          next_cursor: null,
        });
        store.history.reload();
        await fixture.whenStable();
      }
      expect(store.page()).toBe(1);
      expect(await harness.ids()).toEqual(['3']);
      expect(store.selectedVersion()).toBe(3);
      expect(versions).toHaveBeenCalledTimes(outcome === 'retry' ? 4 : 3);
      store.history.reload();
      await fixture.whenStable();
      expect(versions).toHaveBeenCalledTimes(outcome === 'retry' ? 5 : 4);
    },
  );
});
