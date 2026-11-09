import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import type { EvidenceRevisionPage, RunAttemptPage, RunStateDto } from '@evidgate/sdk';
import attemptsOk from '../../../../../../../fixtures/web-api/run-attempts/input.json';
import ready from '../../../../../../../fixtures/web-api/run-detail/input.json';
import formatted from '../../../../../../../fixtures/web-api/run-detail-ref-format/input.json';
import { ReleasesDataService } from '../../../core/data/releases-data.service';
import { RunsDataService } from '../../../core/data/runs-data.service';
import { ApiRequestError } from '../../../core/http/api-error';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { RunDataService } from '../data/run-data.service';
import { RunDetailPage } from './run-detail.page';
import { revision, revisionPage, deferred } from './run-detail-store.fixtures';
import { routes } from '../runs.routes';

// Both bodies are validated against the current RunStateDto by fixture-contract.spec.ts.
const run = ready.body as RunStateDto;
const refRun = formatted.body as RunStateDto;
const attemptPage = attemptsOk.body as RunAttemptPage;
function pendingBody() {
  return {
    status: 'pending',
    decision: null,
    evaluation_id: null,
    evidence_revision: null,
    completeness: null,
    outcome: null,
    missing: null,
    reasons: null,
    policy_hash: null,
    manifest_hash: null,
    evaluator_version: null,
    mode: null,
    url: null,
  };
}
function liveBody() {
  return {
    ...pendingBody(),
    status: 'evaluated',
    decision: 'FAIL',
    evaluation_id: 'evaluation-1',
    evidence_revision: 4,
    completeness: 'incomplete',
    outcome: 'has_failures',
    missing: ['missing_shard'],
    reasons: ['critical_not_executed'],
    policy_hash: 'sha256:policy',
    manifest_hash: 'sha256:manifest',
    evaluator_version: '1.4.0',
    mode: 'live',
  };
}
describe('RunDetailPage', () => {
  provideLayoutTestStubs();
  const get = vi.fn<(id: string) => Promise<RunStateDto>>();
  const listAttempts = vi.fn<(id: string, query?: object) => Promise<RunAttemptPage>>();
  const listEvidenceRevisions =
    vi.fn<(id: string, query?: object) => Promise<EvidenceRevisionPage>>();
  const getDecision = vi.fn<(id: string) => Promise<unknown>>();
  beforeEach(() => {
    get.mockReset().mockResolvedValue(run);
    listAttempts.mockReset().mockResolvedValue(attemptPage);
    listEvidenceRevisions.mockReset().mockResolvedValue({ items: [], next_cursor: null });
    getDecision.mockReset().mockResolvedValue(pendingBody());
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: 'projects/:projectId/runs', children: routes }]),
        { provide: RunsDataService, useValue: { get, listAttempts, listEvidenceRevisions } },
        { provide: RunDataService, useValue: { getDecision } },
        { provide: ReleasesDataService, useValue: { get: vi.fn() } },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());
  async function open(id = run.run_id, fragment = '', query = '') {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(
      `/projects/project-1/runs/${id}${query}${fragment === '' ? '' : `#${fragment}`}`,
      RunDetailPage,
    );
    await settle(harness);
    return { harness, root: harness.routeNativeElement! };
  }
  /** One macrotask after the stable fixture drains the pin write-back and the render handoffs. */
  async function settle(harness: RouterTestingHarness): Promise<void> {
    await harness.fixture.whenStable();
    const tick = deferred<void>();
    setTimeout(tick.resolve, 0);
    await tick.promise;
    await harness.fixture.whenStable();
  }
  it('loads a lazy deep link with its own heading and verbatim header values, no derived verdict', async () => {
    const { root } = await open();
    expect(get).toHaveBeenCalledWith(run.run_id);
    expect(root.querySelectorAll('h1')).toHaveLength(1);
    expect(root.querySelector('[data-run-id]')?.textContent).toBe(run.run_id);
    expect(root.querySelector('[data-run-version]')?.textContent).toBe(String(run.version));
    expect(root.querySelector('[data-run-purpose]')?.textContent).toBe(run.purpose);
    expect(root.querySelector('[data-run-ref] bdi')?.textContent).toBe(run.ref.name);
    expect(root.querySelector('[data-run-evidence-revision] bdi')?.textContent).toBe(
      run.current_evidence_revision_id,
    );
    expect(root.querySelector('[data-run-environment] bdi')?.textContent).toBe(
      run.environment.environment_key,
    );
    expect(root.querySelector('[data-run-environment-revision]')?.textContent).toBe(
      `Revision ${run.environment.revision_no}`,
    );
    expect(root.querySelector('app-run-header [data-run-policy]')).toBeNull();
    expect(root.querySelector('[data-run-environment]')?.previousElementSibling?.textContent).toBe(
      'Environment',
    );
    expect(root.querySelector('app-evidence-badge')?.textContent).toContain(
      'Evidence status unavailable',
    );
    expect(root.querySelector('app-decision-chip')?.textContent).toContain('Not evaluated');
    expect(root.querySelector('app-outcome-pill')).toBeNull();
    expect(root.textContent).not.toContain('PASS');
    const slotRows = root.querySelectorAll('#producers tr[data-producer-slot]');
    expect(slotRows).toHaveLength(run.declared_producers.length);
    expect(root.querySelectorAll('#producers td[data-fact][data-unavailable]')).toHaveLength(0);
  });
  it('places compact run facts before attempts and evidence, with full provenance last', async () => {
    const { root } = await open();
    const regions = [
      ...root.querySelectorAll(
        'app-run-header, app-attempts-table, app-evidence-panel, app-decision-view, app-manifest-panel',
      ),
    ];
    expect(regions.map((region) => region.tagName.toLowerCase())).toEqual([
      'app-run-header',
      'app-attempts-table',
      'app-evidence-panel',
      'app-decision-view',
      'app-run-header',
      'app-manifest-panel',
    ]);
    expect(regions[0]?.querySelector('[data-run-environment]')).toBeNull();
    expect(regions[4]?.querySelector('[data-run-environment]')).not.toBeNull();
    expect(regions[0]?.querySelector('app-decision-chip')).toBeNull();
  });
  it('uses the responsive page title scale rather than a section-heading size', async () => {
    const { root } = await open();
    const title = root.querySelector('h1');
    expect(title).not.toBeNull();
    expect(
      title?.classList.contains('text-[clamp(var(--eg-text-xl),2vw,var(--eg-text-2xl))]'),
    ).toBe(true);
    expect(title?.classList.contains('leading-(--eg-leading-2xl)')).toBe(true);
    expect(title?.textContent).toBe('Run detail');
  });
  it('explains incomplete collection before attempts without inventing a missing reason', async () => {
    get.mockResolvedValue({ ...run, state: 'incomplete' });
    const { root } = await open();
    const note = root.querySelector('[data-run-missing-summary]');
    expect(note?.textContent).toContain('Collection is incomplete');
    note?.querySelector('button')?.click();
    expect(document.activeElement).toBe(root.querySelector('#evidence-title'));
    expect(note?.textContent).not.toMatch(/PASS|FAIL|missing_shard/);
  });
  it('does not announce incomplete collection when no such state is supplied', async () => {
    get.mockResolvedValue({ ...run, state: 'complete' });
    const { root } = await open();
    expect(root.querySelector('[data-run-missing-summary]')).toBeNull();
  });
  it('renders the manifest panel verbatim from the run read', async () => {
    const { root } = await open();
    const panel = root.querySelector('app-manifest-panel')!;
    expect(panel.querySelector('[data-manifest-hash] bdi')?.textContent).toBe(run.manifest_hash);
    expect(panel.querySelector('[data-manifest-suite] bdi')?.textContent).toBe(
      run.suite_revision_id,
    );
    expect(panel.querySelector('[data-manifest-policy] bdi')?.textContent).toBe(
      run.policy_revision_id,
    );
    expect([...panel.querySelectorAll('li bdi')].map((n) => n.textContent)).toEqual(
      run.declared_producers,
    );
  });
  it('renders attempts from the wire: statuses verbatim, retry chain grouped, no decision', async () => {
    const { root } = await open();
    expect(listAttempts).toHaveBeenCalledExactlyOnceWith(run.run_id, { limit: 200 });
    const attempts = root.querySelector('app-attempts-table')!;
    const rows = [...attempts.querySelectorAll('[data-attempt-row]')];
    expect(rows).toHaveLength(attemptPage.items.length);
    const chain = rows.filter((r) => r.querySelector('th')?.textContent?.includes('Attempt 2'));
    expect(chain).toHaveLength(1);
    expect(chain[0]!.querySelector('[data-attempt-normalised]')?.textContent).toContain('passed');
    const blocked = rows.find((r) => r.textContent?.includes('BLOCKED_BY_ENV'))!;
    expect(blocked.querySelector('[data-attempt-normalised]')?.textContent).toContain('blocked');
    expect(blocked.querySelector('[data-attempt-normalised]')?.textContent).not.toContain('passed');
    expect(attempts.querySelector('[data-attempts-healing-unavailable]')).not.toBeNull();
    expect(attempts.querySelector('[data-attempts-unavailable]')).toBeNull();
    expect(root.textContent).not.toContain('PASS');
  });
  it('renders an empty attempts read as the empty state, not unavailable', async () => {
    listAttempts.mockResolvedValueOnce({ items: [], next_cursor: null });
    const { root } = await open();
    expect(root.querySelector('app-attempts-table app-empty-pattern')).not.toBeNull();
    expect(root.querySelector('[data-attempts-unavailable]')).toBeNull();
  });
  it('renders a failed attempts read as a retryable error beside the run header', async () => {
    listAttempts.mockRejectedValueOnce(
      new ApiRequestError({
        kind: 'unavailable',
        status: 503,
        code: 'unavailable',
        message: '',
        requestId: null,
        retryable: true,
        details: [],
        retryAfterSeconds: null,
      }),
    );
    const failed = await open();
    expect(failed.root.querySelector('app-run-header')).not.toBeNull();
    expect(failed.root.querySelector('app-attempts-table app-error-pattern')).not.toBeNull();
  });
  it.each([
    [true, 'Yes'],
    [false, 'No'],
  ] as const)('localizes the fork flag %s as %s', async (isFork, label) => {
    get.mockResolvedValue({ ...run, ref: { ...run.ref, is_fork: isFork } });
    const { root } = await open();
    expect(root.querySelector('[data-run-fork]')?.textContent?.trim()).toBe(label);
  });
  it('escapes U+202E and isolates body-only unverified refs', async () => {
    get.mockResolvedValue(refRun);
    const { root } = await open();
    expect(root.querySelector('[data-run-ref] bdi')?.textContent).toBe(
      'detail/việt⟨U+202E⟩<script>',
    );
    expect(root.querySelector('[data-run-ref]')?.textContent).not.toContain('\u202e');
    expect(root.querySelector('[data-run-unverified]')?.textContent).toContain('Unverified ref');
    expect(root.querySelector('script')).toBeNull();
  });
  it('null evidence revision says none recorded, never PASS', async () => {
    get.mockResolvedValue({ ...run, current_evidence_revision_id: null });
    const { root } = await open();
    expect(root.querySelector('[data-run-evidence-revision]')?.textContent).toContain(
      'None recorded',
    );
    expect(root.textContent).not.toContain('PASS');
  });
  it('renders a null ref kind as the shared unavailable state', async () => {
    get.mockResolvedValue({ ...run, ref: { ...run.ref, kind: null } });
    const { root } = await open();
    expect(root.querySelector('[data-run-ref-kind]')?.textContent).toContain('Status unavailable');
  });
  it('foreign and missing ids render the same not-found view without echoing either id', async () => {
    get.mockRejectedValue(
      new ApiRequestError({
        kind: 'not_found',
        status: 404,
        code: 'not_found',
        message: 'Resource unavailable.',
        requestId: null,
        retryable: false,
        details: [],
        retryAfterSeconds: null,
      }),
    );
    const { harness, root } = await open('foreign-id');
    const foreign = root.textContent;
    expect(root.querySelector('app-not-found-pattern')).not.toBeNull();
    expect(root.querySelector('app-run-header')).toBeNull();
    await harness.navigateByUrl('/projects/project-1/runs/missing-id', RunDetailPage);
    await harness.fixture.whenStable();
    expect(harness.routeNativeElement?.textContent).toBe(foreign);
    expect(foreign).not.toContain('foreign-id');
    expect(foreign).not.toContain('missing-id');
  });
  it('403 shows the shared forbidden surface with no retained run header', async () => {
    const { harness } = await open();
    get.mockRejectedValue(
      new ApiRequestError({
        kind: 'forbidden',
        status: 403,
        code: 'forbidden',
        message: 'Denied.',
        requestId: null,
        retryable: false,
        details: [],
        retryAfterSeconds: null,
      }),
    );
    await harness.navigateByUrl('/projects/project-1/runs/denied-id', RunDetailPage);
    await harness.fixture.whenStable();
    expect(harness.routeNativeElement?.querySelector('app-forbidden-pattern')).not.toBeNull();
    expect(harness.routeNativeElement?.querySelector('app-run-header')).toBeNull();
  });
  it('ignores a late response after the route changes', async () => {
    let resolveOld!: (value: RunStateDto) => void;
    get.mockReturnValueOnce(
      new Promise<RunStateDto>((resolve) => {
        resolveOld = resolve;
      }),
    );
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/projects/project-1/runs/old-id', RunDetailPage);
    expect(harness.routeNativeElement?.querySelector('app-loading-pattern')).not.toBeNull();
    get.mockResolvedValueOnce({ ...run, version: 8 });
    await harness.navigateByUrl('/projects/project-1/runs/new-id', RunDetailPage);
    resolveOld(run);
    await harness.fixture.whenStable();
    expect(harness.routeNativeElement?.querySelector('[data-run-version]')?.textContent).toBe('8');
  });
  it('renders the live decision from one served body beside the evidence panel', async () => {
    getDecision.mockResolvedValue(liveBody());
    const { root } = await open();
    expect(getDecision).toHaveBeenCalledExactlyOnceWith(run.run_id);
    const decision = root.querySelector('app-decision-view')!;
    expect(decision.querySelector('app-decision-chip')?.textContent).toContain('FAIL');
    expect(decision.querySelector('app-outcome-pill')?.textContent).toContain('Has failures');
    expect(decision.querySelector('.decision-reason-list')?.textContent).toContain(
      'critical_not_executed',
    );
    expect(decision.querySelector('[data-decision-revision] dd')?.textContent).toBe('4');
    expect(decision.querySelector('[data-decision-policy] dd')?.textContent).toBe('sha256:policy');
    expect(decision.querySelector('[data-decision-manifest] dd')?.textContent).toBe(
      'sha256:manifest',
    );
    expect(decision.querySelector('[data-decision-evaluator] dd')?.textContent).toBe('1.4.0');
    expect(decision.querySelector('[data-decision-mode] dd')?.textContent).toBe('live');

    const evidence = root.querySelector('app-evidence-panel')!;
    expect(evidence.querySelector('[data-evidence-completeness]')?.textContent).toContain(
      'Evidence incomplete',
    );
    expect(evidence.querySelector('[data-evidence-missing]')?.textContent).toContain(
      'missing_shard',
    );
    // Reading order stays Evidence then Decision, whatever the grid does with the columns.
    expect(evidence.compareDocumentPosition(decision)).toBe(Node.DOCUMENT_POSITION_FOLLOWING);
  });
  it('renders pending with the evidence panel saying no live evaluation exists', async () => {
    const { root } = await open();
    const evidence = root.querySelector('app-evidence-panel')!;
    expect(evidence.textContent).toContain('No live evaluation recorded for this revision');
    expect(evidence.querySelector('[data-evidence-completeness]')?.textContent).toContain(
      'Evidence status unavailable',
    );
    expect(evidence.querySelector('[data-evidence-missing]')?.textContent).toContain(
      'Missing evidence reasons unavailable',
    );
    expect(root.querySelector('app-decision-chip')?.textContent).toContain('Not evaluated');
    expect(root.querySelector('app-outcome-pill')).toBeNull();
  });
  it('a failed decision read keeps the run, manifest and attempts and retries only the decision', async () => {
    getDecision
      .mockRejectedValueOnce(
        new ApiRequestError({
          kind: 'unavailable',
          status: 503,
          code: 'unavailable',
          message: 'Service unavailable.',
          requestId: 'request-decision',
          retryable: true,
          details: [],
          retryAfterSeconds: null,
        }),
      )
      .mockResolvedValueOnce(liveBody());
    const { harness, root } = await open();
    expect(root.querySelector('app-run-header')).not.toBeNull();
    expect(root.querySelector('app-manifest-panel')).not.toBeNull();
    expect(root.querySelector('app-attempts-table')).not.toBeNull();
    const decision = root.querySelector('app-decision-view')!;
    expect(decision.querySelector('app-error-pattern')).not.toBeNull();
    expect(decision.textContent).toContain('request-decision');
    expect(decision.querySelector('app-evidence-panel')).toBeNull();

    (decision.querySelector('app-error-pattern button') as HTMLButtonElement).click();
    await harness.fixture.whenStable();
    expect(getDecision).toHaveBeenCalledTimes(2);
    expect(get).toHaveBeenCalledTimes(1);
    expect(root.querySelector('app-decision-view app-decision-chip')?.textContent).toContain(
      'FAIL',
    );
  });
  it('a refused decision read discloses no decision copy in either region', async () => {
    getDecision.mockRejectedValue(
      new ApiRequestError({
        kind: 'forbidden',
        status: 403,
        code: 'forbidden',
        message: 'Not permitted.',
        requestId: 'request-denied',
        retryable: false,
        details: [],
        retryAfterSeconds: null,
      }),
    );
    const { root } = await open();
    const decision = root.querySelector('app-decision-view')!;
    expect(decision.querySelector('app-forbidden-pattern')).not.toBeNull();
    expect(decision.querySelector('[data-decision-policy]')).toBeNull();
    expect(decision.querySelector('[data-decision-manifest]')).toBeNull();
    expect(decision.textContent).not.toContain('sha256:policy');
    expect(decision.textContent).not.toContain('critical_not_executed');

    const evidence = root.querySelector('app-evidence-panel')!;
    expect(evidence.querySelector('[data-evidence-completeness]')?.textContent).toContain(
      'Evidence status unavailable',
    );
    expect(evidence.querySelector('[data-evidence-missing]')?.textContent).toContain(
      'Missing evidence reasons unavailable',
    );
    expect(evidence.querySelector('app-missing-reason-list')).toBeNull();
    expect(evidence.textContent).not.toContain('missing_shard');
    // The run itself is still readable: only the decision read was refused.
    expect(root.querySelector('app-run-header')).not.toBeNull();
  });
  it('renders the evidence region while the decision read has not answered', async () => {
    const decision = deferred<unknown>();
    getDecision.mockReturnValue(decision.promise);
    listEvidenceRevisions.mockResolvedValue(revisionPage([revision({ revision_no: 4 })]));
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(`/projects/project-1/runs/${run.run_id}`, RunDetailPage);
    await harness.fixture.whenStable();
    const root = harness.routeNativeElement!;
    const evidence = root.querySelector('app-evidence-panel');
    expect(evidence).not.toBeNull();
    expect(evidence?.querySelector('#evidence-title')?.textContent?.trim()).toBe('Evidence');
    // No decision answer yet: the region shows its own neutral state, never a verdict.
    expect(evidence?.querySelector('[data-evidence-completeness]')?.textContent).toContain(
      'Evidence status unavailable',
    );
    expect(root.textContent).not.toContain('PASS');
    decision.resolve(pendingBody());
    await settle(harness);
    expect(harness.routeNativeElement?.querySelector('app-decision-chip')?.textContent).toContain(
      'Not evaluated',
    );
  });
  it('lands on the attempt fragment once the rows render, without paging on its own', async () => {
    const scrollIntoView = vi.fn();
    const original = Object.getOwnPropertyDescriptor(Element.prototype, 'scrollIntoView');
    Object.defineProperty(Element.prototype, 'scrollIntoView', {
      configurable: true,
      value: scrollIntoView,
    });
    try {
      const target = attemptPage.items[0]!;
      const anchor = `attempt-${encodeURIComponent(target.execution_key)}-${target.attempt_no}`;
      const { root } = await open(run.run_id, anchor);
      const row = root.querySelector<HTMLElement>(`[id="${anchor}"]`);
      expect(row).not.toBeNull();
      expect(document.activeElement).toBe(row);
      expect(scrollIntoView).toHaveBeenCalledTimes(1);
      // The target was on the first page: the landing never reads another page by itself.
      expect(listAttempts).toHaveBeenCalledTimes(1);
    } finally {
      if (original === undefined) Reflect.deleteProperty(Element.prototype, 'scrollIntoView');
      else Object.defineProperty(Element.prototype, 'scrollIntoView', original);
    }
  });
  it('leaves an unresolvable fragment alone instead of paginating or moving focus', async () => {
    const { root } = await open(run.run_id, 'attempt-not-on-a-loaded-page-1');
    expect(root.querySelector('app-attempts-table')).not.toBeNull();
    expect(document.activeElement).toBe(document.body);
    expect(listAttempts).toHaveBeenCalledTimes(1);
  });
  it('moves focus to the evidence heading when "View latest revision" removes its control', async () => {
    listEvidenceRevisions.mockResolvedValue(
      revisionPage([revision({ revision_no: 4 }), revision({ revision_no: 3 })]),
    );
    getDecision.mockResolvedValue(liveBody());
    const { harness, root } = await open(run.run_id, '', '?evidence_revision=3');
    const banner = root.querySelector('[data-evidence-newer]');
    expect(banner).not.toBeNull();
    (banner?.querySelector('button') as HTMLButtonElement).focus();
    (banner?.querySelector('button') as HTMLButtonElement).click();
    await settle(harness);
    expect(root.querySelector('[data-evidence-newer]')).toBeNull();
    expect(document.activeElement?.id).toBe('evidence-title');
  });
  it('moves focus to the evidence heading when the decision mismatch action disappears', async () => {
    listEvidenceRevisions.mockResolvedValue(
      revisionPage([revision({ revision_no: 4 }), revision({ revision_no: 3 })]),
    );
    getDecision.mockResolvedValue(liveBody());
    const { harness, root } = await open(run.run_id, '', '?evidence_revision=3');
    root
      .querySelector<HTMLButtonElement>('[role="tab"][aria-controls="brn-tabs-content-decision"]')
      ?.click();
    await settle(harness);
    const action = root.querySelector<HTMLButtonElement>('[data-decision-mismatch] button');
    expect(action).not.toBeNull();
    action!.focus();
    action!.click();
    await settle(harness);
    expect(root.querySelector('[data-decision-mismatch]')).toBeNull();
    expect(document.activeElement?.id).toBe('evidence-title');
  });
});
