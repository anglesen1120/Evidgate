import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideRouter } from '@angular/router';
import { ApiRequestError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { AuthoringPendingDataService } from '../../../core/data/authoring-pending-data.service';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { ChecklistReviewTabComponent } from './checklist-review-tab.component';
import { PENDING_COPY } from './pending-changes.copy';
import { PENDING_UI } from './pending-changes.ui-copy';
import {
  CHANGE,
  ITEM,
  PROJECT,
  PendingConsentHarness,
  PendingUiHarness,
  pendingError,
  pendingRow,
} from './pending-changes.testing';

provideLayoutTestStubs();
describe('ChecklistReviewTabComponent', () => {
  const queue = vi.fn<AuthoringPendingDataService['queue']>();
  const desktop = signal(true);
  const listPending = vi.fn<AuthoringPendingDataService['listPending']>();
  beforeEach(() => {
    desktop.set(true);
    queue.mockReset().mockResolvedValue({
      change_id: CHANGE,
      state: 'pending',
      base_version: 1,
      created_at: '2026-11-19T00:00:00Z',
    });
    listPending.mockReset().mockResolvedValue({ items: [pendingRow()], next_cursor: null });
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: SessionStore,
          useValue: { currentProject: signal({ id: PROJECT, org_id: 'org' }), has: () => true },
        },
        {
          provide: NarrowMutationPolicy,
          useValue: {
            canMutate: desktop,
            narrow: computed(() => !desktop()),
            allowsMutation: () => desktop(),
          },
        },
        { provide: AuthoringPendingDataService, useValue: { queue, listPending } },
      ],
    });
  });
  async function setup() {
    const fixture = TestBed.createComponent(ChecklistReviewTabComponent);
    fixture.componentRef.setInput('projectId', PROJECT);
    fixture.componentRef.setInput('itemId', ITEM);
    fixture.detectChanges();
    await fixture.whenStable();
    return {
      fixture,
      ui: await TestbedHarnessEnvironment.harnessForFixture(fixture, PendingUiHarness),
      root: TestbedHarnessEnvironment.documentRootLoader(fixture),
    };
  }
  it('shows only this item’s loaded changes and the project queue link', async () => {
    listPending.mockResolvedValue({
      items: [pendingRow(), { ...pendingRow(), change_id: 'other', target_id: 'other' }],
      next_cursor: null,
    });
    const { ui } = await setup();
    expect(await ui.count('[data-review-change]')).toBe(1);
    expect(await ui.text()).toContain(PENDING_UI.openQueue);
  });
  it('queues a comment through the store and retains the optional reason', async () => {
    const { ui, fixture } = await setup();
    await ui.set('#review-comment', 'A reviewed comment');
    await ui.set('#review-reason', 'Explain the edit');
    await ui.click('[data-review-comment]');
    await fixture.whenStable();
    expect(queue).toHaveBeenCalledWith(
      PROJECT,
      expect.objectContaining({
        change_type: 'comment',
        payload: { body: 'A reviewed comment' },
        reason: 'Explain the edit',
      }),
      expect.any(String),
      expect.any(AbortSignal),
    );
    expect(await ui.text()).toContain(PENDING_UI.queued);
  });
  it('refuses a blank comment without issuing a queue request', async () => {
    const { ui } = await setup();
    await ui.click('[data-review-comment]');
    expect(queue).not.toHaveBeenCalled();
    expect(await ui.text()).toContain(PENDING_UI.commentRequired);
  });
  it('requires delete confirmation with initial Cancel focus', async () => {
    const { ui, root, fixture } = await setup();
    await ui.click('[data-review-delete]');
    const dialog = await root.getHarness(PendingConsentHarness);
    expect(await dialog.cancelFocused()).toBe(true);
    expect(queue).not.toHaveBeenCalled();
    await dialog.confirm();
    await fixture.whenStable();
    expect(queue).toHaveBeenCalledWith(
      PROJECT,
      expect.objectContaining({ change_type: 'delete', target_id: ITEM }),
      expect.any(String),
      expect.any(AbortSignal),
    );
  }, 30_000);
  it.each([
    ['conflict', 409],
    ['validation', 422],
    ['rate_limited', 429],
  ] as const)('retains the comment and displays a %s refusal', async (kind, status) => {
    queue.mockRejectedValue(
      new ApiRequestError({
        ...pendingError(kind, status),
        details: [
          {
            reason: kind === 'conflict' ? 'change_already_pending' : 'reason_required',
            path: '/reason',
          },
        ],
      }),
    );
    const { ui, fixture } = await setup();
    await ui.set('#review-comment', 'Keep this comment');
    await ui.click('[data-review-comment]');
    await fixture.whenStable();
    expect(await ui.text()).toContain(
      kind === 'conflict'
        ? PENDING_COPY.duplicatePending
        : kind === 'validation'
          ? PENDING_UI.reasonRejected
          : '429',
    );
    expect(await ui.count('[data-review-success]')).toBe(0);
  });
  it('hides queue controls below the desktop breakpoint', async () => {
    desktop.set(false);
    const { ui } = await setup();
    expect(await ui.count('[data-review-comment]')).toBe(0);
    expect(await ui.count('[data-review-delete]')).toBe(0);
    expect(await ui.count('[data-review-change]')).toBe(1);
  });
});
