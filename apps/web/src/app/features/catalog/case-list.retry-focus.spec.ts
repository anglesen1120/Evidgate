import { manualChangeDetection } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import type { CaseListResponse } from '@evidgate/sdk';
import { BehaviorSubject, firstValueFrom, Subject } from 'rxjs';
import { CaseListDataService } from '../../core/data/case-list-data.service';
import { ApiRequestError } from '../../core/http/api-error';
import { SessionStore } from '../../core/session/session.store';
import { provideLayoutTestStubs } from '../../shared/testing/layout.testing';
import { CatalogMembersReader } from './catalog-members.reader';
import { CatalogPageHarness } from './case-list-page.testing';
import { CASE_LIST_FIXTURE, CATALOG_FAILURE } from './case-list-store.fixtures';
import { CaseListPage } from './case-list.page';

class CatalogRetryFocusHarness extends CatalogPageHarness {
  readonly resultsHeading = this.locatorFor('#catalog-results-heading');
}

describe('catalog page Retry focus', () => {
  provideLayoutTestStubs();
  const list = vi.fn<CaseListDataService['list']>();
  beforeEach(() => {
    list.mockReset().mockResolvedValue(CASE_LIST_FIXTURE);
    const paramMap = new BehaviorSubject(convertToParamMap({ projectId: 'project', orgId: 'org' }));
    TestBed.configureTestingModule({
      imports: [CaseListPage],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: CaseListDataService, useValue: { list } },
        { provide: SessionStore, useValue: { has: () => false } },
        {
          provide: ActivatedRoute,
          useValue: {
            pathFromRoot: [{ paramMap }],
            queryParamMap: new BehaviorSubject(convertToParamMap({})),
          },
        },
      ],
    });
    TestBed.overrideComponent(CaseListPage, {
      add: {
        providers: [
          {
            provide: CatalogMembersReader,
            useValue: {
              members: signal(new Map()),
              status: signal('ready'),
              failure: signal(null),
              init: vi.fn(),
              clear: vi.fn(),
            },
          },
        ],
      },
    });
  });

  async function renderFailedPage() {
    const fixture = TestBed.createComponent(CaseListPage);
    fixture.detectChanges();
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(
      fixture,
      CatalogRetryFocusHarness,
    );
    list.mockRejectedValueOnce(new ApiRequestError(CATALOG_FAILURE));
    await (await harness.next()).click();
    expect(await harness.pageError()).not.toBeNull();
    return { fixture, harness };
  }

  it.each(['success', 'failure'] as const)(
    'hands Retry focus to the surviving results heading during a pending %s retry',
    async (outcome) => {
      const { fixture, harness } = await renderFailedPage();
      const pending = new Subject<CaseListResponse>();
      list.mockReturnValueOnce(firstValueFrom(pending));
      await manualChangeDetection(async () => {
        const retry = await harness.retryPage();
        await retry.focus();
        await retry.click();
        fixture.detectChanges();
        TestBed.tick();
        expect(await harness.pageError()).toBeNull();
        expect(await (await harness.resultsHeading()).isFocused()).toBe(true);
      });
      const row = CASE_LIST_FIXTURE.items[0];
      if (!row) throw new Error('Missing catalog fixture');
      if (outcome === 'success') {
        pending.next({ items: [{ ...row, id: 'retry-page-case' }], next_cursor: null });
      } else {
        pending.error(new ApiRequestError(CATALOG_FAILURE));
      }
      await fixture.whenStable();
      expect(await (await harness.resultsHeading()).isFocused()).toBe(true);
      expect(await (await harness.pageNumber()).text()).toBe(
        outcome === 'success' ? 'Page 2' : 'Page 1',
      );
      expect(await harness.pageError()).toEqual(outcome === 'success' ? null : expect.anything());
    },
  );

  it.each([
    ['success', 'before-render'],
    ['failure', 'before-render'],
    ['success', 'after-handoff'],
    ['failure', 'after-handoff'],
  ] as const)(
    'does not steal focus moved to Search on %s %s',
    async (outcome, timing) => {
      const { fixture, harness } = await renderFailedPage();
      const pending = new Subject<CaseListResponse>();
      list.mockReturnValueOnce(firstValueFrom(pending));
      await manualChangeDetection(async () => {
        const retry = await harness.retryPage();
        await retry.focus();
        await retry.click();
        if (timing === 'before-render') await (await harness.search()).focus();
        fixture.detectChanges();
        TestBed.tick();
        if (timing === 'after-handoff') await (await harness.search()).focus();
        expect(await (await harness.search()).isFocused()).toBe(true);
      });
      const row = CASE_LIST_FIXTURE.items[0];
      if (!row) throw new Error('Missing catalog fixture');
      if (outcome === 'success') {
        pending.next({ items: [{ ...row, id: 'retry-page-case' }], next_cursor: null });
      } else {
        pending.error(new ApiRequestError(CATALOG_FAILURE));
      }
      await fixture.whenStable();
      expect(await (await harness.search()).isFocused()).toBe(true);
    },
  );
  it('leaves Search focused when Retry is activated without keyboard focus on it', async () => {
    const { harness } = await renderFailedPage();
    const row = CASE_LIST_FIXTURE.items[0];
    if (!row) throw new Error('Missing catalog fixture');
    list.mockResolvedValueOnce({ items: [{ ...row, id: 'retry-page-case' }], next_cursor: null });
    await (await harness.search()).focus();
    await (await harness.retryPage()).click();
    expect(await (await harness.search()).isFocused()).toBe(true);
  });
});
