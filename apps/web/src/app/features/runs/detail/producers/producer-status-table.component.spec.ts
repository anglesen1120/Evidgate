import { provideZonelessChangeDetection, reflectComponentType } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { clearTranslations, loadTranslations } from '@angular/localize';

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
  ) {
    const fixture = TestBed.createComponent(ProducerStatusTableComponent);
    fixture.componentRef.setInput('slots', slots);
    fixture.componentRef.setInput('status', status);
    await fixture.whenStable();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  it('shows supplied finalized facts without turning them into a run result', async () => {
    const { root } = await render();
    expect(root.querySelector('section#producers[data-producer-status]')).not.toBeNull();
    expect(root.querySelector('tr[data-producer-slot="shard-3"]')).not.toBeNull();
    expect(root.querySelector('[data-fact="identity"]')?.textContent).toContain('bound');
    expect(root.querySelector('[data-fact="finalize"]')?.textContent).toContain('finalized');
    expect(root.querySelector('[data-fact="last-sequence"]')?.textContent).toContain('12');
    expect(root.querySelector('[data-fact="count"]')?.textContent).toContain('12 / 12');
    expect(root.querySelectorAll('[data-fact][data-unavailable]')).toHaveLength(0);
    expect(root.querySelector('[data-count-mismatch]')).toBeNull();
    expect(root.textContent).not.toMatch(/PASS|FAIL|Collection complete/);
  });

  it('uses the shared unavailable state for every missing fact, never zero or missing identity', async () => {
    const { root } = await render(producerSlotsFromRun({ declared_producers: ['shard-3'] }));
    const cells = [...root.querySelectorAll('td[data-fact][data-unavailable]')];
    expect(cells).toHaveLength(4);
    expect(cells.every((cell) => cell.querySelector('app-status-pill') !== null)).toBe(true);
    expect(cells.every((cell) => cell.textContent?.includes('Status unavailable'))).toBe(true);
    expect(root.querySelector('[data-count-mismatch]')).toBeNull();
    expect(root.querySelectorAll('[data-badge]')).toHaveLength(0);
    expect(root.querySelector('[data-state="empty"]')).toBeNull();
  });

  it.each([
    { count: 11, expected: 12 },
    { count: 13, expected: 12 },
  ])('marks mismatched counts with text and an icon: %j', async (counts) => {
    const { root } = await render([{ ...FINALIZED, ...counts }]);
    const marker = root.querySelector('[data-count-mismatch]');
    expect(marker?.textContent).toContain('Count does not match expected');
    expect(marker?.querySelector('[aria-hidden="true"]')?.textContent).toBe('≠');
  });

  it.each([
    { count: null, expected: 12 },
    { count: 12, expected: null },
  ])('does not compare a partially unavailable count: %j', async (counts) => {
    const { root } = await render([{ ...FINALIZED, ...counts }]);
    expect(root.querySelector('[data-fact="count"][data-unavailable]')).not.toBeNull();
    expect(root.querySelector('[data-count-mismatch]')).toBeNull();
  });

  it('renders zero counts and sequences as served values', async () => {
    const { root } = await render([{ ...FINALIZED, count: 0, expected: 0, lastSequence: 0 }]);
    expect(root.querySelector('[data-fact="count"]')?.textContent).toContain('0 / 0');
    expect(root.querySelector('[data-fact="last-sequence"]')?.textContent).toContain('0');
    expect(root.querySelector('[data-count-mismatch]')).toBeNull();
  });

  it('shows stale-epoch events as informational badges', async () => {
    const { root } = await render([{ ...FINALIZED, staleEpochEvents: 4 }]);
    expect(root.querySelector('[data-badge="stale-epoch"]')?.textContent).toContain(
      '4 stale-epoch events',
    );
    expect(root.querySelector('[data-badge="after-cancel"]')).toBeNull();
    expect(root.textContent).toContain('do not affect completeness');
  });

  it('shows after-cancel events as informational badges', async () => {
    const { root } = await render([{ ...FINALIZED, afterCancelEvents: 3 }]);
    expect(root.querySelector('[data-badge="after-cancel"]')?.textContent).toContain(
      '3 after-cancel events',
    );
    expect(root.querySelector('[data-badge="stale-epoch"]')).toBeNull();
  });

  it('cannot take or emit completeness and badges leave all result facts unchanged', async () => {
    const metadata = reflectComponentType(ProducerStatusTableComponent);
    expect(metadata?.inputs.map((input) => input.templateName)).toEqual(['slots', 'status']);
    expect(metadata?.outputs).toEqual([]);
    const { fixture, root } = await render();
    const originalFacts = [...root.querySelectorAll('[data-fact]')].map((cell) => cell.outerHTML);
    const originalNote = root.querySelector('#producers-event-note')?.outerHTML;
    const originalAttributes = [
      ...root.querySelectorAll('[data-completeness], [data-run-completeness]'),
    ];
    fixture.componentRef.setInput('slots', [
      { ...FINALIZED, staleEpochEvents: 4, afterCancelEvents: 3 },
    ]);
    await fixture.whenStable();
    expect([...root.querySelectorAll('[data-fact]')].map((cell) => cell.outerHTML)).toEqual(
      originalFacts,
    );
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

  it('distinguishes an unavailable declaration from an empty declaration', async () => {
    const { root } = await render(null);
    expect(root.querySelector('[data-state="unavailable"] app-status-pill')).not.toBeNull();
    expect(root.querySelector('[data-state="empty"]')).toBeNull();
  });

  it('renders 50 full slot ids inside a focusable local scroll region', async () => {
    const slots = Array.from({ length: 50 }, (_, index) => ({
      ...FINALIZED,
      producerId: `shard-${index}-${'long-id'.repeat(20)}`,
    }));
    const { root } = await render(slots);
    expect(root.querySelectorAll('tr[data-producer-slot]')).toHaveLength(50);
    expect(root.querySelector('[data-producer-scroll]')?.getAttribute('tabindex')).toBe('0');
    expect(
      root.querySelector('[data-producer-scroll]')?.classList.contains('overflow-x-auto'),
    ).toBe(true);
    expect(root.querySelectorAll('tbody th')[49]?.textContent?.trim()).toBe(slots[49]?.producerId);
    expect(root.textContent).not.toContain('…');
  });

  it('uses the evidence-link anchor helper even for reserved characters', async () => {
    const producerId = 'Shard #3/%';
    const { root } = await render([{ ...FINALIZED, producerId }]);
    const anchor = root.querySelector('tbody th');
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
      'runs.detail.producers.stale-epoch': '{$count} sự kiện thuộc epoch cũ',
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
      '2 sự kiện thuộc epoch cũ',
    );
    expect(root.querySelector('[data-badge="after-cancel"]')?.textContent).toContain(
      '5 sự kiện sau khi hủy',
    );
  });
});
