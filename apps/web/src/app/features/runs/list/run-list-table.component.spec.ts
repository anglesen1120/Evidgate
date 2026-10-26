import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
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
    ...changes,
  };
}

describe('RunListTableComponent', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [RunListTableComponent],
      providers: [provideZonelessChangeDetection()],
    }),
  );
  afterEach(() => TestBed.resetTestingModule());
  async function render(rows: readonly RunSummary[]) {
    const fixture = TestBed.createComponent(RunListTableComponent);
    fixture.componentRef.setInput('rows', rows);
    await fixture.whenStable();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }
  it('keeps collection state, evidence unavailable and stored decisions independent; no outcome column', async () => {
    const { root } = await render([
      run(1, { decision: 'PASS' }),
      run(2, { state: 'incomplete', decision: 'FAIL' }),
      run(3, { decision: 'INCONCLUSIVE' }),
      run(4, { decision: null }),
      run(5),
    ]);
    expect(
      [...root.querySelectorAll('[role="columnheader"]')].map((cell) => cell.textContent?.trim()),
    ).toEqual(['Run id', 'State', 'Evidence', 'Decision', 'Ref', 'Run scope', 'Created (UTC)']);
    expect(root.querySelectorAll('[data-run-evidence] [data-tone="neutral"]')).toHaveLength(5);
    expect(root.querySelector('[data-run-evidence] [data-tone="success"]')).toBeNull();
    expect(
      [...root.querySelectorAll('[data-run-decision]')].map((cell) =>
        cell.textContent?.replace(/\s+/g, ' ').trim(),
      ),
    ).toEqual([
      'Decision:PASS',
      'Decision:FAIL',
      'Decision:INCONCLUSIVE',
      'Decision:pending',
      'Decision:pending',
    ]);
    expect(root.querySelector('app-outcome-pill')).toBeNull();
    expect(root.querySelector('time')?.getAttribute('datetime')).toBe('2026-10-27T00:00:00Z');
    expect(root.querySelector('abbr')?.getAttribute('aria-label')).toBe(run(1).run_id);
  });
  it('escapes every Cf code point, isolates Unicode refs and only marks unverified rows', async () => {
    const special = 'release/việt\u202e\u200d<script>';
    const row = run(1);
    const { root } = await render([
      run(1, { ref: { ...row.ref, name: special }, unverified_ref: true }),
      run(2),
    ]);
    const ref = root.querySelector('bdi')!;
    expect(ref.textContent).toContain('release/việt⟨U+202E⟩⟨U+200D⟩<script>');
    expect(ref.textContent).not.toContain('\u202e');
    expect(root.querySelector('script')).toBeNull();
    expect(root.querySelectorAll('[data-run-unverified]')).toHaveLength(1);
    expect(
      root
        .querySelector('[data-run-unverified]')
        ?.closest('[data-run-row]')
        ?.getAttribute('data-run-id'),
    ).toBe(row.run_id);
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
    expect(root.querySelector('[role="region"]')?.getAttribute('tabindex')).toBe('0');
  });
  it('keeps 200 rows nonvirtual and switches to a bounded CDK window for 250 rows', async () => {
    const rows = Array.from({ length: 250 }, (_, index) => run(index));
    const { fixture, root } = await render(rows.slice(0, 200));
    expect(root.querySelector('cdk-virtual-scroll-viewport')).toBeNull();
    expect(root.querySelectorAll('[data-run-row]')).toHaveLength(200);
    fixture.componentRef.setInput('rows', rows);
    await fixture.whenStable();
    expect(root.querySelector('cdk-virtual-scroll-viewport')).not.toBeNull();
    expect(root.querySelector('[role="table"]')?.getAttribute('aria-rowcount')).toBe('251');
    expect(root.querySelectorAll('[data-run-row]').length).toBeLessThan(100);
    fixture.componentRef.setInput('rows', rows.slice(0, 1));
    await fixture.whenStable();
    expect(root.querySelector('cdk-virtual-scroll-viewport')).toBeNull();
    expect(root.querySelectorAll('[data-run-row]')).toHaveLength(1);
  });
});
