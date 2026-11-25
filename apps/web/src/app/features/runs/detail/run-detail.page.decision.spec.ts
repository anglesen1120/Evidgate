import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { ApiRequestError } from '../../../core/http/api-error';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { RunDetailPage } from './run-detail.page';
import { deferred, revision, revisionPage } from './run-detail-store.fixtures';
import {
  attemptPage,
  configureRunDetailTestBed,
  get,
  getDecision,
  listAttempts,
  listEvidenceRevisions,
  liveBody,
  open,
  pendingBody,
  run,
  settle,
  selectTab,
} from './run-detail.page.test-support';

class RunDetailHarness extends ComponentHarness {
  static hostSelector = 'app-run-detail-page';
  private readonly facts = this.locatorForAll('.run-summary dl > div > dd');
  private readonly inspect = this.locatorFor('[data-run-missing-summary] button');
  private readonly decisionTab = this.locatorFor('[role="tab"][aria-label]');
  private readonly evidenceTitle = this.locatorFor('#evidence-title');

  async values(): Promise<string[]> {
    return Promise.all((await this.facts()).map((fact) => fact.text()));
  }

  async inspectFromDecision(): Promise<boolean> {
    await (await this.decisionTab()).click();
    await (await this.inspect()).click();
    return (await this.evidenceTitle()).isFocused();
  }
}

describe('RunDetailPage decision and evidence', () => {
  provideLayoutTestStubs();
  beforeEach(() => configureRunDetailTestBed());
  afterEach(() => TestBed.resetTestingModule());
  it('renders the live decision and its evidence in their respective panels', async () => {
    getDecision.mockResolvedValue(liveBody());
    const { harness, root } = await open(run.run_id, '', '?tab=decision');
    expect(getDecision).toHaveBeenCalledExactlyOnceWith(run.run_id);
    const decision = root.querySelector('app-decision-view')!;
    expect(decision.querySelector('app-decision-chip')?.textContent).toContain('FAIL');
    expect(decision.querySelector('app-outcome-pill')?.textContent).toContain('Has failures');
    expect(decision.querySelector('.decision-reason-list')?.textContent).toContain(
      'critical_not_executed',
    );
    expect(decision.querySelector('[data-decision-revision] dd')?.textContent).toBe('4');
    expect(decision.querySelector('[data-decision-policy] dd bdi')?.textContent).toBe(
      'sha256:policy',
    );
    expect(decision.querySelector('[data-decision-manifest] dd bdi')?.textContent).toBe(
      'sha256:manifest',
    );
    expect(decision.querySelector('[data-decision-evaluator] dd')?.textContent).toBe('1.4.0');
    expect(decision.querySelector('[data-decision-mode] dd')?.textContent).toBe('Live evaluation');
    await selectTab(harness, 'evidence');
    const evidence = root.querySelector('app-evidence-panel')!;
    expect(root.querySelector('.run-summary [data-evidence-completeness]')?.textContent).toContain(
      'Completeness: Incomplete',
    );
    expect(evidence.querySelector('[data-evidence-missing]')?.textContent).toContain(
      'missing_shard',
    );
    const tabs = [...root.querySelectorAll('[role="tab"]')];
    expect(tabs.map((tab) => tab.getAttribute('data-run-tab'))).toEqual([
      'attempts',
      'evidence',
      'decision',
      'context',
    ]);
    expect(decision.isConnected).toBe(false);
  });
  it('shows the three independent facts together only for a matching frozen evaluation', async () => {
    getDecision.mockResolvedValue(liveBody());
    listEvidenceRevisions.mockResolvedValue(
      revisionPage([revision({ revision_no: 4 }), revision({ revision_no: 3 })]),
    );
    const { harness } = await open(run.run_id, '', '?evidence_revision=4');
    const summary = await TestbedHarnessEnvironment.loader(harness.fixture).getHarness(
      RunDetailHarness,
    );
    const [completeness, outcome, decision] = await summary.values();
    expect(completeness).toContain('Incomplete');
    expect(outcome).toContain('Has failures');
    expect(decision).toContain('FAIL');

    await harness.navigateByUrl(
      `/projects/project-1/runs/${run.run_id}?evidence_revision=3`,
      RunDetailPage,
    );
    await settle(harness);
    const [, historicalOutcome, historicalDecision] = await summary.values();
    expect(historicalOutcome).toContain('Unavailable');
    expect(historicalDecision).toContain('Unavailable');
  });
  it('never promotes an older stored PASS to a newer incomplete collection', async () => {
    getDecision.mockResolvedValue({
      ...liveBody(),
      decision: 'PASS',
      completeness: 'complete',
      outcome: 'all_passed',
      missing: [],
      reasons: [],
    });
    listEvidenceRevisions.mockResolvedValue(revisionPage([revision({ revision_no: 5 })]));
    const { harness, root } = await open(run.run_id, '', '?evidence_revision=5&tab=decision');
    const detail = await TestbedHarnessEnvironment.loader(harness.fixture).getHarness(
      RunDetailHarness,
    );
    const [completeness, outcome, decision] = await detail.values();
    expect(completeness).toContain('Incomplete');
    expect(outcome).toContain('Unavailable');
    expect(decision).toContain('Unavailable');
    expect(root.querySelector('[data-decision-summary] app-decision-chip')?.textContent).toContain(
      'PASS',
    );
  });
  it('opens evidence from the header without a second decision or revision read', async () => {
    get.mockResolvedValue({ ...run, state: 'incomplete' });
    const { harness } = await open();
    const detail = await TestbedHarnessEnvironment.loader(harness.fixture).getHarness(
      RunDetailHarness,
    );
    expect(await detail.inspectFromDecision()).toBe(true);
    expect(getDecision).toHaveBeenCalledTimes(1);
    expect(listEvidenceRevisions).toHaveBeenCalledTimes(1);
  });
  it('renders pending with the evidence panel saying no live evaluation exists', async () => {
    const { harness, root } = await open(run.run_id, '', '?tab=decision');
    await selectTab(harness, 'evidence');
    const evidence = root.querySelector('app-evidence-panel')!;
    expect(evidence.textContent).toContain('No live evaluation recorded for this revision');
    expect(root.querySelector('.run-summary [data-evidence-completeness]')?.textContent).toContain(
      'Completeness: Unavailable',
    );
    expect(evidence.querySelector('[data-evidence-missing]')?.textContent).toContain(
      'Missing evidence reasons unavailable',
    );
    expect(root.querySelector('app-decision-chip')?.textContent).toContain('Pending');
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
    expect(root.querySelector('app-attempts-table')).not.toBeNull();
    await selectTab(harness, 'context');
    expect(root.querySelector('app-manifest-panel')).not.toBeNull();
    await selectTab(harness, 'decision');
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
    const { harness, root } = await open(run.run_id, '', '?tab=decision');
    const decision = root.querySelector('app-decision-view')!;
    expect(decision.querySelector('app-forbidden-pattern')).not.toBeNull();
    expect(decision.querySelector('[data-decision-policy]')).toBeNull();
    expect(decision.querySelector('[data-decision-manifest]')).toBeNull();
    expect(decision.textContent).not.toContain('sha256:policy');
    expect(decision.textContent).not.toContain('critical_not_executed');

    await selectTab(harness, 'evidence');
    const evidence = root.querySelector('app-evidence-panel')!;
    expect(root.querySelector('.run-summary [data-evidence-completeness]')?.textContent).toContain(
      'Completeness: Unavailable',
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
    await harness.navigateByUrl(
      `/projects/project-1/runs/${run.run_id}?tab=evidence`,
      RunDetailPage,
    );
    await harness.fixture.whenStable();
    const root = harness.routeNativeElement!;
    const evidence = root.querySelector('app-evidence-panel');
    expect(evidence).not.toBeNull();
    expect(evidence?.querySelector('#evidence-title')?.textContent?.trim()).toBe('Evidence');
    expect(root.querySelector('.run-summary')?.textContent).toContain('Loading live decision');
    // The loaded revision provides completeness, but cannot supply a verdict.
    expect(root.querySelector('.run-summary [data-evidence-completeness]')?.textContent).toContain(
      'Completeness: Incomplete',
    );
    expect(root.textContent).not.toContain('PASS');
    decision.resolve(pendingBody());
    await settle(harness);
    expect(root.querySelector('.run-summary')?.textContent).toContain('Pending');
    expect(harness.routeNativeElement?.querySelector('app-decision-chip')?.textContent).toContain(
      'Pending',
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
    const { harness, root } = await open(run.run_id, '', '?tab=evidence&evidence_revision=3');
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
