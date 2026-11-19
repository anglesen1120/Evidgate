import { registerLocaleData } from '@angular/common';
import viLocale from '@angular/common/locales/vi';
import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection, LOCALE_ID, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { vi } from 'vitest';
import { CaseListDataService } from '../../core/data/case-list-data.service';
import { SessionStore } from '../../core/session/session.store';
import { RETAINED_ROWS_CAP } from '../../shared/keyset-retention';
import { provideLayoutTestStubs } from '../../shared/testing/layout.testing';
import { CatalogMembersReader } from './catalog-members.reader';
import { CASE_LIST_FIXTURE } from './case-list-store.fixtures';
import { CaseListPage } from './case-list.page';

class CatalogCapNoticeHarness extends ComponentHarness {
  static hostSelector = 'app-case-list-page';
  readonly notice = this.locatorFor('[data-catalog-browse-capped]');
  readonly summary = this.locatorFor('[data-catalog-summary]');
}

const SCENARIOS = [
  { locale: 'en', cap: 25, rows: 26, figure: '25' },
  { locale: 'vi', cap: 25, rows: 26, figure: '25' },
  { locale: 'en', cap: null, rows: 10_001, figure: '10,000' },
  { locale: 'vi', cap: null, rows: 10_001, figure: '10.000' },
] as const;

describe('catalog case list localized cap notice', () => {
  provideLayoutTestStubs();
  registerLocaleData(viLocale);
  afterEach(() => TestBed.resetTestingModule());

  it.each(SCENARIOS)(
    'renders the cap as $figure in $locale',
    async ({ locale, cap, rows, figure }) => {
      const first = CASE_LIST_FIXTURE.items[0];
      if (!first) throw new Error('Missing case fixture');
      const list = vi.fn<CaseListDataService['list']>().mockResolvedValue({
        items: Array.from({ length: rows }, (_, index) => ({ ...first, id: String(index) })),
        next_cursor: 'beyond-cap',
      });
      const paramMap = new BehaviorSubject(
        convertToParamMap({ projectId: 'project', orgId: 'org' }),
      );
      TestBed.configureTestingModule({
        imports: [CaseListPage],
        providers: [
          provideZonelessChangeDetection(),
          provideRouter([]),
          { provide: LOCALE_ID, useValue: locale },
          { provide: CaseListDataService, useValue: { list } },
          { provide: SessionStore, useValue: { has: () => false } },
          ...(cap === null ? [] : [{ provide: RETAINED_ROWS_CAP, useValue: cap }]),
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
                reload: vi.fn(),
              },
            },
          ],
        },
      });
      const fixture = TestBed.createComponent(CaseListPage);
      fixture.detectChanges();
      await fixture.whenStable();
      const harness = await TestbedHarnessEnvironment.harnessForFixture(
        fixture,
        CatalogCapNoticeHarness,
      );
      expect(await (await harness.notice()).text()).toContain(`beyond the ${figure} loaded`);
      expect(await (await harness.summary()).text()).toContain(`beyond the ${figure} loaded`);
    },
    20_000,
  );
});
