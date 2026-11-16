import { provideZonelessChangeDetection, reflectComponentType } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { clearTranslations, loadTranslations } from '@angular/localize';
import { By } from '@angular/platform-browser';
import { DataTableComponent } from '../../../../shared/ui/data-table/data-table.component';
import { useLightweightTablePresentation } from '../../../../shared/ui/data-table/data-table-volume.testing';
import { StatusPillComponent } from '../../../../shared/ui/chips/status-pill.component';

import { stubLayoutApis } from '../../../../shared/testing/layout.testing';
import { evidenceHref, producerAnchorId } from '../evidence/run-evidence-anchors';
import { producerSlotsFromRun, type ProducerSlot, type ProducerStatus } from './producer-slots';
import { ProducerStatusTableComponent } from './producer-status-table.component';

const FINALIZED: ProducerSlot = {
  producerId: 'shard-3',
  identityState: 'bound',
  finalizeStatus: 'finalized',
  lastSequence: 12,
  count: 12,
  expected: 12,
  staleEpochEvents: 0,
  afterCancelEvents: 0,
};

/**
 * Every fact cell's markup without `style`: a transition the host applies while the DOM settles
 * toggles that attribute under load and says nothing about the fact the cell reports.
 */
function factSnapshots(root: HTMLElement): string[] {
  return [...root.querySelectorAll('[data-fact]')].map((cell) => {
    const copy = cell.cloneNode(true) as HTMLElement;
    copy.removeAttribute('style');
    copy.querySelectorAll('[style]').forEach((node) => node.removeAttribute('style'));
    return copy.outerHTML;
  });
}

describe('ProducerStatusTableComponent', () => {
  beforeEach(() => {
    stubLayoutApis();
    TestBed.configureTestingModule({
      imports: [ProducerStatusTableComponent],
      providers: [provideZonelessChangeDetection()],
    });
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    clearTranslations();
    vi.unstubAllGlobals();
  });

  async function render(
    slots: readonly ProducerSlot[] | null = [FINALIZED],
    status: ProducerStatus = 'ready',
    runAfterCancelEvents: number | null = null,
    targetAnchor: string | null = null,
  ) {
    const fixture = TestBed.createComponent(ProducerStatusTableComponent);
    fixture.componentRef.setInput('slots', slots);
    fixture.componentRef.setInput('status', status);
    fixture.componentRef.setInput('runAfterCancelEvents', runAfterCancelEvents);
    fixture.componentRef.setInput('targetAnchor', targetAnchor);
    await fixture.whenStable();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  it('uses page flow and independent readable column budgets, with spare space reserved for notes', async () => {
    const { fixture, root } = await render();
    const table = fixture.debugElement.query(By.directive(DataTableComponent))
      .componentInstance as DataTableComponent<ProducerSlot>;
    expect(table.flow()).toBe('page');
    expect(table.fitToContainer()).toBe(true);
    expect(root.querySelector('.eg-table-long')).toBeNull();
    expect(table.columns().map((column) => column.width?.preferred)).toEqual([
      '1%',
      '1%',
      '1%',
      '1%',
      '1%',
      'auto',
    ]);
    expect(table.columns().find((column) => column.id === 'count')?.width?.min).toBe(
      'calc(18ch + 2 * var(--eg-space-4))',
    );
    const identity = root.querySelector('[data-producer-slot]');
    expect(identity?.classList.contains('flex')).toBe(true);
    expect(identity?.querySelector('app-copy-value-button hlm-icon')).not.toBeNull();
  });

  it('shows supplied finalized facts without turning them into a run result', async () => {
    const { root } = await render();
    expect(root.querySelector('section#producers[data-producer-status]')).not.toBeNull();
    expect(root.querySelector('tr[data-row-id="shard-3"]')).not.toBeNull();
    expect(root.querySelector('[data-fact="identity"]')?.textContent).toContain('Bound');
    expect(root.querySelector('[data-fact="finalize"]')?.textContent).toContain('Finalized');
    expect(root.querySelector('[data-fact="last-sequence"]')?.textContent).toContain('12');
    expect(root.querySelector('[data-fact="count"]')?.textContent).toContain('12 / 12');
    expect(root.querySelectorAll('[data-fact][data-unavailable]')).toHaveLength(0);
    expect(root.querySelector('[data-count-mismatch]')).toBeNull();
    expect(root.textContent).not.toMatch(/PASS|FAIL|Collection complete/);
  });

  it('keeps status fallbacks for statuses and plain not provided copy for missing facts', async () => {
    const { root } = await render(
      producerSlotsFromRun({ declared_producers: ['shard-3'], producers: [] }),
    );
    const cells = [...root.querySelectorAll('[data-fact][data-unavailable]')];
    expect(cells).toHaveLength(4);
    expect(cells.slice(0, 2).every((cell) => cell.querySelector('app-status-pill') !== null)).toBe(
      true,
    );
    expect(
      cells.slice(0, 2).every((cell) => cell.textContent?.includes('Status unavailable')),
    ).toBe(true);
    expect(cells.slice(2).every((cell) => cell.querySelector('app-status-pill') === null)).toBe(
      true,
    );
    expect(cells.slice(2).every((cell) => cell.textContent?.includes('Not provided'))).toBe(true);
    expect(root.querySelector('[data-count-mismatch]')).toBeNull();
    expect(root.querySelectorAll('[data-badge]')).toHaveLength(0);
    expect(root.querySelector('[data-state="empty"]')).toBeNull();
  });

  it('preserves unknown producer codes and caller-written slot identifiers', async () => {
    const { root } = await render([
      {
        ...FINALIZED,
        producerId: 'bound',
        identityState: 'future_identity',
        finalizeStatus: 'future_finalize',
      },
    ]);
    expect(root.querySelector('[data-producer-slot] bdi')?.textContent).toBe('bound');
    expect(root.querySelector('[data-fact="identity"] bdi')?.textContent).toBe('future_identity');
    expect(root.querySelector('[data-fact="finalize"] bdi')?.textContent).toBe('future_finalize');
  });

  it.each([
    { count: 11, expected: 12 },
    { count: 13, expected: 12 },
  ])(
    'marks mismatched counts with a compact label, full reason and warning icon: %j',
    async (counts) => {
      const { fixture, root } = await render([{ ...FINALIZED, ...counts }]);
      const marker = root.querySelector('[data-count-mismatch]');
      const pill = fixture.debugElement.query(By.css('[data-count-mismatch]'))
        .componentInstance as StatusPillComponent;
      expect(pill.label()).toBe('Mismatch');
      expect(pill.fullValue()).toBe('Count does not match expected');
      expect(marker?.querySelector('hlm-icon')).not.toBeNull();
      expect(marker?.querySelector('.status-pill--warning')).not.toBeNull();
    },
  );

  it('does not compare or fabricate a count when the count is absent', async () => {
    const { root } = await render([{ ...FINALIZED, count: null, expected: 12 }]);
    const cell = root.querySelector('[data-fact="count"]');
    expect(cell?.hasAttribute('data-unavailable')).toBe(true);
    expect(cell?.textContent).toContain('Not provided');
    expect(root.querySelector('[data-count-mismatch]')).toBeNull();
  });

  it('shows the served count of an open slot with an accessible label and no mismatch', async () => {
    const { fixture, root } = await render([{ ...FINALIZED, count: 3, expected: null }]);
    const cell = root.querySelector('[data-fact="count"]');
    expect(cell?.hasAttribute('data-unavailable')).toBe(false);
    expect(cell?.querySelector('app-number-value')?.textContent).toBe('3');
    expect(cell?.textContent).toContain('Expected unset');
    const pill = fixture.debugElement.query(By.css('[data-open-slot] app-status-pill'))
      .componentInstance as StatusPillComponent;
    expect(pill.fullValue()).toBe('3 served, expected count not set yet');
    expect(cell?.getAttribute('aria-label')).toBe('3 served, expected count not set yet');
    expect(root.querySelector('[data-count-mismatch]')).toBeNull();
  });

  it('shows a zero count of an open slot as a served value', async () => {
    const { root } = await render([{ ...FINALIZED, count: 0, expected: null }]);
    const cell = root.querySelector('[data-fact="count"]');
    expect(cell?.hasAttribute('data-unavailable')).toBe(false);
    expect(cell?.getAttribute('aria-label')).toBe('0 served, expected count not set yet');
  });

  it('keeps the existing count / expected format and no label when expected is set', async () => {
    const { root } = await render([FINALIZED]);
    const cell = root.querySelector('[data-fact="count"]');
    expect(cell?.textContent).toContain('12 / 12');
    expect(cell?.hasAttribute('aria-label')).toBe(false);
  });

  it('renders zero counts and sequences as served values', async () => {
    const { root } = await render([{ ...FINALIZED, count: 0, expected: 0, lastSequence: 0 }]);
    expect(root.querySelector('[data-fact="count"]')?.textContent).toContain('0 / 0');
    expect(root.querySelector('[data-fact="last-sequence"]')?.textContent).toContain('0');
    expect(root.querySelector('[data-count-mismatch]')).toBeNull();
  });

  it('shows stale-epoch events as a badge and says they leave the evidence incomplete', async () => {
    const { root } = await render([{ ...FINALIZED, staleEpochEvents: 4 }]);
    expect(root.querySelector('[data-badge="stale-epoch"]')?.textContent).toContain(
      '4 stale-epoch events',
    );
    expect(root.querySelector('[data-badge="after-cancel"]')).toBeNull();
    expect(root.textContent).toContain('After-cancel event counts are informational only');
    expect(root.textContent).toContain(
      'a stale-epoch event on a closed shard makes the evidence incomplete',
    );
  });

  it('shows after-cancel events as informational badges', async () => {
    const { root } = await render([{ ...FINALIZED, afterCancelEvents: 3 }]);
    expect(root.querySelector('[data-badge="after-cancel"]')?.textContent).toContain(
      '3 after-cancel events',
    );
    expect(root.querySelector('[data-badge="stale-epoch"]')).toBeNull();
  });

  it('shows a run-level count once without assigning it to any producer', async () => {
    const slots = producerSlotsFromRun({
      declared_producers: ['shard-1', 'shard-2'],
      producers: [],
    });
    const { root } = await render(slots, 'ready', 3);
    expect(root.querySelectorAll('[data-badge="after-cancel"]')).toHaveLength(1);
    expect(root.querySelector('[data-run-after-cancel]')?.textContent).toContain(
      '3 after-cancel events for this run',
    );
    expect(root.querySelector('tbody [data-badge="after-cancel"]')).toBeNull();
    expect(root.querySelectorAll('[data-fact][data-unavailable]')).toHaveLength(8);
  });

  it.each([null, 0])('does not show a run-level badge for %s', async (count) => {
    const { root } = await render([FINALIZED], 'ready', count);
    expect(root.querySelector('[data-run-after-cancel]')).toBeNull();
  });

  it('uses singular event labels when exactly one event is supplied', async () => {
    const { root } = await render(
      [{ ...FINALIZED, staleEpochEvents: 1, afterCancelEvents: 1 }],
      'ready',
      1,
    );
    expect(
      root.querySelector('[data-badge="stale-epoch"] .status-pill__detail')?.textContent?.trim(),
    ).toBe('1 stale-epoch event');
    expect(
      root
        .querySelector('tbody [data-badge="after-cancel"] .status-pill__detail')
        ?.textContent?.trim(),
    ).toBe('1 after-cancel event');
    expect(
      root.querySelector('[data-run-after-cancel] .status-pill__detail')?.textContent?.trim(),
    ).toBe('1 after-cancel event for this run');
  });

  it('cannot take or emit completeness and badges leave all result facts unchanged', async () => {
    const metadata = reflectComponentType(ProducerStatusTableComponent);
    expect(metadata?.inputs.map((input) => input.templateName)).toEqual([
      'slots',
      'status',
      'runAfterCancelEvents',
      'targetAnchor',
    ]);
    expect(metadata?.outputs).toEqual([]);
    const { fixture, root } = await render();
    const originalFacts = factSnapshots(root);
    const originalNote = root.querySelector('#producers-event-note')?.outerHTML;
    const originalAttributes = [
      ...root.querySelectorAll('[data-completeness], [data-run-completeness]'),
    ];
    fixture.componentRef.setInput('slots', [{ ...FINALIZED, staleEpochEvents: 4 }]);
    fixture.componentRef.setInput('runAfterCancelEvents', 3);
    await fixture.whenStable();
    expect(factSnapshots(root)).toEqual(originalFacts);
    expect(root.querySelector('#producers-event-note')?.outerHTML).toBe(originalNote);
    expect(root.querySelectorAll('[data-badge]')).toHaveLength(2);
    expect([...root.querySelectorAll('[data-completeness], [data-run-completeness]')]).toEqual(
      originalAttributes,
    );
    expect(originalAttributes).toHaveLength(0);
    expect(root.textContent).not.toMatch(/Collection complete|Collection incomplete|PASS|FAIL/);
  });

  it('explains zero declared producers with a shared empty state', async () => {
    const { root } = await render([]);
    expect(root.querySelector('app-empty-pattern[data-state="empty"]')?.textContent).toContain(
      'No declared producers',
    );
    expect(root.textContent).toContain('This is not a collection result.');
    expect(root.querySelector('table')).toBeNull();
  });

  it.each(['loading', 'error'] as const)(
    'renders the shared %s region without stale facts',
    async (status) => {
      const { root } = await render([FINALIZED], status);
      expect(root.querySelector(`[data-state="${status}"]`)).not.toBeNull();
      expect(root.querySelector('table')).toBeNull();
    },
  );

  it('distinguishes an absent declaration with plain not provided from a known empty declaration', async () => {
    const { root } = await render(null);
    expect(root.querySelector('[data-state="unavailable"]')?.textContent).toContain('Not provided');
    expect(root.querySelector('[data-state="unavailable"] app-status-pill')).toBeNull();
    expect(root.querySelector('[data-state="empty"]')).toBeNull();
  });

  it('uses the shared page-flow table without a local sticky-header scroller for long producer lists', async () => {
    const slots = Array.from({ length: 50 }, (_, index) => ({
      ...FINALIZED,
      producerId: `shard-${index}-${'long-id'.repeat(20)}`,
    }));
    const { root } = await render(slots);
    expect(root.querySelectorAll('tr[data-row-id]')).toHaveLength(50);
    expect(root.querySelector('[data-producer-scroll] .eg-table-long')).toBeNull();
    expect(root.querySelector('[data-producer-scroll] .eg-table-sticky')).toBeNull();
    expect(root.querySelector('app-data-table')?.getAttribute('style')).toContain(
      '--eg-table-header-height: 0px',
    );
    expect(root.querySelector('.eg-table-layout')).not.toBeNull();
    const identities = [...root.querySelectorAll('[data-producer-slot]')];
    expect(identities).toHaveLength(50);
    expect(identities.map((identity) => identity.getAttribute('data-producer-slot'))).toEqual(
      slots.map((slot) => slot.producerId),
    );
    expect(identities.at(-1)?.querySelector(':scope > bdi')?.textContent).toContain(
      slots.at(-1)?.producerId,
    );
    expect([...root.querySelectorAll('th[data-column-role="number"]')]).toHaveLength(2);
  });

  // Volume cases still render real 200-row tables; retain their existing DOM-test budget.
  it.each([200, 201])(
    'bounds %i producers to pages of at most 200 rows',
    async (count) => {
      useLightweightTablePresentation();
      const slots = Array.from({ length: count }, (_, index) => ({
        ...FINALIZED,
        producerId: `shard-${index}`,
      }));
      const { fixture, root } = await render(slots);
      expect(root.querySelectorAll('tr[data-row-id]')).toHaveLength(200);
      const next = root.querySelector<HTMLButtonElement>('[data-producer-next]');
      if (count === 200) {
        expect(next).toBeNull();
      } else {
        expect(next?.disabled).toBe(false);
        next?.click();
        await fixture.whenStable();
        expect(root.querySelectorAll('tr[data-row-id]')).toHaveLength(1);
        expect(root.querySelector('tr[data-row-id]')?.getAttribute('data-row-id')).toBe(
          'shard-200',
        );
        expect(next?.disabled).toBe(true);
        expect(root.querySelector('[data-producer-slot]')?.id).toBe('producer-shard-200');
        expect(root.querySelectorAll('[data-fact]')).toHaveLength(4);
        root.querySelector<HTMLButtonElement>('[data-producer-previous]')?.click();
        await fixture.whenStable();
        expect(root.querySelectorAll('tr[data-row-id]')).toHaveLength(200);
      }
    },
    15_000,
  );

  it('selects the page containing a producer fragment and preserves manual pages on polling', async () => {
    useLightweightTablePresentation();
    const slots = Array.from({ length: 256 }, (_, index) => ({
      ...FINALIZED,
      producerId: `shard-${index}`,
    }));
    const { fixture, root } = await render(slots, 'ready', null, 'producer-shard-255');
    expect(root.querySelector('#producer-shard-255')).not.toBeNull();
    expect(root.querySelectorAll('tr[data-row-id]')).toHaveLength(56);
    root.querySelector<HTMLButtonElement>('[data-producer-previous]')?.click();
    await fixture.whenStable();
    fixture.componentRef.setInput('slots', [...slots]);
    await fixture.whenStable();
    expect(root.querySelector('#producer-shard-0')).not.toBeNull();
    expect(root.querySelector('#producer-shard-255')).toBeNull();
  });

  it('localizes the run-level singular count and page controls into Vietnamese', async () => {
    useLightweightTablePresentation();
    loadTranslations({
      'runs.detail.producers.run-after-cancel-one': '1 sự kiện sau khi hủy của lượt chạy này',
      'runs.detail.producers.previous': 'Nguồn gửi dữ liệu trước',
      'runs.detail.producers.next': 'Nguồn gửi dữ liệu tiếp theo',
      'runs.detail.producers.page': 'Trang {$page} / {$pages}',
    });
    const slots = Array.from({ length: 201 }, (_, index) => ({
      ...FINALIZED,
      producerId: `shard-${index}`,
    }));
    const { root } = await render(slots, 'ready', 1);
    expect(
      root.querySelector('[data-run-after-cancel] .status-pill__detail')?.textContent?.trim(),
    ).toBe('1 sự kiện sau khi hủy của lượt chạy này');
    expect(root.querySelector('[data-producer-next]')?.textContent).toContain(
      'Nguồn gửi dữ liệu tiếp theo',
    );
    expect(root.querySelector('nav [role="status"]')?.textContent).toBe('Trang 1 / 2');
  });

  it('uses the evidence-link anchor helper even for reserved characters', async () => {
    const producerId = 'Shard #3/%';
    const { root } = await render([{ ...FINALIZED, producerId }]);
    const anchor = root.querySelector('[data-producer-slot]');
    expect(anchor?.id).toBe(producerAnchorId(producerId));
    expect(decodeURIComponent(evidenceHref({ kind: 'producer', shard: producerId }).slice(1))).toBe(
      anchor?.id,
    );
    expect(anchor?.getAttribute('tabindex')).toBe('-1');
  });

  it('renders Vietnamese labels, unavailable copy and informational counts', async () => {
    loadTranslations({
      'runs.detail.producers.title': 'Nguồn gửi dữ liệu',
      'runs.detail.producers.identity': 'Trạng thái định danh đã liên kết',
      'runs.detail.producers.finalize': 'Trạng thái chốt dữ liệu',
      'runs.detail.producers.count': 'Số lượng / dự kiến',
      'runs.detail.producers.stale-epoch': '{$count} sự kiện thuộc thế hệ cũ',
      'runs.detail.producers.after-cancel': '{$count} sự kiện sau khi hủy',
      'shared.status-pill.unavailable': 'Chưa có trạng thái',
    });
    const { root } = await render([
      { ...FINALIZED, identityState: null, staleEpochEvents: 2, afterCancelEvents: 5 },
    ]);
    expect(root.querySelector('h2')?.textContent).toBe('Nguồn gửi dữ liệu');
    expect(root.querySelector('[data-fact="identity"]')?.textContent).toContain(
      'Chưa có trạng thái',
    );
    expect(root.textContent).toContain('Trạng thái chốt dữ liệu');
    expect(root.textContent).toContain('Số lượng / dự kiến');
    expect(root.querySelector('[data-badge="stale-epoch"]')?.textContent).toContain(
      '2 sự kiện thuộc thế hệ cũ',
    );
    expect(root.querySelector('[data-badge="after-cancel"]')?.textContent).toContain(
      '5 sự kiện sau khi hủy',
    );
  });
  it('renders all six producer facts in shared stacked records with full identifiers', async () => {
    const producerId = 'shard-' + 'long-'.repeat(80);
    const { fixture, root } = await render([{ ...FINALIZED, producerId, staleEpochEvents: 2 }]);
    fixture.debugElement
      .query(By.directive(DataTableComponent))
      .injector.get(DataTableComponent)
      .narrow.set(true);
    await fixture.whenStable();
    const record = root.querySelector('.eg-table-record');
    expect(record?.querySelectorAll('dt')).toHaveLength(6);
    expect(record?.querySelectorAll('dd')).toHaveLength(6);
    expect(record?.querySelector('[data-producer-slot] bdi')?.textContent).toBe(producerId);
    expect(record?.textContent).toContain('12 / 12');
    expect(record?.textContent).toContain('2 stale-epoch events');
    expect(root.querySelector('.eg-table-narrow')).not.toBeNull();
  });
  it('shows escaped producer format characters and refuses clipboard copying', async () => {
    const producerId = 'shard-\u202e<script>';
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', { clipboard: { writeText } });
    const { fixture, root } = await render([{ ...FINALIZED, producerId }]);
    expect(root.querySelector('[data-producer-slot] bdi')?.textContent).toBe(
      'shard-⟨U+202E⟩<script>',
    );
    const copyButton = root.querySelector<HTMLButtonElement>(
      '[data-producer-slot] app-copy-value-button button',
    );
    copyButton?.click();
    await fixture.whenStable();
    expect(writeText).not.toHaveBeenCalled();
    expect(copyButton?.textContent?.trim()).toBe('Copy failed');
    expect(
      root.querySelector('[data-producer-slot] app-copy-value-button [role="status"]')?.textContent,
    ).toBe('Could not copy Producer slot. Show the full value and copy it manually.');
    expect(root.querySelector('script')).toBeNull();
    expect(root.textContent).not.toContain('\u202e');
  });

  it('copies an ordinary producer identifier exactly and announces success', async () => {
    const producerId = 'shard-ordinary-49';
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', { clipboard: { writeText } });
    const { fixture, root } = await render([{ ...FINALIZED, producerId }]);
    const identity = root.querySelector('[data-producer-slot]');
    expect(identity?.querySelector(':scope > bdi')?.textContent).toBe(producerId);
    identity?.querySelector<HTMLButtonElement>('app-copy-value-button button')?.click();
    await fixture.whenStable();
    expect(writeText).toHaveBeenCalledTimes(1);
    expect(writeText).toHaveBeenCalledWith(producerId);
    expect(identity?.querySelector('app-copy-value-button [role="status"]')?.textContent).toBe(
      'Producer slot copied.',
    );
  });
});
