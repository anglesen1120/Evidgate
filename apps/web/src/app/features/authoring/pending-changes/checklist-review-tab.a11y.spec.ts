import { InteractivityChecker } from '@angular/cdk/a11y';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthoringPendingDataService } from '../../../core/data/authoring-pending-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { ChecklistReviewTabComponent } from './checklist-review-tab.component';
import { CHANGE, ITEM, PROJECT, PendingUiHarness, pendingRow } from './pending-changes.testing';

const HOSTILE =
  '<img src=x onerror="globalThis.pwned=1"> <script>globalThis.pwned=1</script> ' +
  '\u202eevil\u202c RTL \u200bzero\u200bwidth ' +
  'A'.repeat(140);

function consentDialog(): HTMLElement {
  const dialog = document.querySelector<HTMLElement>('[role="alertdialog"]');
  if (!dialog) throw new Error('Expected the review consent alertdialog');
  return dialog;
}

/** The base harness only assigns `value`, which leaves the `ngModel` drafts empty in jsdom. */
class ReviewTabHarness extends PendingUiHarness {
  override async set(selector: string, value: string) {
    const field = await this.locatorFor(selector)();
    await field.setInputValue(value);
    await field.dispatchEvent('input');
  }
  async value(selector: string) {
    return (await this.locatorFor(selector)()).getProperty<string>('value');
  }
}

provideLayoutTestStubs();
describe('ChecklistReviewTabComponent accessibility', () => {
  const desktop = signal(true);
  const queue = vi.fn<AuthoringPendingDataService['queue']>();
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
          useValue: {
            currentProject: signal({ id: PROJECT, org_id: 'org' }),
            has: () => true,
          },
        },
        {
          provide: NarrowMutationPolicy,
          useValue: {
            canMutate: desktop,
            narrow: computed(() => !desktop()),
            allowsMutation: () => true,
          },
        },
        { provide: AuthoringPendingDataService, useValue: { queue, listPending } },
      ],
    });
    // jsdom reports every element hidden; keep the real CDK focus trap and tabbable checks.
    vi.spyOn(TestBed.inject(InteractivityChecker), 'isVisible').mockReturnValue(true);
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });
  async function setup() {
    const fixture = TestBed.createComponent(ChecklistReviewTabComponent);
    fixture.componentRef.setInput('projectId', PROJECT);
    fixture.componentRef.setInput('itemId', ITEM);
    await fixture.whenStable();
    return {
      fixture,
      ui: await TestbedHarnessEnvironment.harnessForFixture(fixture, ReviewTabHarness),
    };
  }
  it('renders hostile checklist comment text inert, verbatim and without injection', async () => {
    listPending.mockResolvedValue({
      items: [
        {
          ...pendingRow('comment'),
          payload: { body: HOSTILE },
          reason: HOSTILE,
        },
      ],
      next_cursor: null,
    });
    const { ui } = await setup();
    expect(await ui.count('[data-review-change]')).toBe(1);
    const rendered = await ui.textAt('[data-review-change]');
    expect(rendered).toContain('<img src=x onerror="globalThis.pwned=1">');
    expect(rendered).toContain('<script>globalThis.pwned=1</script>');
    expect(rendered).toContain('A'.repeat(140));
    expect(rendered).toContain('\u202e');
    expect(rendered).toContain('\u200b');
    expect(await ui.count('[data-review-change] img')).toBe(0);
    expect(await ui.count('[data-review-change] script')).toBe(0);
    expect(document.querySelector('[data-review-change] [onerror]')).toBeNull();
    expect(Object.hasOwn(globalThis, 'pwned')).toBe(false);
  });
  it('keeps hostile queued text inert after a successful comment queue', async () => {
    listPending
      .mockResolvedValueOnce({ items: [pendingRow()], next_cursor: null })
      .mockResolvedValue({
        items: [
          {
            ...pendingRow('comment'),
            payload: { body: HOSTILE },
            reason: HOSTILE,
          },
        ],
        next_cursor: null,
      });
    const { ui, fixture } = await setup();
    await ui.set('#review-comment', HOSTILE);
    expect(await ui.value('#review-comment')).toBe(HOSTILE);
    await ui.click('[data-review-comment]');
    await fixture.whenStable();
    expect(queue).toHaveBeenCalledTimes(1);
    expect(queue.mock.calls[0]?.[1]).toMatchObject({ payload: { body: HOSTILE } });
    expect(await ui.value('#review-comment')).toBe('');
    expect(await ui.count('[data-review-success]')).toBe(1);
    expect(await ui.count('[data-review-change]')).toBe(1);
    const rendered = await ui.textAt('[data-review-change]');
    expect(rendered).toContain('<img src=x onerror="globalThis.pwned=1">');
    expect(rendered).toContain('<script>globalThis.pwned=1</script>');
    expect(rendered).toContain('A'.repeat(140));
    expect(await ui.count('[data-review-change] img')).toBe(0);
    expect(await ui.count('[data-review-change] script')).toBe(0);
    expect(document.querySelector('[data-review-change] [onerror]')).toBeNull();
    expect(Object.hasOwn(globalThis, 'pwned')).toBe(false);
  });
  it('delete consent is a modal trap and Escape restores its opener', async () => {
    const { ui, fixture } = await setup();
    await ui.set('#review-reason', 'Retain this reason');
    await ui.focusControl('[data-review-delete]');
    await ui.click('[data-review-delete]');
    const dialog = consentDialog();
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    const anchors = document.querySelectorAll<HTMLElement>('.cdk-focus-trap-anchor');
    expect(anchors).toHaveLength(2);
    // jsdom does not move focus for Tab, so focus the sentinels native Tab reaches at each end.
    anchors[1]?.focus();
    expect(dialog.contains(document.activeElement)).toBe(true);
    anchors[0]?.focus();
    expect(dialog.contains(document.activeElement)).toBe(true);
    const cancel = dialog.querySelector<HTMLElement>('[data-pending-cancel]');
    if (!cancel) throw new Error('Expected the consent Cancel control');
    cancel.focus();
    cancel.dispatchEvent(
      new KeyboardEvent('keydown', {
        key: 'Escape',
        keyCode: 27,
        bubbles: true,
        cancelable: true,
      }),
    );
    await fixture.whenStable();
    expect(document.querySelector('[role="alertdialog"]')).toBeNull();
    expect(await ui.focused('[data-review-delete]')).toBe(true);
    expect(queue).not.toHaveBeenCalled();
  });
});
