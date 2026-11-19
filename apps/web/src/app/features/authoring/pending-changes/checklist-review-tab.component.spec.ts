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

class ReviewTabHarness extends PendingUiHarness {
  override async set(selector: string, value: string) {
    const field = await this.locatorFor(selector)();
    await field.setInputValue(value);
    await field.dispatchEvent('input');
  }
  async value(selector: string) {
    return (await this.locatorFor(selector)()).getProperty<string>('value');
  }
  async attribute(selector: string, name: string) {
    return (await this.locatorFor(selector)()).getAttribute(name);
  }
  async blur(selector: string) {
    await (await this.locatorFor(selector)()).blur();
  }
  async focus(selector: string) {
    await (await this.locatorFor(selector)()).focus();
  }
  async inspectionFocused() {
    return (await this.locatorFor('section[tabindex="-1"]')()).isFocused();
  }
}

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
      ui: await TestbedHarnessEnvironment.harnessForFixture(fixture, ReviewTabHarness),
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
    expect(await ui.value('#review-comment')).toBe('');
    expect(await ui.value('#review-reason')).toBe('Explain the edit');
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
    await ui.set('#review-reason', 'Retain the reason');
    await ui.click('[data-review-comment]');
    await fixture.whenStable();
    expect(queue).toHaveBeenCalledTimes(1);
    expect(await ui.value('#review-comment')).toBe('Keep this comment');
    expect(await ui.value('#review-reason')).toBe('Retain the reason');
    expect(await ui.textAt('[data-review-enqueue-error]')).toContain(
      kind === 'conflict' ? PENDING_COPY.duplicatePending : pendingError(kind, status).message,
    );
    expect(await ui.text()).toContain(
      kind === 'conflict'
        ? PENDING_COPY.duplicatePending
        : kind === 'validation'
          ? PENDING_UI.reasonRejected
          : '429',
    );
    expect(await ui.count('[data-review-success]')).toBe(0);
  });
  it('projects selectable drafts when resizing narrow and restores them on desktop', async () => {
    const { ui, fixture } = await setup();
    const comment = '<script>unsubmitted</script>\n' + 'Long comment. '.repeat(100);
    const reason = 'Retain this optional reason\nwith its second line';
    await ui.set('#review-comment', comment);
    await ui.set('#review-reason', reason);
    await ui.focus('#review-reason');
    desktop.set(false);
    await fixture.whenStable();
    expect(await ui.count('[data-review-comment]')).toBe(0);
    expect(await ui.count('[data-review-delete]')).toBe(0);
    expect(await ui.textAt('[data-review-comment-snapshot] dt')).toBe(PENDING_COPY.commentLabel);
    expect(await ui.textAt('[data-review-comment-snapshot] dd')).toBe(comment);
    expect(await ui.textAt('[data-review-reason-snapshot] dt')).toBe(PENDING_UI.queueReason);
    expect(await ui.textAt('[data-review-reason-snapshot] dd')).toBe(reason);
    expect(await ui.count('[data-review-comment-snapshot] script')).toBe(0);
    expect(await ui.inspectionFocused()).toBe(true);
    desktop.set(true);
    await fixture.whenStable();
    expect(await ui.value('#review-comment')).toBe(comment);
    expect(await ui.value('#review-reason')).toBe(reason);
    expect(await ui.count('[data-review-comment-snapshot]')).toBe(0);
    expect(queue).not.toHaveBeenCalled();
  });
  it('labels unset drafts in the narrow snapshot', async () => {
    desktop.set(false);
    const { ui } = await setup();
    expect(await ui.textAt('[data-review-comment-snapshot] dd')).toBe(PENDING_COPY.reasonUnset);
    expect(await ui.textAt('[data-review-reason-snapshot] dd')).toBe(PENDING_COPY.reasonUnset);
  });
  it.each([
    ['comment', 2000],
    ['reason', 500],
  ] as const)(
    'validates the %s limit and accepts its exact Unicode boundary',
    async (name, limit) => {
      const { ui, fixture } = await setup();
      const selector = `#review-${name}`;
      expect(await ui.attribute(selector, 'maxlength')).toBe(String(limit));
      expect(await ui.attribute(selector, 'aria-describedby')).toContain(`review-${name}-count`);
      await ui.set('#review-comment', 'A valid comment');
      await ui.set(selector, 'x'.repeat(limit + 1));
      await ui.focus(selector);
      await ui.blur(selector);
      expect(await ui.attribute(selector, 'aria-invalid')).toBe('true');
      expect(await ui.textAt(`#review-${name}-error`)).toContain(limit === 2000 ? '2,000' : '500');
      await ui.click('[data-review-comment]');
      expect(queue).not.toHaveBeenCalled();
      expect(await ui.value(selector)).toBe('x'.repeat(limit + 1));
      const boundary = 'x'.repeat(limit - 1) + '😀';
      await ui.set(selector, boundary);
      expect(await ui.attribute(selector, 'aria-invalid')).toBeNull();
      const formattedLimit = limit === 2000 ? '2,000' : '500';
      expect((await ui.textAt(`#review-${name}-count`)).replace(/\s+/g, ' ')).toBe(
        `${formattedLimit} of ${formattedLimit} characters`,
      );
      await ui.click('[data-review-comment]');
      await fixture.whenStable();
      expect(queue).toHaveBeenCalledTimes(1);
      expect(queue).toHaveBeenCalledWith(
        PROJECT,
        expect.objectContaining(
          name === 'comment' ? { payload: { body: boundary } } : { reason: boundary },
        ),
        expect.any(String),
        expect.any(AbortSignal),
      );
    },
  );
  it('refuses delete consent for an over-limit reason and accepts its corrected boundary', async () => {
    const { ui, root, fixture } = await setup();
    await ui.set('#review-reason', 'r'.repeat(501));
    await ui.click('[data-review-delete]');
    expect(await root.getAllHarnesses(PendingConsentHarness)).toHaveLength(0);
    expect(queue).not.toHaveBeenCalled();
    expect(await ui.attribute('#review-reason', 'aria-invalid')).toBe('true');
    await ui.set('#review-reason', 'r'.repeat(500));
    await ui.click('[data-review-delete]');
    await (await root.getHarness(PendingConsentHarness)).confirm();
    await fixture.whenStable();
    expect(queue).toHaveBeenCalledWith(
      PROJECT,
      expect.objectContaining({ change_type: 'delete', reason: 'r'.repeat(500) }),
      expect.any(String),
      expect.any(AbortSignal),
    );
  }, 30_000);
  it('hides queue controls below the desktop breakpoint', async () => {
    desktop.set(false);
    const { ui } = await setup();
    expect(await ui.count('[data-review-comment]')).toBe(0);
    expect(await ui.count('[data-review-delete]')).toBe(0);
    expect(await ui.count('[data-review-change]')).toBe(1);
  });
});
