import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';
import { RouterTestingHarness } from '@angular/router/testing';
import type { RunStateDto } from '@evidgate/sdk';
import { ApiRequestError } from '../../../core/http/api-error';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { HeaderHarness } from '../../../shared/ui/page-header/header.harness';
import { RunDetailPage } from './run-detail.page';
import {
  attemptPage,
  configureRunDetailTestBed,
  get,
  listAttempts,
  listEvidenceRevisions,
  open,
  refRun,
  run,
  selectTab,
} from './run-detail.page.test-support';

class DetailPagingHarness extends ComponentHarness {
  static hostSelector = 'app-run-detail-page';
  readonly next = this.locatorFor('app-attempts-table [data-page="next"]');
  readonly previous = this.locatorFor('app-attempts-table [data-page="previous"]');
  readonly summary = this.locatorFor('app-attempts-table app-data-table-pager [role="status"]');
  readonly rows = this.locatorForAll('app-attempts-table [data-attempt-row]');
  readonly producers = this.locatorFor('app-producer-status-table');
  readonly evidence = this.locatorFor('app-evidence-panel');
}

describe('RunDetailPage', () => {
  provideLayoutTestStubs();
  beforeEach(() => configureRunDetailTestBed());
  afterEach(() => TestBed.resetTestingModule());
  it('loads a lazy deep link with its own heading, localized purpose and verbatim identifiers, no derived verdict', async () => {
    const { harness, root } = await open();
    const header = await TestbedHarnessEnvironment.loader(harness.fixture).getHarness(
      HeaderHarness,
    );
    expect(await header.getDescription()).toBe(`Replay · Version ${run.version}`);
    expect(get).toHaveBeenCalledWith(run.run_id);
    expect(root.querySelectorAll('h1')).toHaveLength(1);
    expect(root.querySelector('[data-run-id] .identifier-accessible bdi')?.textContent).toBe(
      `Run id: ${run.run_id}`,
    );
    await selectTab(harness, 'context');
    expect(root.querySelector('[data-run-version]')?.textContent).toBe(String(run.version));
    expect(root.querySelector('[data-run-purpose]')?.textContent).toBe('Replay');
    expect(root.querySelector('[data-run-ref] bdi')?.textContent).toBe(run.ref.name);
    expect(root.querySelector('[data-run-evidence-revision] bdi')?.textContent).toBe(
      run.current_evidence_revision_id,
    );
    expect(root.querySelector('[data-run-environment] bdi')?.textContent).toBe(
      run.environment.environment_key,
    );
    const revision = root.querySelector('dd[data-run-environment-revision]');
    expect(revision?.previousElementSibling?.textContent).toBe('Environment revision');
    expect(revision?.textContent?.trim()).toBe(String(run.environment.revision_no));
    const copy = root.querySelector('[data-run-environment] button.icon-only');
    expect(copy?.getAttribute('title')).toBe('Copy Environment');
    expect(copy?.getAttribute('aria-label')).toBe('Copy Environment');
    expect(root.querySelector('app-run-header [data-run-policy]')).toBeNull();
    expect(root.querySelector('[data-run-environment]')?.previousElementSibling?.textContent).toBe(
      'Environment',
    );
    expect(root.querySelector('app-evidence-badge')?.textContent).toContain(
      'Completeness: Unavailable',
    );
    expect(root.querySelector('app-decision-chip')?.textContent).toContain('Pending');
    expect(root.querySelector('app-outcome-pill')).toBeNull();
    expect(root.textContent).not.toContain('PASS');
    await selectTab(harness, 'evidence');
    const slotRows = root.querySelectorAll('#producers tr[data-row-id]');
    expect(slotRows).toHaveLength(run.declared_producers.length);
    expect(root.querySelectorAll('#producers [data-fact][data-unavailable]')).toHaveLength(0);
  });
  it('shows one sticky verdict above the four independent panels', async () => {
    const { harness, root } = await open();
    expect(root.querySelectorAll('app-run-header')).toHaveLength(1);
    expect(root.querySelector('app-run-header')?.classList.contains('sticky')).toBe(true);
    expect(root.querySelector('app-run-header app-cancel-run-action')).not.toBeNull();
    expect(root.querySelector('app-attempts-table')).not.toBeNull();
    expect(root.querySelector('[data-attempt-counts] dd')?.textContent).toBe(
      String(run.counts.passed),
    );
    expect(
      root.querySelector('app-manifest-panel, app-evidence-panel, app-decision-view'),
    ).toBeNull();
    await selectTab(harness, 'context');
    expect(root.querySelector('app-run-context [data-run-environment]')).not.toBeNull();
    expect(root.querySelector('app-attempts-table')).toBeNull();
  });
  it('explains incomplete collection before attempts without inventing a missing reason', async () => {
    get.mockResolvedValue({ ...run, state: 'incomplete' });
    const { harness, root } = await open();
    const note = root.querySelector('[data-run-missing-summary]');
    expect(note?.textContent).toContain('Collection is incomplete');
    note?.querySelector('button')?.click();
    await harness.fixture.whenStable();
    expect(document.activeElement).toBe(root.querySelector('#evidence-title'));
    expect(note?.textContent).not.toMatch(/PASS|FAIL|missing_shard/);
  });
  it('does not announce incomplete collection when no such state is supplied', async () => {
    get.mockResolvedValue({ ...run, state: 'complete' });
    const { root } = await open();
    expect(root.querySelector('[data-run-missing-summary]')).toBeNull();
  });
  it('renders the manifest panel verbatim from the run read', async () => {
    const { root } = await open(run.run_id, '', '?tab=context');
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
  it('localizes normalized attempt statuses, preserves reporter strings and groups retries without a decision', async () => {
    const { root } = await open();
    expect(listAttempts).toHaveBeenCalledExactlyOnceWith(run.run_id, { limit: 20 });
    const attempts = root.querySelector('app-attempts-table')!;
    const rows = [...attempts.querySelectorAll('[data-row-id]')];
    expect(rows).toHaveLength(attemptPage.items.length);
    const chain = rows.filter((r) =>
      r.querySelector('.attempt-number')?.textContent?.includes('Attempt 2'),
    );
    expect(chain).toHaveLength(1);
    expect(chain[0]!.querySelector('[data-attempt-normalised]')?.textContent).toContain('Passed');
    const blocked = rows.find((r) => r.textContent?.includes('BLOCKED_BY_ENV'))!;
    expect(blocked.querySelector('[data-attempt-normalised]')?.textContent).toContain('Blocked');
    expect(blocked.querySelector('[data-attempt-normalised]')?.textContent).not.toContain('Passed');
    expect(attempts.querySelector('[data-attempts-healing-unavailable]')).not.toBeNull();
    expect(attempts.querySelector('[data-attempts-unavailable]')).toBeNull();
    expect(root.textContent).not.toContain('PASS');
  });
  it('wires independent attempts next and previous without resetting producers or revisions', async () => {
    const first = { items: attemptPage.items.slice(0, 2), next_cursor: 'attempt-tail' };
    const last = { items: attemptPage.items.slice(2), next_cursor: null };
    listAttempts
      .mockResolvedValueOnce(first)
      .mockResolvedValueOnce(last)
      .mockResolvedValueOnce(first);
    const { harness } = await open();
    const table = await TestbedHarnessEnvironment.loader(harness.fixture).getHarness(
      DetailPagingHarness,
    );
    await selectTab(harness, 'evidence');
    const producerText = await (await table.producers()).text();
    const evidenceText = await (await table.evidence()).text();
    await selectTab(harness, 'attempts');
    expect(await table.rows()).toHaveLength(first.items.length);
    await (await table.next()).click();
    expect(await (await table.summary()).text()).toBe('Page 2');
    expect(await table.rows()).toHaveLength(last.items.length);
    expect(listAttempts).toHaveBeenLastCalledWith(run.run_id, {
      limit: 20,
      cursor: 'attempt-tail',
    });
    await selectTab(harness, 'evidence');
    expect(await (await table.producers()).text()).toBe(producerText);
    expect(await (await table.evidence()).text()).toBe(evidenceText);
    await selectTab(harness, 'attempts');
    await (await table.previous()).click();
    expect(await (await table.summary()).text()).toBe('Page 1');
    expect(await table.rows()).toHaveLength(first.items.length);
    expect(get).toHaveBeenCalledTimes(1);
    expect(listEvidenceRevisions).toHaveBeenCalledTimes(1);
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
    const { root } = await open(run.run_id, '', '?tab=context');
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
    const { root } = await open(run.run_id, '', '?tab=context');
    expect(root.querySelector('[data-run-evidence-revision]')?.textContent).toContain(
      'None recorded',
    );
    expect(root.textContent).not.toContain('PASS');
  });
  it('renders a missing ref kind as plain not provided rather than a status', async () => {
    get.mockResolvedValue({ ...run, ref: { ...run.ref, kind: null } });
    const { root } = await open(run.run_id, '', '?tab=context');
    expect(root.querySelector('[data-run-ref-kind]')?.textContent).toContain('Not provided');
    expect(root.querySelector('[data-run-ref-kind] app-status-pill')).toBeNull();
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
    const header = await TestbedHarnessEnvironment.loader(harness.fixture).getHarness(
      HeaderHarness,
    );
    expect(await header.getDescription()).toBeNull();
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
    await harness.navigateByUrl('/projects/project-1/runs/new-id?tab=context', RunDetailPage);
    resolveOld(run);
    await harness.fixture.whenStable();
    expect(harness.routeNativeElement?.querySelector('[data-run-version]')?.textContent).toBe('8');
  });
});
