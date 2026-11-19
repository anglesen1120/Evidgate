import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { ApiRequestError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { AuthoringPendingDataService } from '../../../core/data/authoring-pending-data.service';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { PendingChangesPage } from './pending-changes.page';
import { PendingChangesStore } from './pending-changes.store';
import { PENDING_COPY } from './pending-changes.copy';
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
describe('PendingChangesPage', () => {
  const writable = signal(true);
  const desktop = signal(true);
  const listPending = vi.fn<AuthoringPendingDataService['listPending']>();
  const selection = vi.fn<AuthoringPendingDataService['selection']>();
  const apply = vi.fn<AuthoringPendingDataService['apply']>();
  const discard = vi.fn<AuthoringPendingDataService['discard']>();
  beforeEach(() => {
    writable.set(true);
    desktop.set(true);
    listPending.mockReset().mockResolvedValue({ items: [pendingRow()], next_cursor: null });
    selection.mockReset().mockResolvedValue({ items: [pendingRow()], batchEtag: '"batch"' });
    apply.mockReset().mockResolvedValue({
      batch_id: CHANGE,
      applied_change_ids: [CHANGE],
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
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: { data: { pageHeadingLevel: 1 } },
            data: of({ pageHeadingLevel: 1 }),
          },
        },
        {
          provide: SessionStore,
          useValue: {
            currentProject: signal({ id: PROJECT }),
            has: (code: string) => code.endsWith(':read') || writable(),
          },
        },
        {
          provide: NarrowMutationPolicy,
          useValue: {
            canMutate: desktop,
            narrow: computed(() => !desktop()),
            allowsMutation: () => desktop(),
          },
        },
        {
          provide: AuthoringPendingDataService,
          useValue: {
            listPending,
            selection,
            apply,
            discard,
            checklistItems: vi.fn().mockResolvedValue({ items: [], next_cursor: null }),
          },
        },
      ],
    });
  });
  async function setup() {
    const fixture = TestBed.createComponent(PendingChangesPage);
    fixture.detectChanges();
    await fixture.whenStable();
    return {
      fixture,
      store: fixture.debugElement.injector.get(PendingChangesStore),
      ui: await TestbedHarnessEnvironment.harnessForFixture(fixture, PendingUiHarness),
      root: TestbedHarnessEnvironment.documentRootLoader(fixture),
    };
  }
  it('renders a confirmed empty queue without an apply mutation', async () => {
    listPending.mockResolvedValue({ items: [], next_cursor: null });
    const { ui } = await setup();
    expect(await ui.text()).toContain(PENDING_COPY.emptyTitle);
    expect(apply).not.toHaveBeenCalled();
  });
  it('renders rows, exact selection, full escaped comment and no row apply', async () => {
    const { ui } = await setup();
    expect(await ui.text()).toContain('Review this change');
    await ui.click('[data-pending-preview]');
    expect(await ui.textAt('[data-pending-preview-content]')).toContain(
      '<script>plain text</script>',
    );
    expect(await ui.textAt('[data-pending-preview-content]')).toContain(
      'Long comment. '.repeat(80).trim(),
    );
    expect(await ui.count('script')).toBe(0);
    expect(await ui.count('[data-pending-apply]')).toBe(1);
  });
  it.each(['comment', 'delete', 'merge', 'split'] as const)(
    'shows the %s plain-text preview and supplied lineage',
    async (kind) => {
      listPending.mockResolvedValue({ items: [pendingRow(kind)], next_cursor: null });
      const { ui } = await setup();
      await ui.click('[data-pending-preview]');
      const preview = await ui.textAt('[data-pending-preview-content]');
      expect(preview).toContain(
        PENDING_COPY[
          kind === 'comment'
            ? 'commentLabel'
            : kind === 'delete'
              ? 'deleteLabel'
              : kind === 'merge'
                ? 'mergeLabel'
                : 'splitLabel'
        ],
      );
      expect(preview).toContain(
        kind === 'split' ? 'New split item' : kind === 'comment' ? 'plain text' : ITEM,
      );
    },
  );
  it('requires explicit confirmation with Cancel focused and shows comment-only receipt zero', async () => {
    const { ui, root, fixture } = await setup();
    await ui.click('[data-pending-apply]');
    const dialog = await root.getHarness(PendingConsentHarness);
    expect(await dialog.cancelFocused()).toBe(true);
    expect(await dialog.text()).toContain(CHANGE);
    expect(apply).not.toHaveBeenCalled();
    await dialog.confirm();
    await fixture.whenStable();
    expect(apply).toHaveBeenCalledTimes(1);
    expect(await ui.textAt('[data-pending-receipt]')).toContain(PENDING_COPY.receiptCommentOnly);
  }, 30_000);
  it('keeps queue and local reason unchanged after 412 until explicit Reload', async () => {
    apply.mockRejectedValue(new ApiRequestError(pendingError('precondition_failed', 412)));
    const { ui, root, store, fixture } = await setup();
    store.setReason('Keep this draft');
    await ui.click('[data-pending-apply]');
    await (await root.getHarness(PendingConsentHarness)).confirm();
    await fixture.whenStable();
    expect(store.rows()).toHaveLength(1);
    expect(store.localReason()).toBe('Keep this draft');
    expect(listPending).toHaveBeenCalledTimes(1);
    expect(await root.getAllHarnesses(PendingConsentHarness)).toHaveLength(0);
    expect(store.conflict()).not.toBeNull();
    expect(await ui.count('app-conflict-dialog')).toBe(1);
  }, 30_000);
  it('keeps the readable queue and disables writes after 403 write refusal', async () => {
    apply.mockRejectedValue(new ApiRequestError(pendingError('forbidden', 403)));
    const { ui, root, fixture } = await setup();
    await ui.click('[data-pending-apply]');
    await (await root.getHarness(PendingConsentHarness)).confirm();
    writable.set(false);
    await fixture.whenStable();
    expect(await ui.text()).toContain(PENDING_COPY.readOnly);
    expect(await ui.text()).toContain('Review this change');
    expect(await ui.disabled('[data-pending-apply]')).toBe(true);
  }, 30_000);
  it.each(['merge', 'split'] as const)('blocks %s apply with an explanation', async (kind) => {
    listPending.mockResolvedValue({ items: [pendingRow(kind)], next_cursor: null });
    const { ui } = await setup();
    expect(await ui.disabled('[data-pending-apply]')).toBe(true);
    expect(await ui.text()).toContain(PENDING_COPY.blockedStructural);
    expect(apply).not.toHaveBeenCalled();
  });
  it('removes mutations and consent on narrow resize but retains readable facts', async () => {
    const { ui, root, fixture } = await setup();
    await ui.click('[data-pending-apply]');
    desktop.set(false);
    await fixture.whenStable();
    expect(await root.getAllHarnesses(PendingConsentHarness)).toHaveLength(0);
    expect(await ui.count('[data-pending-apply]')).toBe(0);
    expect(await ui.text()).toContain('Review this change');
    expect(apply).not.toHaveBeenCalled();
  }, 30_000);
  it('uses the forbidden request pattern for read denial', async () => {
    listPending.mockRejectedValue(new ApiRequestError(pendingError('forbidden', 403)));
    const { ui } = await setup();
    expect(await ui.count('app-forbidden-pattern')).toBe(1);
    expect(await ui.count('app-data-table')).toBe(0);
  });
});
