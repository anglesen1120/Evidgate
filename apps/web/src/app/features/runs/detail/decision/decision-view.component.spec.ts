import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { type ApiError } from '../../../../core/http/api-error';
import type { DecisionViewModel } from './decision-view-model';
import { DecisionViewComponent } from './decision-view.component';

const EVALUATED: DecisionViewModel = {
  kind: 'evaluated',
  decision: 'FAIL',
  evidenceRevision: 4,
  completeness: 'incomplete',
  outcome: 'has_failures',
  missing: ['missing_shard'],
  reasons: ['critical_not_executed', 'expected_count_zero', 'critical_not_executed'],
  policyHash: 'sha256:policy-hash',
  manifestHash: 'sha256:manifest-hash',
  evaluatorVersion: '1.4.0',
  mode: 'live',
};

function failure(overrides: Partial<ApiError> = {}): ApiError {
  return {
    kind: 'unavailable',
    status: 503,
    code: 'service_unavailable',
    message: 'Service unavailable.',
    requestId: 'request-decision',
    retryable: true,
    details: [],
    retryAfterSeconds: null,
    ...overrides,
  };
}

describe('DecisionViewComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DecisionViewComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
  });

  afterEach(() => TestBed.resetTestingModule());

  function render(inputs: {
    decision?: DecisionViewModel | null;
    shadowIgnored?: boolean;
    updating?: boolean;
    error?: ApiError | null;
    retryEligible?: boolean;
    pinnedRevision?: number | null;
  }) {
    const fixture = TestBed.createComponent(DecisionViewComponent);
    fixture.componentRef.setInput('decision', inputs.decision ?? null);
    if (inputs.shadowIgnored !== undefined) {
      fixture.componentRef.setInput('shadowIgnored', inputs.shadowIgnored);
    }
    if (inputs.updating !== undefined) fixture.componentRef.setInput('updating', inputs.updating);
    if (inputs.error !== undefined) fixture.componentRef.setInput('error', inputs.error);
    if (inputs.retryEligible !== undefined) {
      fixture.componentRef.setInput('retryEligible', inputs.retryEligible);
    }
    if (inputs.pinnedRevision !== undefined) {
      fixture.componentRef.setInput('pinnedRevision', inputs.pinnedRevision);
    }
    fixture.detectChanges();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  function text(root: HTMLElement, selector: string): string | null {
    return root.querySelector(selector)?.textContent?.trim() ?? null;
  }

  it('names the region and its heading, and owns no other h2', () => {
    const { root } = render({ decision: EVALUATED });
    const region = root.querySelector('section#decision-panel')!;
    expect(region.getAttribute('aria-labelledby')).toBe('decision-title');
    expect(root.querySelectorAll('h2')).toHaveLength(1);
    expect(text(root, '#decision-title')).toBe('Latest live decision');
  });

  it('renders the served verdict, outcome, reasons and provenance verbatim', () => {
    const { root } = render({ decision: EVALUATED });
    expect(text(root, '[data-decision-summary] app-decision-chip')).toContain('FAIL');
    expect(text(root, '[data-decision-summary] app-outcome-pill')).toContain('Has failures');
    expect(
      Array.from(root.querySelectorAll('.decision-reason-list li')).map((it) => it.textContent),
    ).toEqual(['critical_not_executed', 'expected_count_zero', 'critical_not_executed']);
    expect(text(root, '[data-decision-revision] dd')).toBe('4');
    expect(text(root, '[data-decision-policy] dd')).toBe('sha256:policy-hash');
    expect(text(root, '[data-decision-manifest] dd')).toBe('sha256:manifest-hash');
    expect(text(root, '[data-decision-evaluator] dd')).toBe('1.4.0');
    expect(text(root, '[data-decision-mode] dd')).toBe('live');
    expect(text(root, '[data-decision-status] dd')).toBe('evaluated');
    expect(text(root, '[data-decision-unavailable]')).toBeNull();
    expect(text(root, '[data-decision-pending]')).toBeNull();
  });

  it('says so when the served reasons list is explicitly empty, without inventing a verdict', () => {
    const { root } = render({ decision: { ...EVALUATED, reasons: [] } });
    expect(text(root, '[data-decision-no-reasons]')).toBe('No decision reasons reported');
    expect(root.querySelector('.decision-reason-list')).toBeNull();
  });

  it('renders pending with the shared pending chip and no verdict, outcome or provenance', () => {
    const { root } = render({ decision: { kind: 'pending' } });
    expect(text(root, 'app-decision-chip')).toContain('pending');
    expect(text(root, '[data-decision-pending]')).toBe('No live evaluation is available yet.');
    expect(root.querySelector('app-outcome-pill')).toBeNull();
    expect(root.querySelector('[data-decision-provenance]')).toBeNull();
    expect(root.querySelector('app-decision-chip .verdict')?.getAttribute('data-tone')).toBe(
      'pending',
    );
    expect(root.textContent).not.toContain('PASS');
    expect(root.textContent).not.toContain('FAIL');
  });

  it('shows a local loading state, never pending, while the first read is in flight', () => {
    const { root } = render({ decision: null });
    expect(root.querySelector('app-loading-pattern')).not.toBeNull();
    expect(root.textContent).toContain('Loading live decision');
    expect(root.querySelector('app-decision-chip')).toBeNull();
    expect(root.querySelector('section#decision-panel')?.getAttribute('aria-busy')).toBe('true');
  });

  it('renders an unavailable decision as neutral copy, never a verdict or pending chip', () => {
    const { root } = render({ decision: { kind: 'unavailable', cause: 'contract' } });
    expect(text(root, '[data-decision-unavailable]')).toBe('Live decision unavailable');
    expect(root.querySelector('app-decision-chip')).toBeNull();
    expect(root.querySelector('app-loading-pattern')).toBeNull();
  });

  it('keeps the accepted body and discloses a refused shadow candidate', () => {
    const { root } = render({ decision: EVALUATED, shadowIgnored: true });
    expect(text(root, '[data-decision-shadow]')).toContain('Shadow evaluation ignored');
    expect(text(root, '[data-decision-summary] app-decision-chip')).toContain('FAIL');
  });

  it('shows the shadow notice with unavailable when no live decision was ever accepted', () => {
    const { root } = render({
      decision: { kind: 'unavailable', cause: 'shadow-ignored' },
      shadowIgnored: true,
    });
    expect(text(root, '[data-decision-shadow]')).toContain('Shadow evaluation ignored');
    expect(text(root, '[data-decision-unavailable]')).toBe('Live decision unavailable');
  });

  it('renders a read failure as the shared error pattern with its request id, not pending', () => {
    const { root } = render({
      decision: { kind: 'unavailable', cause: 'error' },
      error: failure(),
      retryEligible: true,
    });
    expect(root.querySelector('app-error-pattern')).not.toBeNull();
    expect(root.textContent).toContain('Service unavailable.');
    expect(root.textContent).toContain('request-decision');
    expect(root.querySelector('app-decision-chip')).toBeNull();
    expect(root.querySelector('app-loading-pattern')).toBeNull();
  });

  it('retries only while the region is eligible and never through a disabled control', () => {
    const { fixture, root } = render({
      decision: { kind: 'unavailable', cause: 'error' },
      error: failure(),
      retryEligible: false,
    });
    const retries: number[] = [];
    fixture.componentInstance.retry.subscribe(() => retries.push(1));
    const button = root.querySelector('app-error-pattern button') as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    button.click();
    expect(retries).toHaveLength(0);

    fixture.componentRef.setInput('retryEligible', true);
    fixture.detectChanges();
    (root.querySelector('app-error-pattern button') as HTMLButtonElement).click();
    expect(retries).toHaveLength(1);
  });

  it('retains an accepted body through a failed refresh with an explicit stale-read notice', () => {
    const { root } = render({ decision: EVALUATED, error: failure() });
    expect(text(root, '[data-decision-stale]')).toBe(
      'Decision refresh failed. Previously loaded decision retained.',
    );
    expect(text(root, '[data-decision-summary] app-decision-chip')).toContain('FAIL');
    expect(root.querySelector('app-error-pattern')).toBeNull();
  });

  it.each([
    ['forbidden', 403],
    ['unauthenticated', 401],
    ['not_found', 404],
  ] as const)('discloses no body when the refresh is refused with %s', (kind, status) => {
    const { root } = render({ decision: EVALUATED, error: failure({ kind, status }) });
    expect(root.querySelector('app-decision-chip')).toBeNull();
    expect(root.querySelector('[data-decision-policy]')).toBeNull();
    expect(root.querySelector('[data-decision-stale]')).toBeNull();
    expect(root.textContent).not.toContain('sha256:policy');
    expect(root.textContent).not.toContain('critical_not_executed');
    expect(
      root.querySelector('app-forbidden-pattern, app-not-found-pattern, app-error-pattern'),
    ).not.toBeNull();
  });

  it('shows an updating status over the accepted body without falling back to loading', () => {
    const { root } = render({ decision: EVALUATED, updating: true });
    expect(text(root, '.decision-updating')).toBe('Updating decision');
    expect(text(root, '[data-decision-summary] app-decision-chip')).toContain('FAIL');
    expect(root.querySelector('app-loading-pattern')).toBeNull();
  });

  it('announces a changed decision revision only after the first accepted body', () => {
    const { fixture, root } = render({ decision: EVALUATED });
    expect(root.querySelector('[data-decision-announcement]')).toBeNull();

    fixture.componentRef.setInput('decision', { ...EVALUATED, evidenceRevision: 4 });
    fixture.detectChanges();
    expect(root.querySelector('[data-decision-announcement]')).toBeNull();

    fixture.componentRef.setInput('decision', { ...EVALUATED, evidenceRevision: 5 });
    fixture.detectChanges();
    expect(text(root, '[data-decision-announcement]')).toBe(
      'Live decision updated for evidence revision 5',
    );
  });

  it('keeps a pinned revision quiet while it matches the decision', () => {
    const { fixture, root } = render({ decision: EVALUATED, pinnedRevision: 4 });
    expect(root.querySelector('[data-decision-mismatch]')).toBeNull();
    fixture.componentRef.setInput('pinnedRevision', null);
    fixture.detectChanges();
    expect(root.querySelector('[data-decision-mismatch]')).toBeNull();
  });

  it('raises a revision mismatch with an action that asks for the decision evidence', () => {
    const { fixture, root } = render({ decision: EVALUATED, pinnedRevision: 3 });
    const notice = root.querySelector('[data-decision-mismatch]')!;
    expect(notice.textContent).toContain('evaluated at evidence revision 4');
    expect(notice.textContent).toContain('selected evidence revision is 3');
    const seen: number[] = [];
    fixture.componentInstance.viewDecisionEvidence.subscribe((revision) => seen.push(revision));
    (notice.querySelector('button') as HTMLButtonElement).click();
    expect(seen).toEqual([4]);
  });

  it.each([
    ['pending' as const, { kind: 'pending' } as const],
    ['unavailable' as const, { kind: 'unavailable', cause: 'error' } as const],
  ])('raises no revision mismatch while the decision is %s', (_name, decision) => {
    const { root } = render({ decision, pinnedRevision: 99 });
    expect(root.querySelector('[data-decision-mismatch]')).toBeNull();
  });

  it('escapes a served reason instead of rendering it as markup', () => {
    const { root } = render({
      decision: { ...EVALUATED, reasons: ['<img src=x onerror="alert(1)">'] },
    });
    expect(root.querySelector('.decision-reason-list img')).toBeNull();
    expect(text(root, '.decision-reason-list li')).toBe('<img src=x onerror="alert(1)">');
  });
});
