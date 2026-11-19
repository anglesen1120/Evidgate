import { InteractivityChecker } from '@angular/cdk/a11y';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthoringPendingDataService } from '../../../core/data/authoring-pending-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { PendingChangesPage } from './pending-changes.page';
import { CHANGE, ITEM, PROJECT, PendingUiHarness, pendingRow } from './pending-changes.testing';

const SECOND = { ...pendingRow(), change_id: ITEM, target_id: CHANGE };

function consentDialog(): HTMLElement {
  const dialog = document.querySelector<HTMLElement>('[role="alertdialog"]');
  if (!dialog) throw new Error('Expected the pending consent alertdialog');
  return dialog;
}

function escapeFrom(element: HTMLElement): void {
  element.dispatchEvent(
    new KeyboardEvent('keydown', {
      key: 'Escape',
      keyCode: 27,
      bubbles: true,
      cancelable: true,
    }),
  );
}

provideLayoutTestStubs();
describe('PendingChangesPage dialog accessibility', () => {
  const desktop = signal(true);
  const apply = vi.fn<AuthoringPendingDataService['apply']>();
  const discard = vi.fn<AuthoringPendingDataService['discard']>();
  beforeEach(() => {
    desktop.set(true);
    apply.mockReset().mockResolvedValue({
      batch_id: ITEM,
      applied_change_ids: [CHANGE, ITEM],
      versions: [],
      lineage: [],
      committed_at: '2026-11-19T00:00:00Z',
    });
    discard.mockReset().mockResolvedValue({
      change_id: CHANGE,
      state: 'discarded',
      discarded_at: '2026-11-19T00:00:00Z',
    });
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { data: of({ pageHeadingLevel: 1 }) } },
        {
          provide: SessionStore,
          useValue: { currentProject: signal({ id: PROJECT }), has: () => true },
        },
        {
          provide: NarrowMutationPolicy,
          useValue: {
            canMutate: desktop,
            narrow: computed(() => !desktop()),
            allowsMutation: () => true,
          },
        },
        {
          provide: AuthoringPendingDataService,
          useValue: {
            listPending: vi
              .fn()
              .mockResolvedValue({ items: [pendingRow(), SECOND], next_cursor: null }),
            selection: vi
              .fn()
              .mockResolvedValue({ items: [pendingRow(), SECOND], batchEtag: '"batch"' }),
            checklistItems: vi.fn().mockResolvedValue({ items: [], next_cursor: null }),
            apply,
            discard,
          },
        },
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
    const fixture = TestBed.createComponent(PendingChangesPage);
    await fixture.whenStable();
    return {
      fixture,
      ui: await TestbedHarnessEnvironment.harnessForFixture(fixture, PendingUiHarness),
    };
  }
  it.each(['apply', 'discard'] as const)(
    '%s consent is a modal trap and Escape restores its opener',
    async (action) => {
      const { fixture, ui } = await setup();
      if (action === 'discard') await ui.click('[data-pending-preview]');
      const opener = `[data-pending-${action}]`;
      await ui.focusControl(opener);
      await ui.click(opener);
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
      escapeFrom(cancel);
      await fixture.whenStable();
      expect(document.querySelector('[role="alertdialog"]')).toBeNull();
      expect(await ui.focused(opener)).toBe(true);
      expect(apply).not.toHaveBeenCalled();
      expect(discard).not.toHaveBeenCalled();
    },
    30_000,
  );
  it('falls back to the preview region when an atomic apply empties the queue', async () => {
    const { fixture, ui } = await setup();
    await ui.focusControl('[data-pending-apply]');
    await ui.click('[data-pending-apply]');
    const confirm = consentDialog().querySelector<HTMLElement>('[data-pending-confirm]');
    if (!confirm) throw new Error('Expected the consent commit control');
    confirm.click();
    await fixture.whenStable();
    expect(apply).toHaveBeenCalledTimes(1);
    expect(await ui.count('[data-pending-receipt]')).toBe(1);
    // The contract applies the whole selection, so both queued rows appear in the receipt.
    expect(await ui.count('[data-pending-applied-id]')).toBe(2);
    expect(await ui.focused('[data-pending-preview-content]')).toBe(true);
    // An emptied queue either removes the Apply control or leaves it disabled; never focused.
    if ((await ui.count('[data-pending-apply]')) > 0) {
      expect(await ui.disabled('[data-pending-apply]')).toBe(true);
      expect(await ui.focused('[data-pending-apply]')).toBe(false);
    }
  }, 30_000);
  it('moves focus to the preview region after discard removes its opener', async () => {
    const { fixture, ui } = await setup();
    await ui.click('[data-pending-preview]');
    await ui.focusControl('[data-pending-discard]');
    await ui.click('[data-pending-discard]');
    const confirm = consentDialog().querySelector<HTMLElement>('[data-pending-confirm]');
    if (!confirm) throw new Error('Expected the consent commit control');
    confirm.click();
    await fixture.whenStable();
    expect(discard).toHaveBeenCalledTimes(1);
    expect(discard.mock.calls[0]).toContain(CHANGE);
    expect(await ui.count('[data-pending-discard]')).toBe(0);
    expect(await ui.focused('[data-pending-preview-content]')).toBe(true);
  }, 30_000);
});
