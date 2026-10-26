import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import type { RunStateDto } from '@evidgate/sdk';
import ready from '../../../../../../../fixtures/web-api/run-detail/input.json';
import formatted from '../../../../../../../fixtures/web-api/run-detail-ref-format/input.json';
import { RunsDataService } from '../../../core/data/runs-data.service';
import { ApiRequestError } from '../../../core/http/api-error';
import { RunDetailPage } from './run-detail.page';
import { routes } from '../runs.routes';

// Both bodies are validated against the current RunStateDto by fixture-contract.spec.ts.
const run = ready.body as RunStateDto;
const refRun = formatted.body as RunStateDto;
describe('RunDetailPage', () => {
  const get = vi.fn<(id: string) => Promise<RunStateDto>>();
  beforeEach(() => {
    get.mockReset().mockResolvedValue(run);
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: 'projects/:projectId/runs', children: routes }]),
        { provide: RunsDataService, useValue: { get } },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());
  async function open(id = run.run_id) {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(`/projects/project-1/runs/${id}`, RunDetailPage);
    await harness.fixture.whenStable();
    return { harness, root: harness.routeNativeElement! };
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
    expect(root.querySelector('[data-run-environment]')?.textContent).toContain(
      'Status unavailable',
    );
    expect(root.querySelector('app-run-header [data-run-policy]')).toBeNull();
    expect(root.querySelector('[data-run-environment]')?.previousElementSibling?.textContent).toBe(
      'Environment',
    );
    expect(root.querySelector('app-evidence-badge')?.textContent).toContain(
      'Evidence status unavailable',
    );
    expect(root.querySelector('app-decision-chip, app-outcome-pill')).toBeNull();
    expect(root.textContent).not.toContain('PASS');
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
});
