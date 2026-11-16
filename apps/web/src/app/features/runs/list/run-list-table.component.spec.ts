import { formatDate, registerLocaleData } from '@angular/common';
import viLocale from '@angular/common/locales/vi';
import { LOCALE_ID, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { By } from '@angular/platform-browser';
import { DataTableComponent } from '../../../shared/ui/data-table/data-table.component';
import type { RunSummary } from '@evidgate/sdk';
import { RunListTableComponent } from './run-list-table.component';

function run(index: number, changes: Partial<RunSummary> = {}): RunSummary {
  return {
    run_id: `018f0000-0000-7000-8000-${String(index).padStart(12, '0')}`,
    state: 'complete',
    created_at: '2026-10-27T00:00:00Z',
    ref: {
      kind: 'branch',
      name: 'main',
      pr_number: null,
      head_sha: null,
      base_sha: null,
      trigger_event: 'push',
      is_fork: false,
    },
    unverified_ref: false,
    decision: null,
    completeness: 'complete',
    parent_run_id: null,
    rerun_scope: null,
    ...changes,
  };
}

// Applies to every test in this file. Idle wall of the slowest is 330 ms (250-row test); the smoke run hit the
// 5000 ms default here after the fuzz stage (CPU contention), so 15 s keeps a ~1.3x margin only (11.3 s of 15 s under smoke load).
describe('RunListTableComponent', { timeout: 15_000 }, () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [RunListTableComponent],
      providers: [provideZonelessChangeDetection(), provideRouter([])],
    }),
  );
  afterEach(() => TestBed.resetTestingModule());
  async function render(rows: readonly RunSummary[]) {
    const fixture = TestBed.createComponent(RunListTableComponent);
    fixture.componentRef.setInput('rows', rows);
    await fixture.whenStable();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }
  it('uses Helm tables and badges while retaining a single accessible table', async () => {
    const { root } = await render([run(1, { decision: 'PASS', rerun_label: 'failed_subset' })]);
    expect(root.querySelectorAll('[role="table"]')).toHaveLength(1);
    expect(root.querySelectorAll('table[data-slot="table"][role="presentation"]')).toHaveLength(1);
    expect(root.querySelector('[data-run-row] a')).not.toBeNull();
    expect(root.querySelector('[data-run-decision] [hlmBadge]')).not.toBeNull();
    expect(root.querySelector('[data-run-rerun] [hlmBadge]')).not.toBeNull();
    expect(root.querySelector('.eg-table-layout')).not.toBeNull();
    expect(root.querySelector('.eg-table-long')).not.toBeNull();
  });
  it('budgets all seven desktop facts while retaining complete identifier provenance', async () => {
    const { fixture, root } = await render([run(1)]);
    const table = fixture.debugElement
      .query(By.directive(DataTableComponent))
      .injector.get(DataTableComponent);
    expect(table.fitToContainer()).toBe(true);
    const minimums = {
      id: 'calc(17ch + var(--eg-space-4) * 2)',
      state: 'calc(12ch + var(--eg-space-4) * 2)',
      evidence: 'calc(14ch + var(--eg-space-4) * 2)',
      decision: 'calc(15ch + var(--eg-space-4) * 2)',
      created: '18ch',
    };
    for (const [id, minimum] of Object.entries(minimums)) {
      expect(table.columns().find((column) => column.id === id)?.width?.min).toBe(minimum);
      const cells = root.querySelectorAll<HTMLElement>(`th.cdk-column-${id},td.cdk-column-${id}`);
      expect(cells).toHaveLength(2);
      for (const cell of cells) {
        expect(cell.style.getPropertyValue('min-inline-size')).toBe(minimum);
      }
    }
    expect(root.querySelectorAll('[role="columnheader"]')).toHaveLength(7);
    const identity = root.querySelector('[data-run-row] a');
    expect(identity?.textContent?.trim()).toBe('018f0000…00000001');
    expect(identity?.getAttribute('aria-label')).toContain(run(1).run_id);
  });
  it('keeps the empty table verdict-free with only the header row counted', async () => {
    const { root } = await render([]);
    expect(root.querySelector('[role="table"]')?.getAttribute('aria-rowcount')).toBe('1');
    expect(root.querySelector('[data-run-row]')).toBeNull();
    expect(root.querySelector('app-decision-chip')).toBeNull();
  });
  it('links the row id to its detail child and preserves the current filter query', async () => {
    await TestBed.inject(Router).navigateByUrl('/?state=complete&ref=main');
    const { root } = await render([run(1)]);
    const link = root.querySelector<HTMLAnchorElement>('.identifier a')!;
    expect(link.getAttribute('href')).toBe(`/${run(1).run_id}?state=complete&ref=main`);
    expect(link.textContent).toContain(run(1).run_id.slice(0, 8));
    expect(link.getAttribute('aria-label')).toBe(`${link.textContent?.trim()} (${run(1).run_id})`);
    expect(link.classList.contains('whitespace-nowrap')).toBe(true);
    expect(link.closest('.identifier')?.classList.contains('min-w-max')).toBe(true);
  });
  it('keeps collection state, evidence unavailable and stored decisions independent; no outcome column', async () => {
    const unavailable = run(5);
    delete unavailable.decision;
    const { root } = await render([
      run(1, { decision: 'PASS' }),
      run(2, { state: 'incomplete', decision: 'FAIL' }),
      run(3, { decision: 'INCONCLUSIVE' }),
      run(4, { decision: null }),
      unavailable,
    ]);
    expect(
      [...root.querySelectorAll('[role="columnheader"]')].map((cell) => cell.textContent?.trim()),
    ).toEqual(['Run id', 'State', 'Evidence', 'Decision', 'Ref', 'Run scope', 'Created (UTC)']);
    expect(root.querySelectorAll('[data-run-evidence] .status-pill--neutral')).toHaveLength(5);
    expect(root.querySelector('[data-run-evidence] .status-pill--success')).toBeNull();
    expect(root.querySelectorAll('[data-run-evidence-hint]')).toHaveLength(1);
    expect(root.querySelector('[data-run-evidence-hint]')?.textContent).toContain(
      'Open the run for evidence completeness.',
    );
    expect(
      [...root.querySelectorAll('[data-run-decision]')].map((cell) => ({
        dimension: cell.querySelector('.verdict__label')?.textContent?.trim(),
        value: cell.querySelector('.verdict__value')?.textContent?.trim(),
      })),
    ).toEqual([
      { dimension: 'Decision:', value: 'PASS' },
      { dimension: 'Decision:', value: 'FAIL' },
      { dimension: 'Decision:', value: 'INCONCLUSIVE' },
      { dimension: 'Decision:', value: 'Not evaluated' },
      { dimension: 'Decision:', value: 'Unavailable' },
    ]);
    expect(root.querySelector('app-outcome-pill')).toBeNull();
    expect(root.querySelector('time')?.getAttribute('datetime')).toBe('2026-10-27T00:00:00Z');
    expect(root.querySelector('[data-run-row] a')?.getAttribute('aria-label')).toContain(
      run(1).run_id,
    );
    expect(root.querySelectorAll('[data-run-decision] [data-tone="pending"]')).toHaveLength(1);
    expect(root.querySelectorAll('[data-run-decision] [data-tone="neutral"]')).toHaveLength(1);
    expect(root.querySelector('[data-run-decision] .verdict__detail')?.textContent?.trim()).toBe(
      'The decision could not be read. Try again.',
    );
    expect(root.querySelector('[data-run-decision] [hlmBadge] .verdict__detail')).toBeNull();
    expect(root.querySelector('[data-run-decision] [title]')).toBeNull();
  });
  it('keeps dense state and evidence labels short with the explanation outside the badge', async () => {
    const { root } = await render([run(1)]);
    expect(root.querySelector('app-run-state-pill')?.textContent?.trim()).toBe('Complete');
    const evidence = root.querySelector('[data-run-evidence]');
    expect(evidence?.querySelector('app-status-pill')?.textContent?.trim()).toBe('Unavailable');
    expect(root.querySelector('[data-run-evidence-hint]')?.textContent).toContain(
      'Open the run for evidence completeness.',
    );
    expect(evidence?.textContent).not.toContain('Open the run for evidence completeness.');
    expect(evidence?.querySelector('app-status-pill')?.textContent).not.toContain('Open the run');
  });
  it('uses the shared UTC absolute-time format without losing the server instant', async () => {
    const instant = '2026-11-02T18:47:05.130948+07:00';
    const { root } = await render([run(1, { created_at: instant })]);
    const time = root.querySelector('time');
    expect(time?.getAttribute('datetime')).toBe(instant);
    expect(time?.textContent?.replace(/\s+/g, ' ').trim()).toBe('11/2/2026, 11:47:05 AM UTC');
  });
  it.each([['not-a-date'], ['']])(
    'refuses to render an unusable server time %j instead of failing the row',
    async (createdAt) => {
      const { root } = await render([run(1, { created_at: createdAt })]);
      expect(root.querySelector('time')).toBeNull();
      const cell = root.querySelector('td[data-column-role="date"]');
      expect(cell?.textContent?.trim()).toBe('Time unavailable');
    },
  );
  it('formats the same UTC instant with Vietnamese date and time conventions', async () => {
    registerLocaleData(viLocale);
    TestBed.overrideProvider(LOCALE_ID, { useValue: 'vi' });
    const instant = '2026-11-02T18:47:05.130948+07:00';
    const { root } = await render([run(1, { created_at: instant })]);
    expect(root.querySelector('time')?.textContent?.replace(/\s+/g, ' ').trim()).toBe(
      `${formatDate(instant, 'HH:mm:ss dd/MM/yyyy', 'vi', 'UTC')} UTC`,
    );
    expect(root.querySelector('time')?.getAttribute('datetime')).toBe(instant);
  });
  it('uses primary/secondary column roles and renders all seven facts as shared narrow records', async () => {
    const { fixture, root } = await render([
      run(1, {
        rerun_label: 'retry-'.repeat(80),
        reprocess_label: 'evidence-'.repeat(80),
      }),
    ]);
    const headers = [...root.querySelectorAll('[role="columnheader"]')];
    expect(headers.map((cell) => cell.getAttribute('data-column-role'))).toEqual([
      'identity',
      'status',
      'status',
      'status',
      'identity',
      'text',
      'date',
    ]);
    expect(headers[0]?.getAttribute('data-column-priority')).toBe('primary');
    expect(headers[4]?.getAttribute('data-column-kind')).toBe('branch');
    fixture.debugElement
      .query(By.directive(DataTableComponent))
      .injector.get(DataTableComponent)
      .narrow.set(true);
    await fixture.whenStable();
    const record = root.querySelector('.eg-table-record');
    expect(record?.querySelectorAll('dt')).toHaveLength(7);
    expect(record?.querySelectorAll('dd')).toHaveLength(7);
    expect(record?.querySelector('a')?.textContent).toBe(run(1).run_id);
    expect(record?.querySelector('a')?.getAttribute('aria-label')).toBe(run(1).run_id);
    expect(record?.querySelector('a')?.classList.contains('whitespace-nowrap')).toBe(false);
    expect(record?.querySelector('.identifier')?.classList.contains('min-w-max')).toBe(false);
    expect(record?.querySelector('.identifier')?.classList.contains('min-w-0')).toBe(true);
    expect(record?.textContent).toContain('retry-'.repeat(80));
    expect(record?.textContent).toContain('evidence-'.repeat(80));
    expect(root.querySelector('.eg-table-narrow')).not.toBeNull();
    expect(root.querySelector('[role="table"]')).toBeNull();
  });
  it('escapes every Cf code point, isolates Unicode refs and only marks unverified rows', async () => {
    const special = 'release/việt\u202e\u200d<script>';
    const row = run(1);
    const { root } = await render([
      run(1, { ref: { ...row.ref, name: special }, unverified_ref: true }),
      run(2),
    ]);
    const ref = root.querySelector('.ref-cell bdi')!;
    expect(ref.textContent).toContain('release/việt⟨U+202E⟩⟨U+200D⟩<script>');
    expect(ref.textContent).not.toContain('\u202e');
    expect(root.querySelector('script')).toBeNull();
    expect(root.querySelectorAll('[data-run-unverified]')).toHaveLength(1);
    expect(
      root
        .querySelector('[data-run-unverified]')
        ?.closest('tr[data-row-id]')
        ?.getAttribute('data-row-id'),
    ).toBe(row.run_id);
  });
  it('escapes and isolates format characters in rerun and reprocess labels', async () => {
    const { root } = await render([
      run(1, { rerun_label: 'failed\u202E_subset', parent_run_id: run(9).run_id }),
      run(2, { reprocess_label: 'evidence\u202E:reprocess', parent_run_id: run(9).run_id }),
    ]);
    const rerun = root.querySelector('[data-run-rerun]')!;
    const reprocess = root.querySelector('[data-run-reprocess]')!;
    // The label is escaped like the ref name: the override becomes a visible token, never a raw
    // character that could reorder the rest of the row.
    expect(rerun.textContent).toContain('failed⟨U+202E⟩_subset');
    expect(rerun.textContent).not.toContain('\u202E');
    expect(reprocess.textContent).toContain('evidence⟨U+202E⟩:reprocess');
    expect(reprocess.textContent).not.toContain('\u202E');
    // Both are wrapped in `<bdi>`, so even a label without a Cf character cannot reorder columns.
    expect(rerun.querySelector('bdi')).not.toBeNull();
    expect(reprocess.querySelector('bdi')).not.toBeNull();
    // The shared overflow renderer receives the escaped text, so its popover copy is escaped too.
    expect(rerun.querySelector('app-data-table-value')?.textContent).toContain('⟨U+202E⟩');
  });
  it('renders rerun and reprocess as visibly distinct labels without treating either as full regression', async () => {
    const { root } = await render([
      run(1, { rerun_label: 'failed_subset', parent_run_id: run(9).run_id }),
      run(2, { reprocess_label: 'reprocessed', parent_run_id: run(9).run_id }),
      run(3),
      run(4, { parent_run_id: run(9).run_id }),
    ]);
    expect(root.querySelector('[data-run-rerun] strong')?.textContent).toBe('Rerun');
    expect(root.querySelector('[data-run-rerun]')?.textContent).toContain('failed_subset');
    expect(root.querySelector('[data-run-reprocess] strong')?.textContent).toBe('Reprocessed');
    expect(root.querySelector('[data-run-reprocess]')?.textContent).toContain('reprocessed');
    expect(root.querySelector('[data-run-rerun]')?.classList.contains('scope-rerun')).toBe(true);
    expect(root.querySelector('[data-run-reprocess]')?.classList.contains('scope-reprocess')).toBe(
      true,
    );
    expect(root.textContent).toContain('Original run');
    expect(root.textContent).toContain('Derived run');
  });
  it('renders missing refs with explicit unavailable copy and long refs through the shared overflow renderer', async () => {
    const first = run(1);
    const name = 'feature/' + 'long-ref-'.repeat(60);
    const { root } = await render([
      run(1, { ref: { ...first.ref, name: null } }),
      run(2, { ref: { ...first.ref, name } }),
    ]);
    expect(root.textContent).toContain('Ref unavailable');
    expect(root.textContent).toContain(name);
  });
  it.each([200, 201, 250])(
    'bounds %i rich rows without a fixed-height virtual probe or lost records',
    async (count) => {
      const input = Array.from({ length: count }, (_, index) =>
        run(index, {
          rerun_label: 'reason-'.repeat(80),
        }),
      );
      const { fixture, root } = await render(input);
      expect(root.querySelector('cdk-virtual-scroll-viewport')).toBeNull();
      expect(root.querySelector('.row-probe')).toBeNull();
      expect(root.querySelectorAll('[data-run-row]')).toHaveLength(200);
      expect(root.querySelector('[role="table"]')?.getAttribute('aria-rowcount')).toBe(
        String(count + 1),
      );
      const pager = root.querySelector('.eg-table-pagination');
      if (count === 200) {
        expect(pager).toBeNull();
      } else {
        const buttons = pager?.querySelectorAll<HTMLButtonElement>('button');
        expect(pager?.textContent).toContain(`Loaded records 1–200 of ${count}`);
        buttons?.[1]?.click();
        await fixture.whenStable();
        expect(root.querySelectorAll('[data-run-row]')).toHaveLength(count - 200);
        expect(root.querySelector('[data-run-row]')?.getAttribute('data-run-id')).toBe(
          run(200).run_id,
        );
        expect(pager?.textContent).toContain(`Loaded records 201–${count} of ${count}`);
        buttons?.[0]?.click();
        await fixture.whenStable();
        expect(root.querySelector('[data-run-row]')?.getAttribute('data-run-id')).toBe(
          run(0).run_id,
        );
      }
    },
  );
  it('copies the original full id rather than the shortened link preview', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', { clipboard: { writeText } });
    try {
      const { fixture, root } = await render([run(1)]);
      root.querySelector<HTMLButtonElement>('[data-run-row] app-copy-value-button button')?.click();
      await fixture.whenStable();
      expect(writeText).toHaveBeenCalledWith(run(1).run_id);
    } finally {
      vi.unstubAllGlobals();
    }
  });
});
