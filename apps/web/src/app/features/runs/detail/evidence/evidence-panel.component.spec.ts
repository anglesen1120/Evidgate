import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { APP_BASE_HREF } from '@angular/common';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import type { EvidenceRevision } from '@evidgate/sdk';
import { EMPTY } from 'rxjs';
import type { ApiError } from '../../../../core/http/api-error';
import type { DecisionViewModel } from '../decision/decision-view-model';
import type { RevisionsStatus } from '../revision-walk';
import { EvidencePanelComponent } from './evidence-panel.component';
import type { ConfirmedMissingTarget } from './missing-reasons';
import { stubScopeBrowserApis } from '../../../../shell/layout/scope.fixture';
import { EvidenceRevisionSelectorHarness } from './evidence-revision-selector.test-support';

type EvaluatedDecision = Extract<DecisionViewModel, { kind: 'evaluated' }>;

function evaluated(overrides: Partial<Omit<EvaluatedDecision, 'kind'>> = {}): EvaluatedDecision {
  return {
    kind: 'evaluated',
    decision: 'FAIL',
    evaluationId: 'evaluation-4',
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
  decision?: DecisionViewModel | null;
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
  confirmedTargets?: readonly ConfirmedMissingTarget[];
}

describe('EvidencePanelComponent', () => {
  let restoreBrowserApis: () => void;
  beforeEach(() => {
    restoreBrowserApis = stubScopeBrowserApis();
    TestBed.configureTestingModule({
      imports: [EvidencePanelComponent],
      providers: [provideZonelessChangeDetection(), provideRouter([])],
    });
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    restoreBrowserApis();
    vi.unstubAllGlobals();
  });

  async function render(inputs: PanelInputs = {}) {
    const fixture = TestBed.createComponent(EvidencePanelComponent);
    const ref = fixture.componentRef;
    ref.setInput('decision', 'decision' in inputs ? inputs.decision : { kind: 'pending' });
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
    ref.setInput('confirmedTargets', inputs.confirmedTargets ?? []);
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
    expect(region?.hasAttribute('hlmCard')).toBe(true);
    expect(root.querySelector('#evidence-title')?.getAttribute('tabindex')).toBe('-1');
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

  it('emits the chosen revision from the Brain selector and the older-page intent', async () => {
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
    const selector = await TestbedHarnessEnvironment.loader(fixture).getHarness(
      EvidenceRevisionSelectorHarness,
    );
    await selector.choose(3);
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

  it('renders the revision region from its own row while the decision has not answered', async () => {
    const { root } = await render({
      decision: null,
      pinnedRevision: 4,
      pinned: revision({ revision_no: 4 }),
      revisions: [revision({ revision_no: 4 })],
      revisionsStatus: 'ready',
    });
    expect(badge(root)?.textContent).toContain('Evidence incomplete');
    // The evaluation reference comes from the revision row, never from the unanswered decision.
    expect(root.querySelector('[data-evidence-evaluation]')).not.toBeNull();
    expect(root.textContent).not.toContain('Historical decision details');
    expect(root.querySelector('[data-evidence-selected]')).not.toBeNull();
    expect(root.textContent).not.toMatch(/PASS|FAIL|INCONCLUSIVE/);
  });

  it('keeps the pin readable from its own row while the decision has not answered', async () => {
    const pinned = revision({ revision_no: 3, completeness: 'incomplete' });
    const { root } = await render({ decision: null, pinnedRevision: 3, pinned });
    expect(badge(root)?.textContent).toContain('Evidence incomplete');
    expect(root.querySelector('[data-evidence-missing]')?.textContent).toContain('count_mismatch');
  });

  it('shows a failed older page over the rows that stay readable, with its retry window', async () => {
    const { fixture, root } = await render({
      pinnedRevision: 4,
      pinned: revision({ revision_no: 4 }),
      revisions: [revision({ revision_no: 4 })],
      revisionsStatus: 'ready',
      revisionsError: unavailable,
      revisionsHasMore: true,
      retryEligible: true,
    });
    let retries = 0;
    fixture.componentInstance.retry.subscribe(() => (retries += 1));
    expect(root.querySelector('app-error-pattern')?.textContent).toContain('request-revisions');
    expect(root.querySelector('[data-evidence-revision-select]')).not.toBeNull();
    (root.querySelector('app-error-pattern button') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(retries).toBe(1);
  });

  it('renders a refused walk as a visible failure instead of silence', async () => {
    const refused: ApiError = {
      ...unavailable,
      kind: 'unexpected',
      status: 0,
      code: 'revisions_cursor_repeated',
      retryable: false,
    };
    const { root } = await render({
      revisions: [revision({ revision_no: 4 })],
      revisionsStatus: 'ready',
      revisionsError: refused,
      retryEligible: false,
    });
    expect(root.querySelector('app-error-pattern')?.textContent).toContain(
      'revisions_cursor_repeated',
    );
    // A refused walk offers no retry: the same request would be refused the same way.
    expect(root.querySelector('app-error-pattern button')).toBeNull();
  });

  it('renders a wait window for a continuation failure that answered Retry-After', async () => {
    const rateLimited: ApiError = {
      ...unavailable,
      kind: 'rate_limited',
      status: 429,
      code: 'rate_limited',
      retryAfterSeconds: 30,
    };
    const { root } = await render({
      revisions: [revision({ revision_no: 4 })],
      revisionsStatus: 'ready',
      revisionsError: rateLimited,
      retryEligible: false,
    });
    expect(root.querySelector('app-retry-after')?.textContent).toContain('Retry after: 30 s');
    expect((root.querySelector('app-retry-after button') as HTMLButtonElement).disabled).toBe(true);
  });

  it('marks the region busy while an older page is read', async () => {
    const { root } = await render({
      revisions: [revision({ revision_no: 4 })],
      revisionsStatus: 'ready',
      revisionsLoadingMore: true,
    });
    expect(root.querySelector('#evidence-panel')?.getAttribute('aria-busy')).toBe('true');
  });

  it('announces a revision change politely, silently on the first paint and once per state', async () => {
    const { fixture, root } = await render({
      pinnedRevision: 3,
      pinned: revision({ revision_no: 3 }),
      revisions: [revision({ revision_no: 4 }), revision({ revision_no: 3 })],
      revisionsStatus: 'ready',
    });
    const region = root.querySelector('[data-evidence-announcement]')!;
    expect(region.getAttribute('role')).toBe('status');
    expect(region.textContent?.trim()).toBe('');

    const ref = fixture.componentRef;
    ref.setInput('pinnedRevision', 4);
    ref.setInput('pinned', revision({ revision_no: 4 }));
    await fixture.whenStable();
    expect(region.textContent).toContain('Viewing evidence revision 4');

    // An unrelated render of the same state does not repeat the announcement.
    ref.setInput('retryEligible', true);
    await fixture.whenStable();
    expect(region.textContent).toContain('Viewing evidence revision 4');

    ref.setInput('pinnedRevision', 3);
    ref.setInput('pinned', revision({ revision_no: 3 }));
    ref.setInput('newerRevisionExists', true);
    ref.setInput('latestRevisionNo', 4);
    await fixture.whenStable();
    expect(region.textContent).toContain('Viewing evidence revision 3');
    expect(region.textContent).toContain('Newer revision 4 is available');
  });

  it('handles a reason link as an in-page landing, keeping its href for a new tab', async () => {
    const scrollIntoView = vi.fn();
    const original = Object.getOwnPropertyDescriptor(Element.prototype, 'scrollIntoView');
    Object.defineProperty(Element.prototype, 'scrollIntoView', {
      configurable: true,
      value: scrollIntoView,
    });
    const row = document.createElement('th');
    row.id = 'attempt-exec_42-2';
    row.tabIndex = -1;
    document.body.append(row);
    try {
      const confirmedTargets: ConfirmedMissingTarget[] = [
        {
          code: 'missing_result',
          target: { kind: 'attempt', executionKey: 'exec_42', attemptNo: 2 },
        },
      ];
      const { root } = await render({
        pinnedRevision: 3,
        pinned: revision({ revision_no: 3, missing: ['missing_result'] }),
        revisions: [revision({ revision_no: 3 })],
        revisionsStatus: 'ready',
        confirmedTargets,
      });
      const link = root.querySelector<HTMLAnchorElement>('a.reason-link')!;
      expect(link.getAttribute('href')).toBe('/#attempt-exec_42-2');
      const event = new MouseEvent('click', { bubbles: true, cancelable: true, button: 0 });
      link.dispatchEvent(event);
      expect(event.defaultPrevented).toBe(true);
      expect(document.activeElement).toBe(row);
      expect(scrollIntoView).toHaveBeenCalledTimes(1);
    } finally {
      row.remove();
      if (original === undefined) Reflect.deleteProperty(Element.prototype, 'scrollIntoView');
      else Object.defineProperty(Element.prototype, 'scrollIntoView', original);
    }
  });

  it('keeps a localized build base href in the reason link, so a new tab stays in the app', async () => {
    // A `vi` build is served from `/vi/`: `Router.url` has no prefix, so the panel qualifies the
    // route with the base href instead of dropping the visitor onto the default locale.
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      imports: [EvidencePanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: APP_BASE_HREF, useValue: '/vi/' },
        {
          provide: Router,
          useValue: { url: '/projects/project-1/runs/run-1?evidence_revision=3', events: EMPTY },
        },
      ],
    });
    const fixture = TestBed.createComponent(EvidencePanelComponent);
    const ref = fixture.componentRef;
    ref.setInput('pinnedRevision', 3);
    ref.setInput('pinned', revision({ revision_no: 3, missing: ['missing_result'] }));
    ref.setInput('revisions', [revision({ revision_no: 3 })]);
    ref.setInput('revisionsStatus', 'ready');
    ref.setInput('confirmedTargets', [
      {
        code: 'missing_result',
        target: { kind: 'attempt', executionKey: 'exec_42', attemptNo: 2 },
      },
    ] satisfies ConfirmedMissingTarget[]);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector<HTMLAnchorElement>('a.reason-link')?.getAttribute('href')).toBe(
      '/vi/projects/project-1/runs/run-1?evidence_revision=3#attempt-exec_42-2',
    );
  });

  it('leaves a modified reason-link click to the browser', async () => {
    const confirmedTargets: ConfirmedMissingTarget[] = [
      {
        code: 'missing_result',
        target: { kind: 'attempt', executionKey: 'exec_42', attemptNo: 2 },
      },
    ];
    const { root } = await render({
      pinnedRevision: 3,
      pinned: revision({ revision_no: 3, missing: ['missing_result'] }),
      revisions: [revision({ revision_no: 3 })],
      revisionsStatus: 'ready',
      confirmedTargets,
    });
    const link = root.querySelector<HTMLAnchorElement>('a.reason-link')!;
    const event = new MouseEvent('click', {
      bubbles: true,
      cancelable: true,
      button: 0,
      ctrlKey: true,
    });
    link.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(false);
  });
});
