import { registerLocaleData } from '@angular/common';
import viLocale from '@angular/common/locales/vi';
import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { LOCALE_ID, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import type { ManualCaseListItemDto } from '@evidgate/sdk';
import { BehaviorSubject } from 'rxjs';
import { RETAINED_ROWS_CAP } from '../../shared/keyset-retention';
import { provideLayoutTestStubs } from '../../shared/testing/layout.testing';
import { AuthoringDataService } from './authoring-data.service';
import { AuthoringCaseTableComponent } from './authoring-case-table.component';

class CaseCapNoticeHarness extends ComponentHarness {
  static hostSelector = 'app-authoring-case-table';
  readonly notice = this.locatorFor('[data-case-browse-capped]');
  readonly announcement = this.locatorFor('app-live-status [role="status"]');
}

const caseRow = (index: number): ManualCaseListItemDto => ({
  case_id: `case-${index}`,
  code: `CASE-${index}`,
  node_id: '018f0000-0000-7000-8000-000000000101',
  checklist_item_id: '018f0000-0000-7000-8000-000000000201',
  title: 'Manual case',
  priority: 'high',
  state: 'draft',
  version: 1,
  linked_live_count: 0,
  linked_case_ids: [],
});

/** One row beyond each cap keeps the list capped without a second page. */
const SCENARIOS = [
  { locale: 'en', cap: 25, rows: 26, figure: '25' },
  { locale: 'vi', cap: 25, rows: 26, figure: '25' },
  { locale: 'en', cap: null, rows: 10_001, figure: '10,000' },
  { locale: 'vi', cap: null, rows: 10_001, figure: '10.000' },
] as const;

describe('authoring case table localized cap notice', () => {
  provideLayoutTestStubs();
  registerLocaleData(viLocale);
  afterEach(() => TestBed.resetTestingModule());

  it.each(SCENARIOS)(
    'renders and announces the cap as $figure in $locale',
    async ({ locale, cap, rows, figure }) => {
      const paramMap = new BehaviorSubject(convertToParamMap({ projectId: 'project' }));
      TestBed.configureTestingModule({
        imports: [AuthoringCaseTableComponent],
        providers: [
          provideZonelessChangeDetection(),
          provideRouter([]),
          { provide: LOCALE_ID, useValue: locale },
          ...(cap === null ? [] : [{ provide: RETAINED_ROWS_CAP, useValue: cap }]),
          {
            provide: AuthoringDataService,
            useValue: {
              listManualCases: () =>
                Promise.resolve({
                  items: Array.from({ length: rows }, (_, index) => caseRow(index)),
                  next_cursor: 'beyond-cap',
                }),
            },
          },
          {
            provide: ActivatedRoute,
            useValue: {
              pathFromRoot: [{ paramMap }],
              queryParamMap: new BehaviorSubject(convertToParamMap({})),
            },
          },
        ],
      });
      const fixture = TestBed.createComponent(AuthoringCaseTableComponent);
      fixture.detectChanges();
      await fixture.whenStable();
      const harness = await TestbedHarnessEnvironment.harnessForFixture(
        fixture,
        CaseCapNoticeHarness,
      );
      expect(await (await harness.notice()).text()).toContain(`beyond the ${figure} loaded`);
      expect(await (await harness.announcement()).text()).toContain(`beyond the ${figure} loaded`);
    },
    20_000,
  );
});
