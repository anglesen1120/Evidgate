import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
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
    completeness: 'complete',
    parent_run_id: null,
    rerun_scope: null,
    ...changes,
  };
}

describe('RunListTableComponent', () => {
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
  it('links the row id to its detail child and preserves the current filter query', async () => {
    await TestBed.inject(Router).navigateByUrl('/?state=complete&ref=main');
    const { root } = await render([run(1)]);
    const link = root.querySelector<HTMLAnchorElement>('.identifier a')!;
    expect(link.getAttribute('href')).toBe(`/${run(1).run_id}?state=complete&ref=main`);
    expect(link.textContent).toContain(run(1).run_id.slice(0, 8));
  });
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
      'Decision: Decision unavailable',
      'Decision: Decision unavailable',
    ]);
    expect(root.querySelector('app-outcome-pill')).toBeNull();
    expect(root.querySelector('time')?.getAttribute('datetime')).toBe('2026-10-27T00:00:00Z');
    expect(root.querySelector('abbr')?.getAttribute('aria-label')).toBe(run(1).run_id);
    expect(root.querySelectorAll('[data-run-decision] [data-tone="neutral"]')).toHaveLength(2);
    expect(root.querySelector('[data-run-decision] [data-tone="pending"]')).toBeNull();
    expect(root.querySelector('[data-run-decision] [title]')?.getAttribute('title')).toContain(
      'Open the run for its stored decision',
    );
  });
  it('supplies localized field labels for every stacked record without changing table semantics', async () => {
    const { root } = await render([run(1)]);
    const labels = [...root.querySelectorAll('[data-run-row] > [data-label]')].map((cell) =>
      cell.getAttribute('data-label'),
    );
    const headings = [...root.querySelectorAll('[role="columnheader"]')].map((cell) =>
      cell.textContent?.trim(),
    );
    expect(labels).toEqual(headings);
    expect(labels).toHaveLength(7);
    expect(root.querySelectorAll('[data-run-row] [role="cell"]')).toHaveLength(6);
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
        ?.closest('[data-run-row]')
        ?.getAttribute('data-run-id'),
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
    expect(root.querySelector('[role="region"]')?.getAttribute('tabindex')).toBe('0');
  });
  it('scrolls the columns from the keyboard while the wrapper holds focus', async () => {
    const { fixture, root } = await render(Array.from({ length: 3 }, (_, index) => run(index)));
    fixture.detectChanges();
    const wrapper = root.querySelector<HTMLElement>('.table-scroll')!;
    // jsdom does not lay out, so the scroll geometry the handler reads is supplied here.
    Object.defineProperty(wrapper, 'scrollWidth', { value: 1600, configurable: true });
    Object.defineProperty(wrapper, 'clientWidth', { value: 1000, configurable: true });
    wrapper.focus();

    const key = (name: string) =>
      wrapper.dispatchEvent(
        new KeyboardEvent('keydown', { key: name, bubbles: true, cancelable: true }),
      );

    key('ArrowRight');
    expect(wrapper.scrollLeft).toBe(500);
    key('End');
    expect(wrapper.scrollLeft).toBe(600);
    key('ArrowLeft');
    expect(wrapper.scrollLeft).toBe(100);
    key('Home');
    expect(wrapper.scrollLeft).toBe(0);

    // A key aimed at a control inside the table keeps its own meaning: it bubbles through the
    // wrapper without being consumed there.
    const link = root.querySelector<HTMLElement>('[data-run-row] a')!;
    link.focus();
    const inner = new KeyboardEvent('keydown', { key: 'End', bubbles: true, cancelable: true });
    link.dispatchEvent(inner);
    expect(inner.defaultPrevented).toBe(false);
    expect(wrapper.scrollLeft).toBe(0);
  });

  it('gives the table keyboard stops with different names in each mode', async () => {
    const { fixture, root } = await render(Array.from({ length: 200 }, (_, index) => run(index)));
    // The plain list scrolls in its wrapper, so the wrapper is the table's only stop.
    expect(root.querySelector('.table-scroll')?.getAttribute('tabindex')).toBe('0');
    expect(root.querySelectorAll('[tabindex="0"]')).toHaveLength(1);

    fixture.componentRef.setInput(
      'rows',
      Array.from({ length: 250 }, (_, index) => run(index)),
    );
    await fixture.whenStable();
    // Two stops, because the wrapper holds the horizontal overflow and the viewport the vertical
    // one; neither can serve the other's axis. They must not share a name, or one table reads as
    // two identical landmarks (F-RUN-04).
    const wrapper = root.querySelector('.table-scroll');
    const viewport = root.querySelector('cdk-virtual-scroll-viewport');
    expect(wrapper?.getAttribute('tabindex')).toBe('0');
    expect(viewport?.getAttribute('tabindex')).toBe('0');
    expect(root.querySelectorAll('[tabindex="0"]')).toHaveLength(2);
    expect(wrapper?.getAttribute('aria-label')).toBe('Project runs');
    expect(viewport?.getAttribute('aria-label')).toBe('Project runs rows');
    expect(wrapper?.getAttribute('aria-label')).not.toBe(viewport?.getAttribute('aria-label'));
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
