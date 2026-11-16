import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { RunStateDto } from '@evidgate/sdk';
import { run } from './run-detail-store.fixtures';
import { RunHeaderComponent } from './run-header.component';

describe('RunHeaderComponent', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [RunHeaderComponent],
      providers: [provideZonelessChangeDetection()],
    }),
  );
  afterEach(() => TestBed.resetTestingModule());

  async function render(value: RunStateDto = run, view: 'summary' | 'context' = 'summary') {
    const fixture = TestBed.createComponent(RunHeaderComponent);
    fixture.componentRef.setInput('run', value);
    fixture.componentRef.setInput('view', view);
    await fixture.whenStable();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  it('renders a named compact card with independent state and completeness, no fabricated verdict', async () => {
    const { root } = await render();
    expect(root.querySelector('section')?.getAttribute('data-slot')).toBe('card');
    expect(root.querySelector('section')?.getAttribute('aria-labelledby')).toBe(
      'run-summary-title',
    );
    expect(root.querySelector('#run-summary-title')?.textContent).toContain(run.run_id);
    expect(root.querySelectorAll('dt')).toHaveLength(3);
    expect(root.querySelector('app-run-state-pill')).not.toBeNull();
    expect(root.querySelector('app-evidence-badge')?.textContent).toContain(
      'Completeness: Unavailable',
    );
    expect(root.querySelector('app-decision-chip, app-outcome-pill')).toBeNull();
    expect(root.querySelector('[data-run-environment]')).toBeNull();
  });

  it('keeps incomplete collection explanation and a keyboard-focusable evidence action', async () => {
    const { root, fixture } = await render({ ...run, state: 'incomplete' });
    const viewEvidence = vi.fn();
    fixture.componentInstance.viewEvidence.subscribe(viewEvidence);
    const note = root.querySelector('[data-run-missing-summary]');
    const button = note?.querySelector<HTMLButtonElement>('button');
    expect(note?.textContent).toContain('Collection is incomplete');
    expect(button?.getAttribute('data-slot')).toBe('button');
    expect(button?.classList.contains('min-h-(--eg-control-touch)')).toBe(true);
    button?.focus();
    expect(document.activeElement).toBe(button);
    button?.click();
    expect(viewEvidence).toHaveBeenCalledTimes(1);
    expect(note?.textContent).not.toMatch(/PASS|FAIL|missing_shard/);
  });

  it('distinguishes complete, unknown and incomplete evidence without changing run state', async () => {
    const { root, fixture } = await render({ ...run, state: 'complete' });
    expect(root.querySelector('[data-run-missing-summary]')).toBeNull();
    fixture.componentRef.setInput('runCompleteness', 'incomplete');
    await fixture.whenStable();
    expect(root.querySelector('[data-run-missing-summary]')).not.toBeNull();
    expect(root.querySelector('app-run-state-pill')?.textContent).toContain('Complete');
    fixture.componentRef.setInput('runCompleteness', 'complete');
    await fixture.whenStable();
    expect(root.querySelector('[data-run-missing-summary]')).toBeNull();
  });

  it.each([
    ['regular', 'Regular run'],
    ['replay', 'Replay'],
  ] as const)(
    'labels the %s purpose without translating the environment identifier',
    async (purpose, label) => {
      const { root } = await render({ ...run, purpose }, 'context');
      expect(root.querySelector('[data-run-purpose]')?.textContent?.trim()).toBe(label);
      expect(root.querySelector('[data-run-environment] bdi')?.textContent).toBe(
        run.environment.environment_key,
      );
    },
  );

  it('presents absent provenance as plain facts, not unavailable statuses', async () => {
    const value: RunStateDto = {
      ...run,
      ref: {
        name: null,
        kind: null,
        pr_number: null,
        head_sha: null,
        base_sha: null,
        trigger_event: null,
        is_fork: false,
      },
      unverified_ref: false,
    };
    const { root } = await render(value, 'context');
    expect(root.textContent?.match(/Not provided/g)).toHaveLength(6);
    expect(root.querySelector('[data-run-fork]')?.textContent?.trim()).toBe('No');
    expect(root.textContent).not.toContain('Status unavailable');
    expect(root.querySelector('[data-run-ref] app-status-pill')).toBeNull();
    const summary = await render(value);
    expect(summary.root.textContent).toContain('Not provided');
    expect(summary.root.querySelector('app-run-state-pill')).not.toBeNull();
  });

  it('preserves complete provenance in one context card and escapes long untrusted refs', async () => {
    const ref = 'release-'.repeat(80) + '\u202e<script>';
    const { root } = await render(
      { ...run, ref: { ...run.ref, name: ref }, unverified_ref: true },
      'context',
    );
    expect(root.querySelectorAll('[data-slot="card"]')).toHaveLength(1);
    expect(root.querySelector('section')?.getAttribute('aria-labelledby')).toBe(
      'run-environment-title',
    );
    expect(root.querySelector('#run-environment-title')?.textContent).toBe('Execution context');
    expect(root.querySelector('[data-run-id] bdi')?.textContent).toBe(run.run_id);
    expect(root.querySelector('[data-run-ref] bdi')?.textContent).toBe(
      'release-'.repeat(80) + '⟨U+202E⟩<script>',
    );
    expect(root.querySelector('[data-run-unverified]')).not.toBeNull();
    expect(root.querySelector('[data-run-environment] bdi')?.textContent).toBe(
      run.environment.environment_key,
    );
    expect(root.querySelector('script')).toBeNull();
  });
});
