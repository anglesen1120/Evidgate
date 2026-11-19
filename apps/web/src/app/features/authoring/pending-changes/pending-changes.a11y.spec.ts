import { InteractivityChecker } from '@angular/cdk/a11y';
import { manualChangeDetection } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthoringPendingDataService } from '../../../core/data/authoring-pending-data.service';
import { ApiRequestError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { PendingChangesPage } from './pending-changes.page';
import { PendingChangesStore } from './pending-changes.store';
import {
  CHANGE,
  ITEM,
  PROJECT,
  PendingUiHarness,
  pendingError,
  pendingRow,
} from './pending-changes.testing';

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
  const listPending = vi.fn<AuthoringPendingDataService['listPending']>();
  beforeEach(() => {
    desktop.set(true);
    listPending.mockReset().mockResolvedValue({ items: [pendingRow(), SECOND], next_cursor: null });
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
            listPending,
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
  );
  it('focuses and announces the empty queue after an atomic apply', async () => {
    const { fixture, ui } = await setup();
    await ui.focusControl('[data-pending-apply]');
    await ui.click('[data-pending-apply]');
    const confirm = consentDialog().querySelector<HTMLElement>('[data-pending-confirm]');
    if (!confirm) throw new Error('Expected the consent commit control');
    confirm.click();
    await fixture.whenStable();
    // The mutation has settled; read its rendered result without rechecking the whole fixture
    // before every harness query.
    await manualChangeDetection(async () => {
      expect(apply).toHaveBeenCalledTimes(1);
      expect(await ui.count('[data-pending-receipt]')).toBe(1);
      // The contract applies the whole selection, so both queued rows appear in the receipt.
      expect(await ui.count('[data-pending-applied-id]')).toBe(2);
      expect(await ui.focused('[data-pending-preview-content]')).toBe(true);
      const emptyRegion = document.querySelector<HTMLElement>('[data-pending-preview-content]');
      expect(emptyRegion?.getAttribute('aria-label')).toBe('No pending changes');
      expect(await ui.count('app-empty-pattern')).toBe(1);
      expect(await ui.text()).not.toContain('Choose a queued change');
      expect(await ui.text()).not.toContain('Selected change');
      // An emptied queue either removes the Apply control or leaves it disabled; never focused.
      if ((await ui.count('[data-pending-apply]')) > 0) {
        expect(await ui.disabled('[data-pending-apply]')).toBe(true);
        expect(await ui.focused('[data-pending-apply]')).toBe(false);
      }
    });
  });
  it('moves focus to the preview region after discard removes its opener', async () => {
    const { fixture, ui } = await setup();
    await ui.click('[data-pending-preview]');
    await ui.focusControl('[data-pending-discard]');
    await ui.click('[data-pending-discard]');
    const confirm = consentDialog().querySelector<HTMLElement>('[data-pending-confirm]');
    if (!confirm) throw new Error('Expected the consent commit control');
    confirm.click();
    await fixture.whenStable();
    await manualChangeDetection(async () => {
      expect(discard).toHaveBeenCalledTimes(1);
      expect(discard.mock.calls[0]).toContain(CHANGE);
      expect(await ui.count('[data-pending-discard]')).toBe(0);
      expect(await ui.focused('[data-pending-preview-content]')).toBe(true);
    });
  });
  it('reloads away a blocked replay after a discard removes part of its selection', async () => {
    apply.mockRejectedValueOnce(new ApiRequestError(pendingError('network', 0)));
    const { fixture, ui } = await setup();
    const store = fixture.debugElement.injector.get(PendingChangesStore);
    const confirm = async () => {
      const commit = consentDialog().querySelector<HTMLElement>('[data-pending-confirm]');
      if (!commit) throw new Error('Expected the consent commit control');
      commit.click();
      await fixture.whenStable();
    };
    await ui.click('[data-pending-apply]');
    await confirm();
    expect(apply).toHaveBeenCalledTimes(1);
    expect(store.applyRequest()?.ids).toEqual([CHANGE, ITEM]);
    await ui.click('[data-pending-preview]');
    await ui.click('[data-pending-discard]');
    await confirm();
    expect(discard).toHaveBeenCalledTimes(1);
    expect(store.rows().map((row) => row.change_id)).toEqual([ITEM]);
    expect(store.selectedIds()).toEqual([ITEM]);
    expect(store.applyRequest()?.ids).toEqual([CHANGE, ITEM]);
    await manualChangeDetection(async () => {
      expect(await ui.disabled('[data-pending-apply]')).toBe(true);
      expect(await ui.textAt('#pending-apply-explanation')).toContain('reload the queue');
      expect(await ui.textAt('[data-pending-replay-reload]')).toContain('Reload the queue');
    });
    listPending.mockResolvedValueOnce({ items: [SECOND], next_cursor: null });
    await ui.click('[data-pending-replay-reload]');
    await fixture.whenStable();
    await manualChangeDetection(async () => {
      expect(store.applyRequest()).toBeNull();
      expect(store.selectedIds()).toEqual([ITEM]);
      expect(await ui.count('[data-pending-replay-reload]')).toBe(0);
      expect(await ui.disabled('[data-pending-apply]')).toBe(false);
    });
  });
});
