import { ComponentHarness, TestKey } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import type { ManualCaseDetailDto } from '@evidgate/sdk';
import { AuthoringEditorDataService } from '../../../core/data/authoring-editor-data.service';
import { AuthoringPendingDataService } from '../../../core/data/authoring-pending-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { ITEM, PROJECT, pendingRow } from '../pending-changes/pending-changes.testing';
import { EditorPage } from './editor.page';

provideLayoutTestStubs();

class EditorReviewHarness extends ComponentHarness {
  static hostSelector = 'app-authoring-editor';

  async tabs() {
    const tabs = await this.locatorForAll('[role="tab"]')();
    return Promise.all(tabs.map(async (tab) => (await tab.text()).trim()));
  }

  async click(selector: string) {
    await (await this.locatorFor(selector)()).click();
  }

  async count(selector: string) {
    return (await this.locatorForAll(selector)()).length;
  }

  async textAt(selector: string) {
    return (await this.locatorFor(selector)()).text();
  }

  async fill(id: string, value: string) {
    const field = await this.locatorFor('#' + id)();
    await field.setInputValue(value);
    await field.dispatchEvent('input');
  }

  async title() {
    return (await this.locatorFor('#case-title')()).getProperty<string>('value');
  }

  async value(id: string) {
    return (await this.locatorFor('#' + id)()).getProperty<string>('value');
  }

  async selectedTab() {
    return (await (await this.locatorFor('[role="tab"][aria-selected="true"]')()).text()).trim();
  }

  async focusFirstTab() {
    const [first] = await this.locatorForAll('[role="tab"]')();
    if (!first) throw new Error('Editor tab is missing');
    await first.focus();
  }

  async nextTab() {
    const current = await this.locatorFor('[role="tab"][aria-selected="true"]')();
    await current.sendKeys(TestKey.RIGHT_ARROW);
    const review = await this.locatorFor('[data-editor-review-tab]')();
    const movedToReview = await review.isFocused();
    await review.sendKeys(TestKey.ENTER);
    return movedToReview;
  }

  async choosePanel(label: string) {
    await this.click('#case-panel-select');
    const options = await this.documentRootLocatorFactory().locatorForAll('[role="option"]')();
    for (const option of options) {
      if ((await option.text()).trim() === label) {
        await option.click();
        return;
      }
    }
    throw new Error('Panel option not found: ' + label);
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

const REVIEW = 'Checklist review';

describe('EditorPage checklist review mount', () => {
  const narrow = signal(false);
  const readCase = vi.fn<AuthoringEditorDataService['readCase']>();
  const versions = vi.fn<AuthoringEditorDataService['versions']>();
  const listPending = vi.fn<AuthoringPendingDataService['listPending']>();
  const queue = vi.fn<AuthoringPendingDataService['queue']>();

  beforeEach(() => {
    narrow.set(false);
    readCase.mockReset().mockResolvedValue({ view: CASE, etag: '"2"' });
    versions.mockReset().mockResolvedValue({ items: [], next_cursor: null });
    queue.mockReset();
    const rows = [
      { ...pendingRow(), payload: { body: 'Parent checklist comment' } },
      {
        ...pendingRow(),
        change_id: '018f0000-0000-7000-8000-000000000004',
        target_id: CASE.case_id,
        payload: { body: 'An unrelated case-id target' },
      },
    ];
    listPending.mockReset().mockImplementation((_project, _signal, _cursor, targetId) =>
      Promise.resolve({
        items: rows.filter((row) => targetId === undefined || row.target_id === targetId),
        next_cursor: null,
      }),
    );
    TestBed.configureTestingModule({
      imports: [EditorPage],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: 'cases/:caseId', component: EditorPage }]),
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
          provide: NarrowMutationPolicy,
          useValue: { narrow, canMutate: () => !narrow(), allowsMutation: () => !narrow() },
        },
      ],
    });
  });

  afterEach(() => TestBed.resetTestingModule());

  async function mount() {
    const routed = await RouterTestingHarness.create('/cases/' + CASE.case_id);
    await routed.fixture.whenStable();
    return {
      routed,
      fixture: routed.fixture,
      ui: await TestbedHarnessEnvironment.loader(routed.fixture).getHarness(EditorReviewHarness),
    };
  }

  it('mounts review lazily for the parent item and retains editor and review drafts across tabs', async () => {
    const { ui, fixture } = await mount();
    expect(await ui.tabs()).toEqual(['Editor', REVIEW, 'Timeline']);
    expect(await ui.count('app-checklist-review-tab')).toBe(0);
    expect(listPending).not.toHaveBeenCalled();
    await ui.fill('case-title', 'Unsaved editor draft');
    await ui.click('[data-editor-review-tab]');
    await fixture.whenStable();
    expect(await ui.selectedTab()).toBe(REVIEW);
    expect(await ui.count('[data-review-change]')).toBe(1);
    expect(await ui.textAt('app-checklist-review-tab')).toContain('Parent checklist comment');
    expect(await ui.textAt('app-checklist-review-tab')).not.toContain(
      'An unrelated case-id target',
    );
    expect(listPending).toHaveBeenCalledWith(PROJECT, expect.any(AbortSignal), undefined, ITEM);
    expect(versions).not.toHaveBeenCalled();
    expect(queue).not.toHaveBeenCalled();
    await ui.fill('review-comment', 'Unsent review comment');
    await ui.fill('review-reason', 'Unsent change reason');
    await ui.click('[role="tab"]:first-child');
    expect(await ui.count('app-checklist-review-tab')).toBe(1);
    expect(await ui.selectedTab()).toBe('Editor');
    expect(await ui.title()).toBe('Unsaved editor draft');
    await ui.click('[data-editor-review-tab]');
    expect(await ui.value('review-comment')).toBe('Unsent review comment');
    expect(await ui.value('review-reason')).toBe('Unsent change reason');
    expect(listPending).toHaveBeenCalledTimes(1);
    expect(queue).not.toHaveBeenCalled();
  });

  it('clears review drafts on a routed case change even when both cases share a checklist item', async () => {
    const { ui, fixture, routed } = await mount();
    await ui.click('[data-editor-review-tab]');
    await fixture.whenStable();
    await ui.fill('review-comment', 'First case comment');
    await ui.fill('review-reason', 'First case reason');
    const otherCase = { ...CASE, case_id: 'a4430000-0000-4000-8000-000000000002' };
    readCase.mockResolvedValue({ view: otherCase, etag: '"2"' });
    await routed.navigateByUrl('/cases/' + otherCase.case_id, EditorPage);
    await fixture.whenStable();
    expect(await ui.selectedTab()).toBe('Editor');
    expect(await ui.count('app-checklist-review-tab')).toBe(0);
    await ui.click('[data-editor-review-tab]');
    await fixture.whenStable();
    expect(await ui.value('review-comment')).toBe('');
    expect(await ui.value('review-reason')).toBe('');
    expect(listPending).toHaveBeenCalledTimes(2);
    expect(queue).not.toHaveBeenCalled();
  });

  it('reaches checklist review between Editor and Timeline with ArrowRight and Enter', async () => {
    const { ui, fixture } = await mount();
    await ui.focusFirstTab();
    expect(await ui.nextTab()).toBe(true);
    await fixture.whenStable();
    expect(await ui.selectedTab()).toBe(REVIEW);
    expect(await ui.count('[data-review-change]')).toBe(1);
    expect(versions).not.toHaveBeenCalled();
    expect(queue).not.toHaveBeenCalled();
  });

  it('offers the same review panel in the narrow select and retains its label across resize', async () => {
    narrow.set(true);
    const { ui, fixture } = await mount();
    expect(await ui.count('app-checklist-review-tab')).toBe(0);
    await ui.choosePanel(REVIEW);
    await fixture.whenStable();
    expect(await ui.textAt('#case-panel-select')).toContain(REVIEW);
    expect(await ui.count('[data-review-change]')).toBe(1);
    expect(await ui.count('#review-comment')).toBe(0);
    narrow.set(false);
    await fixture.whenStable();
    expect(await ui.selectedTab()).toBe(REVIEW);
    expect(await ui.count('#review-comment')).toBe(1);
    expect(queue).not.toHaveBeenCalled();
  });
});
