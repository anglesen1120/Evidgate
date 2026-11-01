import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { EvidenceRevision } from '@evidgate/sdk';
import type { ApiError } from '../../../../core/http/api-error';
import type { DecisionViewModel } from '../decision/decision-view-model';
import type { RevisionsStatus } from '../revision-walk';
import { EvidencePanelComponent } from './evidence-panel.component';

type EvaluatedDecision = Extract<DecisionViewModel, { kind: 'evaluated' }>;

function evaluated(overrides: Partial<Omit<EvaluatedDecision, 'kind'>> = {}): EvaluatedDecision {
  return {
    kind: 'evaluated',
    decision: 'FAIL',
    evidenceRevision: 4,
    completeness: 'incomplete',
    outcome: 'has_failures',
    missing: ['missing_shard'],
    reasons: [],
    policyHash: 'policy-hash-1',
    manifestHash: 'manifest-hash-1',
    evaluatorVersion: 'evaluator-1.4.0',
    mode: 'live',
    ...overrides,
  };
}

function revision(overrides: Partial<EvidenceRevision> = {}): EvidenceRevision {
  return {
    revision_no: 3,
    created_at: '2026-05-01T10:00:00Z',
    completeness: 'incomplete',
    missing: ['count_mismatch'],
    evaluation_id: null,
    ...overrides,
  };
}

const unavailable: ApiError = {
  kind: 'unavailable',
  status: 503,
  code: 'unavailable',
  message: 'Service unavailable.',
  requestId: 'request-revisions',
  retryable: true,
  details: [],
  retryAfterSeconds: null,
};

interface PanelInputs {
  decision?: DecisionViewModel;
  pinnedRevision?: number | null;
  pinned?: EvidenceRevision | null;
  revisions?: readonly EvidenceRevision[] | null;
  revisionsStatus?: RevisionsStatus;
  revisionsError?: ApiError | null;
  revisionsHasMore?: boolean;
  revisionsLoadingMore?: boolean;
  newerRevisionExists?: boolean;
  latestRevisionNo?: number | null;
  retryEligible?: boolean;
}

describe('EvidencePanelComponent', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [EvidencePanelComponent],
      providers: [provideZonelessChangeDetection()],
    }),
  );
  afterEach(() => TestBed.resetTestingModule());

  async function render(inputs: PanelInputs = {}) {
    const fixture = TestBed.createComponent(EvidencePanelComponent);
    const ref = fixture.componentRef;
    ref.setInput('decision', inputs.decision ?? { kind: 'pending' });
    ref.setInput('pinnedRevision', inputs.pinnedRevision ?? null);
    ref.setInput('pinned', inputs.pinned ?? null);
    ref.setInput('revisions', inputs.revisions ?? null);
    ref.setInput('revisionsStatus', inputs.revisionsStatus ?? 'loading');
    ref.setInput('revisionsError', inputs.revisionsError ?? null);
    ref.setInput('revisionsHasMore', inputs.revisionsHasMore ?? false);
    ref.setInput('revisionsLoadingMore', inputs.revisionsLoadingMore ?? false);
    ref.setInput('newerRevisionExists', inputs.newerRevisionExists ?? false);
    ref.setInput('latestRevisionNo', inputs.latestRevisionNo ?? null);
    ref.setInput('retryEligible', inputs.retryEligible ?? false);
    await fixture.whenStable();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  function badge(root: HTMLElement) {
    return root.querySelector('[data-evidence-completeness]');
  }

  it('is the labelled Evidence region', async () => {
    const { root } = await render();
    const region = root.querySelector('#evidence-panel');
    expect(region?.getAttribute('aria-labelledby')).toBe('evidence-title');
    expect(root.querySelector('#evidence-title')?.textContent?.trim()).toBe('Evidence');
  });

  it('shows the neutral badge and unavailable reasons while the revision is not known', async () => {
    const { root } = await render();
    expect(badge(root)?.textContent).toContain('Evidence status unavailable');
    expect(root.textContent).toContain('No live evaluation recorded for this revision');
    expect(root.textContent).toContain('Missing evidence reasons unavailable');
    expect(root.querySelector('ul.reason-list')).toBeNull();
    expect(root.querySelector('app-decision-chip')).toBeNull();
    expect(root.textContent).not.toMatch(/PASS|FAIL|INCONCLUSIVE/);
  });

  it('binds the pinned revision row even while the live decision is pending', async () => {
    const pinned = revision({ revision_no: 3, evaluation_id: 'evaluation-3' });
    const { root } = await render({ pinnedRevision: 3, pinned });
    expect(badge(root)?.textContent).toContain('Evidence incomplete');
    expect(root.querySelector('[data-evidence-missing]')?.textContent).toContain('count_mismatch');
    expect(root.querySelector('[data-evidence-evaluation]')?.textContent).toContain('evaluation-3');
    // The pending line belongs to "no revision data at all", not to a loaded historical row.
    expect(root.textContent).not.toContain('No live evaluation recorded for this revision');
  });

  it('uses the decision body when the pin is that decision evidence revision', async () => {
    const pinned = revision({ revision_no: 4, completeness: 'complete', missing: [] });
    const { root } = await render({
      decision: evaluated({ evidenceRevision: 4, completeness: 'incomplete' }),
      pinnedRevision: 4,
      pinned,
    });
    expect(badge(root)?.textContent).toContain('Evidence incomplete');
    expect(root.querySelector('[data-evidence-missing]')?.textContent).toContain('missing_shard');
    expect(root.querySelector('.reason-empty')).toBeNull();
  });

  it('keeps an older pinned revision on its own row data and never reuses the decision values', async () => {
    const pinned = revision({
      revision_no: 3,
      completeness: 'complete',
      missing: [],
      evaluation_id: 'evaluation-3',
    });
    const { root } = await render({
      decision: evaluated({ evidenceRevision: 4, missing: ['missing_shard'] }),
      pinnedRevision: 3,
      pinned,
    });
    expect(badge(root)?.textContent).toContain('Evidence complete');
    expect(root.querySelector('.reason-empty')?.textContent?.trim()).toBe(
      'No missing evidence reasons reported',
    );
    expect(root.querySelector('[data-evidence-missing]')?.textContent).not.toContain(
      'missing_shard',
    );
    expect(root.querySelector('[data-evidence-historical]')).not.toBeNull();
    expect(root.querySelector('[data-evidence-evaluation]')?.textContent).toContain('evaluation-3');
  });

  it('does not report an incomplete row with no reasons as "nothing missing"', async () => {
    const pinned = revision({ revision_no: 3, completeness: 'incomplete', missing: [] });
    const { root } = await render({ pinnedRevision: 3, pinned });
    expect(root.textContent).toContain('Missing evidence reasons unavailable');
    expect(root.querySelector('.reason-empty')).toBeNull();
  });

  it('treats a non-array decision missing member as unavailable instead of an empty list', async () => {
    // A wire body can violate the mapped type; the panel must not read that as "nothing missing".
    const fixture = TestBed.createComponent(EvidencePanelComponent);
    fixture.componentRef.setInput('decision', { ...evaluated(), missing: null });
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('Missing evidence reasons unavailable');
    expect(root.querySelector('.reason-empty')).toBeNull();
    expect(badge(root)?.textContent).toContain('Evidence incomplete');
  });

  it('shows the empty state, and no selector, for a successful empty revision list', async () => {
    const { root } = await render({ revisions: [], revisionsStatus: 'ready' });
    expect(root.querySelector('[data-evidence-empty]')?.textContent).toContain(
      'No frozen evidence revision yet',
    );
    expect(root.querySelector('app-evidence-revision-selector')).toBeNull();
  });

  it('keeps the decision-derived evidence when the revision list fails', async () => {
    const { root } = await render({
      decision: evaluated({ evidenceRevision: 4 }),
      pinnedRevision: 4,
      revisionsStatus: 'error',
      revisionsError: unavailable,
    });
    expect(root.querySelector('app-error-pattern')?.textContent).toContain('request-revisions');
    expect(badge(root)?.textContent).toContain('Evidence incomplete');
    expect(root.querySelector('[data-evidence-missing]')?.textContent).toContain('missing_shard');
  });

  it('offers the newest revision as an action instead of switching on its own', async () => {
    const { fixture, root } = await render({
      pinnedRevision: 3,
      pinned: revision({ revision_no: 3 }),
      revisions: [revision({ revision_no: 4 }), revision({ revision_no: 3, missing: [] })],
      revisionsStatus: 'ready',
      newerRevisionExists: true,
      latestRevisionNo: 4,
    });
    const banner = root.querySelector('[data-evidence-newer]');
    expect(banner?.textContent).toContain('A newer revision exists');
    expect(banner?.textContent).toContain('You are viewing revision 3. The newest revision is 4.');
    let latest = 0;
    fixture.componentInstance.viewLatest.subscribe(() => (latest += 1));
    (banner?.querySelector('button') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(latest).toBe(1);
  });

  it('emits the chosen revision from the native selector and the older-page intent', async () => {
    const { fixture, root } = await render({
      pinnedRevision: 4,
      pinned: revision({ revision_no: 4 }),
      revisions: [revision({ revision_no: 4 }), revision({ revision_no: 3 })],
      revisionsStatus: 'ready',
      revisionsHasMore: true,
    });
    const chosen: number[] = [];
    let older = 0;
    fixture.componentInstance.selectRevision.subscribe((value) => chosen.push(value));
    fixture.componentInstance.loadOlder.subscribe(() => (older += 1));
    const select = root.querySelector('select') as HTMLSelectElement;
    select.value = '3';
    select.dispatchEvent(new Event('change'));
    (root.querySelector('[data-evidence-load-older]') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(chosen).toEqual([3]);
    expect(older).toBe(1);
    expect(
      root.querySelector('[data-evidence-revision-select]')?.getAttribute('aria-describedby'),
    ).toBe('evidence-pin-help');
  });

  it('says where a pin that is not on a loaded page went', async () => {
    const loading = await render({
      pinnedRevision: 2,
      revisions: [revision({ revision_no: 4 })],
      revisionsStatus: 'ready',
      revisionsHasMore: true,
    });
    expect(loading.root.textContent).toContain(
      'This revision is not in the loaded pages. Load older revisions to look for it.',
    );
    loading.fixture.destroy();

    const exhausted = await render({
      pinnedRevision: 2,
      revisions: [revision({ revision_no: 4 })],
      revisionsStatus: 'ready',
    });
    expect(
      exhausted.root.querySelector('[data-evidence-selected-unavailable]')?.textContent,
    ).toContain('Selected revision unavailable');
  });
});
