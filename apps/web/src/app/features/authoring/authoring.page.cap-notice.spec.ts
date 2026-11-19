import { registerLocaleData } from '@angular/common';
import viLocale from '@angular/common/locales/vi';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { LOCALE_ID, provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import type { AuthoringNodePathEntry, NodeDto } from '@evidgate/sdk';
import { BehaviorSubject } from 'rxjs';
import { vi } from 'vitest';
import { RETAINED_ROWS_CAP } from '../../shared/keyset-retention';
import { provideLayoutTestStubs } from '../../shared/testing/layout.testing';
import { AuthoringDataService } from './authoring-data.service';
import { AuthoringPage } from './authoring.page';
import { AuthoringStore, type AuthoringNodePage } from './authoring.store';
import { AuthoringPageHarness } from './authoring-tree.testing';

const root: NodeDto = {
  node_id: '018f0000-0000-7000-8000-000000000101',
  parent_id: null,
  kind: 'module',
  title: 'Module',
  position: 0,
  version: 1,
  child_count: 0,
  item_count: 1,
};

const SCENARIOS = [
  { locale: 'en', cap: 25, figure: '25' },
  { locale: 'vi', cap: 25, figure: '25' },
  { locale: 'en', cap: null, figure: '10,000' },
  { locale: 'vi', cap: null, figure: '10.000' },
] as const;

describe('authoring page localized cap announcement', () => {
  provideLayoutTestStubs();
  registerLocaleData(viLocale);
  afterEach(() => TestBed.resetTestingModule());

  it.each(SCENARIOS)('announces the cap as $figure in $locale', async ({ locale, cap, figure }) => {
    const capped: AuthoringNodePage = {
      items: [root],
      nextCursor: null,
      status: 'capped',
      error: null,
    };
    const paramMap = new BehaviorSubject(convertToParamMap({ projectId: 'project' }));
    TestBed.configureTestingModule({
      imports: [AuthoringPage],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: LOCALE_ID, useValue: locale },
        ...(cap === null ? [] : [{ provide: RETAINED_ROWS_CAP, useValue: cap }]),
        {
          provide: AuthoringDataService,
          useValue: {
            listTree: () => Promise.resolve({ items: [root], next_cursor: null }),
            listManualCases: () => Promise.resolve({ items: [], next_cursor: null }),
          },
        },
        {
          provide: ActivatedRoute,
          useValue: {
            pathFromRoot: [{ paramMap }],
            queryParamMap: new BehaviorSubject(convertToParamMap({})),
            snapshot: { data: { pageHeadingLevel: 1 } },
          },
        },
      ],
    });
    TestBed.overrideComponent(AuthoringPage, {
      set: {
        providers: [
          {
            provide: AuthoringStore,
            useValue: {
              projectId: signal('project'),
              roots: signal(capped),
              children: signal<Record<string, AuthoringNodePage>>({}),
              selectedNode: signal<NodeDto | null>(null),
              selectedNodeId: signal<string | null>(null),
              path: signal<AuthoringNodePathEntry[]>([]),
              selectionStatus: signal('ready'),
              treeStatus: signal('ready'),
              treeError: signal(null),
              stale: signal(false),
              init: vi.fn().mockResolvedValue(undefined),
              retryTree: vi.fn().mockResolvedValue(undefined),
              selectNode: vi.fn(),
              expand: vi.fn(),
              loadMoreChildren: vi.fn().mockResolvedValue(true),
            },
          },
        ],
      },
    });
    const fixture = TestBed.createComponent(AuthoringPage);
    fixture.detectChanges();
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(
      fixture,
      AuthoringPageHarness,
    );
    expect(await (await harness.announcement()).text()).toContain(`the first ${figure} nodes`);
  });
});
