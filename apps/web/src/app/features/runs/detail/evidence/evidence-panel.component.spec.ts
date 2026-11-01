import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { Completeness } from '../../../../shared/ui/verdict/verdict-values';
import type { DecisionViewModel } from '../decision/decision-view-model';
import { EvidencePanelComponent } from './evidence-panel.component';

type EvaluatedDecision = Extract<DecisionViewModel, { kind: 'evaluated' }>;

function evaluated(overrides: Partial<Omit<EvaluatedDecision, 'kind'>> = {}): EvaluatedDecision {
  return {
    kind: 'evaluated',
    decision: 'FAIL',
    evidenceRevision: 4,
    completeness: 'incomplete',
    outcome: 'has_failures',
    missing: [],
    reasons: [],
    policyHash: 'policy-hash-1',
    manifestHash: 'manifest-hash-1',
    evaluatorVersion: 'evaluator-1.4.0',
    mode: 'live',
    ...overrides,
  };
}

describe('EvidencePanelComponent', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [EvidencePanelComponent],
      providers: [provideZonelessChangeDetection()],
    }),
  );
  afterEach(() => TestBed.resetTestingModule());

  async function render(
    decision: DecisionViewModel,
    runCompleteness: Completeness | null = null,
  ): Promise<HTMLElement> {
    const fixture = TestBed.createComponent(EvidencePanelComponent);
    fixture.componentRef.setInput('decision', decision);
    fixture.componentRef.setInput('runCompleteness', runCompleteness);
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  function badge(root: HTMLElement) {
    return root.querySelector('[data-evidence-completeness]');
  }

  it('is the labelled Evidence region', async () => {
    const root = await render({ kind: 'pending' });
    const region = root.querySelector('#evidence-panel');
    expect(region?.getAttribute('aria-labelledby')).toBe('evidence-title');
    expect(root.querySelector('#evidence-title')?.textContent?.trim()).toBe('Evidence');
  });

  it('shows only collection completeness while pending, with no verdict tone or verdict copy', async () => {
    const root = await render({ kind: 'pending' }, 'incomplete');
    expect(badge(root)?.textContent).toContain('Evidence incomplete');
    // The badge is the only toned element: no decision chip, outcome pill or verdict colour.
    const toned = [...root.querySelectorAll('[data-tone]')];
    expect(toned).toHaveLength(1);
    expect(toned[0]?.getAttribute('data-tone')).toBe('warning');
    expect(root.querySelector('app-decision-chip')).toBeNull();
    expect(root.querySelector('app-outcome-pill')).toBeNull();
    expect(root.querySelector('.verdict--decision')).toBeNull();
    expect(root.textContent).not.toMatch(/PASS|FAIL|INCONCLUSIVE/);
  });

  it('renders missing reasons as unavailable while pending, never as an empty list', async () => {
    const root = await render({ kind: 'pending' }, 'incomplete');
    expect(root.textContent).toContain('Missing evidence reasons unavailable');
    expect(root.textContent).toContain('No live evaluation recorded for this revision');
    expect(root.querySelector('ul.reason-list')).toBeNull();
    expect(root.querySelector('.reason-empty')).toBeNull();
  });

  it('shows a neutral unavailable badge when pending carries no collection completeness', async () => {
    const root = await render({ kind: 'pending' }, null);
    expect(badge(root)?.textContent).toContain('Evidence status unavailable');
    expect(root.querySelector('[data-tone]')?.getAttribute('data-tone')).toBe('neutral');
    expect(root.querySelector('app-decision-chip')).toBeNull();
  });

  it.each([
    ['complete', 'incomplete', 'Evidence complete'],
    ['incomplete', 'complete', 'Evidence incomplete'],
  ] as const)(
    'uses the decision completeness %s, not the run collection completeness %s, when evaluated',
    async (decisionCompleteness, runCompleteness, label) => {
      const root = await render(evaluated({ completeness: decisionCompleteness }), runCompleteness);
      expect(badge(root)?.textContent).toContain(label);
    },
  );

  it('renders the shared empty explanation only for an explicit empty array', async () => {
    const root = await render(evaluated({ missing: [] }), 'complete');
    expect(root.querySelector('.reason-empty')?.textContent?.trim()).toBe(
      'No missing evidence reasons reported',
    );
    expect(root.querySelector('ul.reason-list')).toBeNull();
    expect(root.textContent).not.toContain('Missing evidence reasons unavailable');
  });

  it('treats a non-array missing member as unavailable instead of an empty list', async () => {
    // A wire body can violate the mapped type; the panel must not read that as "nothing missing".
    const violation = { ...evaluated(), missing: null };
    const fixture = TestBed.createComponent(EvidencePanelComponent);
    fixture.componentRef.setInput('decision', violation);
    fixture.componentRef.setInput('runCompleteness', 'incomplete');
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('Missing evidence reasons unavailable');
    expect(root.querySelector('.reason-empty')).toBeNull();
    expect(badge(root)?.textContent).toContain('Evidence incomplete');
  });

  it('keeps collection completeness and no verdict copy when the decision is unavailable', async () => {
    const root = await render({ kind: 'unavailable', cause: 'contract' }, 'incomplete');
    expect(badge(root)?.textContent).toContain('Evidence incomplete');
    expect(root.textContent).toContain('Missing evidence reasons unavailable');
    expect(root.querySelector('.reason-empty')).toBeNull();
    expect(root.querySelector('app-decision-chip')).toBeNull();
    expect(root.querySelector('app-outcome-pill')).toBeNull();
    expect(root.textContent).not.toMatch(/PASS|FAIL|INCONCLUSIVE/);
  });
});
