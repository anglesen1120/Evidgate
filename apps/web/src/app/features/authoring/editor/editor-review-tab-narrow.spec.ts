import { BreakpointObserver, type BreakpointState } from '@angular/cdk/layout';
import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import type { ManualCaseDetailDto } from '@evidgate/sdk';
import { BehaviorSubject, of } from 'rxjs';
import { AuthoringEditorDataService } from '../../../core/data/authoring-editor-data.service';
import { AuthoringPendingDataService } from '../../../core/data/authoring-pending-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import {
  DESKTOP_MUTATION_QUERY,
  NarrowMutationPolicy,
} from '../../../shared/ui/narrow/narrow-mutation-policy';
import { ITEM, PROJECT, pendingRow } from '../pending-changes/pending-changes.testing';
import { EditorPage } from './editor.page';

provideLayoutTestStubs();

class NarrowReviewEditorHarness extends ComponentHarness {
  static hostSelector = 'app-authoring-editor';

  async count(selector: string) {
    return (await this.locatorForAll(selector)()).length;
  }

  async choosePanel(label: string) {
    await (await this.locatorFor('#case-panel-select')()).click();
    const options = await this.documentRootLocatorFactory().locatorForAll('[role="option"]')();
    for (const option of options) {
      if ((await option.text()).trim() === label) {
        await option.click();
        return;
      }
    }
    throw new Error('Missing editor section: ' + label);
  }

  async selectDesktopReview() {
    await (await this.locatorFor('[data-editor-review-tab]')()).click();
  }
}

const CASE: ManualCaseDetailDto = {
  case_id: 'a4430000-0000-4000-8000-000000000001',
  checklist_item_id: ITEM,
  node_id: 'a4430000-0000-4000-8000-000000000012',
  code: 'AUTH-001',
  title: 'Review checkout total',
  description: null,
  precondition: 'Checkout is open',
  priority: 'high',
  steps: [{ no: 1, action: 'Confirm order', expected: 'The total is shown', data: null }],
  state: 'draft',
  version: 2,
  created_at: '2026-11-18T00:00:00Z',
  updated_at: '2026-11-19T00:00:00Z',
};

describe('Editor checklist review notice ownership', () => {
  const readCase = vi.fn<AuthoringEditorDataService['readCase']>();
  const versions = vi.fn<AuthoringEditorDataService['versions']>();
  const listPending = vi.fn<AuthoringPendingDataService['listPending']>();
  const queue = vi.fn<AuthoringPendingDataService['queue']>();
  let viewportWidth: number;
  let viewportStates: BehaviorSubject<BreakpointState>;

  beforeEach(() => {
    viewportWidth = 360;
    viewportStates = new BehaviorSubject<BreakpointState>({
      matches: false,
      breakpoints: { [DESKTOP_MUTATION_QUERY]: false },
    });
    readCase.mockReset().mockResolvedValue({ view: CASE, etag: '"2"' });
    versions.mockReset().mockResolvedValue({ items: [], next_cursor: null });
    listPending.mockReset().mockResolvedValue({ items: [pendingRow()], next_cursor: null });
    queue.mockReset();
    TestBed.configureTestingModule({
      imports: [EditorPage],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: {
            paramMap: of(convertToParamMap({ caseId: CASE.case_id })),
            snapshot: { data: { pageHeadingLevel: 1 } },
          },
        },
        { provide: AuthoringEditorDataService, useValue: { readCase, versions } },
        { provide: AuthoringPendingDataService, useValue: { listPending, queue } },
        {
          provide: SessionStore,
          useValue: {
            currentOrg: signal({ id: 'org' }),
            currentProject: signal({ id: PROJECT, org_id: 'org' }),
            has: () => true,
          },
        },
        {
          provide: BreakpointObserver,
          useValue: {
            observe: () => viewportStates.asObservable(),
            isMatched: (query: string) => query === DESKTOP_MUTATION_QUERY && viewportWidth >= 768,
          },
        },
      ],
    });
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    viewportStates.complete();
  });

  function resize(width: number) {
    viewportWidth = width;
    const matches = width >= 768;
    viewportStates.next({ matches, breakpoints: { [DESKTOP_MUTATION_QUERY]: matches } });
  }

  async function mount() {
    const fixture = TestBed.createComponent(EditorPage);
    await fixture.whenStable();
    return {
      fixture,
      ui: await TestbedHarnessEnvironment.harnessForFixture(fixture, NarrowReviewEditorHarness),
    };
  }

  it('renders exactly one desktop-required notice at 360 px with the actual review tab mounted', async () => {
    const { fixture, ui } = await mount();
    expect(viewportWidth).toBe(360);
    expect(TestBed.inject(NarrowMutationPolicy).canMutate()).toBe(false);
    await ui.choosePanel('Checklist review');
    await fixture.whenStable();
    expect(await ui.count('app-checklist-review-tab')).toBe(1);
    expect(await ui.count('[data-review-change]')).toBe(1);
    expect(await ui.count('app-desktop-required-notice')).toBe(1);
    expect(await ui.count('app-desktop-required-notice [role="note"]')).toBe(1);
    expect(await ui.count('app-checklist-review-tab app-desktop-required-notice')).toBe(0);
    expect(await ui.count('#review-comment')).toBe(0);
    expect(queue).not.toHaveBeenCalled();
  });

  it('keeps the single editor-owned notice across narrow section changes', async () => {
    const { fixture, ui } = await mount();
    await ui.choosePanel('Checklist review');
    await fixture.whenStable();
    await ui.choosePanel('Editor');
    await fixture.whenStable();
    expect(await ui.count('app-desktop-required-notice [role="note"]')).toBe(1);
    await ui.choosePanel('Checklist review');
    await fixture.whenStable();
    expect(await ui.count('[data-review-change]')).toBe(1);
    expect(await ui.count('app-desktop-required-notice [role="note"]')).toBe(1);
    expect(listPending).toHaveBeenCalledTimes(1);
    expect(queue).not.toHaveBeenCalled();
  });

  it('shows the notice only below the desktop cutoff while retaining the mounted review tab', async () => {
    resize(1280);
    const { fixture, ui } = await mount();
    await ui.selectDesktopReview();
    await fixture.whenStable();
    expect(TestBed.inject(NarrowMutationPolicy).canMutate()).toBe(true);
    expect(await ui.count('app-desktop-required-notice [role="note"]')).toBe(0);
    expect(await ui.count('#review-comment')).toBe(1);
    resize(360);
    await fixture.whenStable();
    expect(await ui.count('app-checklist-review-tab')).toBe(1);
    expect(await ui.count('[data-review-change]')).toBe(1);
    expect(await ui.count('app-desktop-required-notice [role="note"]')).toBe(1);
    expect(await ui.count('#review-comment')).toBe(0);
    expect(queue).not.toHaveBeenCalled();
  });
});
