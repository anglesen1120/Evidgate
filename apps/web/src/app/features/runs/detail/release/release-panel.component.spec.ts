import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import type { ApiError } from '../../../../core/http/api-error';
import { stubLayoutApis } from '../../../../shared/testing/layout.testing';
import { ReleasePanelComponent } from './release-panel.component';
import type { ReleaseDetailViewModel, ReleaseStatus } from './release-summary';

const RUN_ID = '018f0000-0000-7000-8000-000000000071';
const OTHER_RUN = '018f0000-0000-7000-8000-000000000072';
const UNBOUND_RUN = '018f0000-0000-7000-8000-000000000073';
const digest = (character: string): string => `sha256:${character.repeat(64)}`;

const RELEASE: ReleaseDetailViewModel = {
  releaseId: 'rel-1',
  name: 'Web 2026.11',
  state: 'active',
  version: 2,
  components: [
    {
      componentKey: 'api',
      digest: digest('1'),
      environmentRevisionId: 'env-api-3',
    },
    {
      componentKey: 'web',
      digest: digest('2'),
      environmentRevisionId: 'env-web-3',
    },
  ],
  boundRuns: [
    { runId: OTHER_RUN, componentKey: 'api' },
    { runId: RUN_ID, componentKey: 'web' },
  ],
};

const FAILURE: ApiError = {
  kind: 'unavailable',
  status: 503,
  code: 'unavailable',
  message: 'Release read unavailable.',
  requestId: '3f2b9d54-0d7e-4c1a-9a2e-6b0f5c7d81c4',
  retryable: true,
  details: [],
  retryAfterSeconds: null,
};

const WAIT_FAILURE: ApiError = {
  ...FAILURE,
  kind: 'rate_limited',
  status: 429,
  code: 'rate_limited',
  retryAfterSeconds: 30,
};

interface PanelInputs {
  releaseId?: string | null;
  release?: ReleaseDetailViewModel | null;
  status?: ReleaseStatus;
  error?: ApiError | null;
  retryEligible?: boolean;
}

describe('ReleasePanelComponent', () => {
  beforeEach(() => {
    stubLayoutApis();
    TestBed.configureTestingModule({
      imports: [ReleasePanelComponent],
      providers: [provideZonelessChangeDetection()],
    });
  });
  afterEach(() => {
    vi.unstubAllGlobals();
    TestBed.resetTestingModule();
  });

  async function render(overrides: Partial<PanelInputs> = {}) {
    const inputs: PanelInputs = {
      releaseId: overrides.releaseId === undefined ? 'rel-1' : overrides.releaseId,
      release: overrides.release === undefined ? RELEASE : overrides.release,
      status: overrides.status ?? 'ready',
      error: overrides.error === undefined ? null : overrides.error,
      retryEligible: overrides.retryEligible ?? false,
    };
    const fixture = TestBed.createComponent(ReleasePanelComponent);
    fixture.componentRef.setInput('runId', RUN_ID);
    fixture.componentRef.setInput('releaseId', inputs.releaseId);
    fixture.componentRef.setInput('release', inputs.release);
    fixture.componentRef.setInput('status', inputs.status);
    fixture.componentRef.setInput('error', inputs.error);
    fixture.componentRef.setInput('retryEligible', inputs.retryEligible);
    await fixture.whenStable();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  it('lists every served component digest in order and marks the bound component', async () => {
    const { root } = await render();
    const rows = [...root.querySelectorAll('[data-release-component]')];
    expect(rows).toHaveLength(2);
    expect(rows.map((row) => row.querySelector('bdi')?.textContent)).toEqual(['api', 'web']);
    expect(rows.map((row) => row.querySelector('[data-release-digest]')?.textContent)).toEqual([
      digest('1'),
      digest('2'),
    ]);
    expect(rows[0]?.textContent).not.toContain('Bound');
    expect(rows[1]?.textContent).toContain('Bound');
    expect(root.querySelector<HTMLElement>('[data-release-name]')?.textContent?.trim()).toBe(
      'Web 2026.11',
    );
    expect(root.querySelector('[data-release-state]')?.textContent?.trim()).toBe('Active');
    expect(root.querySelector('[data-release-version]')?.textContent?.trim()).toBe('2');
    expect(root.querySelector('[data-release-bound-run]')?.textContent?.trim()).toBe(RUN_ID);
  });

  it('labels the panel by its heading and gives the table a caption', async () => {
    const { root } = await render();
    const section = root.querySelector('section');
    expect(section?.getAttribute('aria-labelledby')).toBe('release-title');
    expect(root.querySelector('#release-title')?.textContent).toBe('Release');
    expect(root.querySelector('caption')?.textContent?.trim()).toBe('Release components');
    expect(root.querySelector('[role="status"]')?.getAttribute('aria-live')).toBe('polite');
  });

  it('renders the empty bound copy when no binding names this run', async () => {
    const { root } = await render({
      release: { ...RELEASE, boundRuns: [] },
    });
    expect(root.querySelector('[data-release-unbound]')?.textContent?.trim()).toBe(
      'No release bound to this run',
    );
    expect(root.querySelector('table')).toBeNull();
    expect(root.querySelector('[data-release-digest]')).toBeNull();
  });

  it('re-derives the binding when the same release is shown for another run', async () => {
    const { fixture, root } = await render();
    expect(root.querySelectorAll('[data-release-component]')).toHaveLength(2);
    // `api` is bound to a different run, so it must stay unmarked while this run is shown.
    expect(root.querySelectorAll('[data-release-component]')[0]?.textContent).not.toContain(
      'Bound',
    );
    fixture.componentRef.setInput('runId', UNBOUND_RUN);
    await fixture.whenStable();
    expect(root.querySelector('[data-release-unbound]')?.textContent?.trim()).toBe(
      'No release bound to this run',
    );
    expect(root.querySelector('table')).toBeNull();
    expect(root.querySelector('[data-release-bound-run]')).toBeNull();
    // Returning to a bound run restores the list from the same served rows.
    fixture.componentRef.setInput('runId', RUN_ID);
    await fixture.whenStable();
    expect(root.querySelectorAll('[data-release-component]')).toHaveLength(2);
  });

  it('renders the shared unavailable state, not the bound empty copy, when the run names no release', async () => {
    const { root } = await render({ releaseId: null });
    expect(root.querySelector('[data-release-unavailable]')).not.toBeNull();
    expect(root.querySelector('[data-release-unavailable] app-status-pill')).not.toBeNull();
    expect(root.querySelector('[data-release-unavailable]')?.textContent).toContain(
      'does not name a release',
    );
    expect(root.querySelector('[data-release-unbound]')).toBeNull();
    expect(root.querySelector('table')).toBeNull();
  });

  it('renders the loading state before a release lands', async () => {
    const { root } = await render({ release: null, status: 'loading' });
    expect(root.querySelector('app-loading-pattern')).not.toBeNull();
    expect(root.querySelector('table')).toBeNull();
    expect(root.querySelector('[data-release-unavailable]')).toBeNull();
  });

  it('renders a retryable error and emits retry from the panel', async () => {
    const { fixture, root } = await render({
      release: null,
      status: 'error',
      error: FAILURE,
      retryEligible: true,
    });
    expect(root.querySelector('app-error-pattern')).not.toBeNull();
    expect(root.querySelector('table')).toBeNull();
    const retried = vi.fn();
    fixture.componentInstance.retry.subscribe(retried);
    root.querySelector<HTMLButtonElement>('app-error-pattern button')?.click();
    await fixture.whenStable();
    expect(retried).toHaveBeenCalledTimes(1);
  });

  it('renders a refusal that names its wait instead of the plain error panel', async () => {
    const { fixture, root } = await render({
      release: null,
      status: 'error',
      error: WAIT_FAILURE,
      retryEligible: true,
    });
    expect(root.querySelector('app-retry-after')).not.toBeNull();
    expect(root.querySelector('app-error-pattern')).toBeNull();
    const retried = vi.fn();
    fixture.componentInstance.retry.subscribe(retried);
    root.querySelector<HTMLButtonElement>('app-retry-after button')?.click();
    await fixture.whenStable();
    expect(retried).toHaveBeenCalledTimes(1);
  });

  it('renders not-found and forbidden as their own states', async () => {
    const notFound = await render({ release: null, status: 'not-found' });
    expect(notFound.root.querySelector('app-not-found-pattern')).not.toBeNull();
    expect(notFound.root.querySelector('table')).toBeNull();
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      imports: [ReleasePanelComponent],
      providers: [provideZonelessChangeDetection()],
    });
    const forbidden = await render({ release: null, status: 'forbidden' });
    expect(forbidden.root.querySelector('app-forbidden-pattern')).not.toBeNull();
    expect(forbidden.root.querySelector('table')).toBeNull();
  });

  it('says so when a bound release declares no components', async () => {
    const { root } = await render({ release: { ...RELEASE, components: [] } });
    expect(root.querySelector('[data-release-no-components]')?.textContent?.trim()).toBe(
      'This release declares no components',
    );
    expect(root.querySelector('[data-release-component]')).toBeNull();
  });

  it('keeps 100 components usable: every row renders, every digest stays whole and wraps', async () => {
    const components = Array.from({ length: 100 }, (_, index) => ({
      componentKey: `web-${index}`,
      digest: `sha256:${String(index).padStart(2, '0').repeat(32)}`,
      environmentRevisionId: `env-rev-${index}`,
    }));
    const { root } = await render({
      release: {
        ...RELEASE,
        components,
        boundRuns: [{ runId: RUN_ID, componentKey: 'web-99' }],
        version: 100,
      },
    });
    const rows = [...root.querySelectorAll('[data-release-component]')];
    expect(rows).toHaveLength(100);
    const served = rows.map((row) => row.querySelector('[data-release-digest]')?.textContent);
    expect(served).toEqual(components.map((component) => component.digest));
    expect(served.every((value) => value?.length === 71)).toBe(true);
    expect(root.textContent).not.toContain('…');
    expect(rows[99]?.textContent).toContain('Bound');
    expect(rows[0]?.textContent).not.toContain('Bound');
    // The host wraps long identifiers and fixes the table layout, so no cell can overflow at 320px.
    expect(root.classList.contains('wrap-anywhere')).toBe(true);
    expect(root.querySelector('table')?.classList.contains('table-fixed')).toBe(true);
    expect(rows[0]?.querySelector('button')?.getAttribute('aria-label')).toBe(
      'Copy full digest for component web-0',
    );
  });

  it('copies the exact full digest and announces the component', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', { clipboard: { writeText } });
    const { fixture, root } = await render();
    root.querySelector<HTMLButtonElement>('[data-release-component] button')?.click();
    await fixture.whenStable();
    expect(writeText).toHaveBeenCalledWith(digest('1'));
    expect(root.querySelector('[role="status"]')?.textContent).toBe(
      'Digest copied for component api',
    );
  });

  it('offers manual copying when clipboard access fails', async () => {
    vi.stubGlobal('navigator', {
      clipboard: { writeText: vi.fn().mockRejectedValue(new Error('denied')) },
    });
    const { fixture, root } = await render();
    root.querySelector<HTMLButtonElement>('[data-release-component] button')?.click();
    await fixture.whenStable();
    expect(root.querySelector('[role="status"]')?.textContent).toContain('Copy failed.');
    expect(root.querySelector('[data-release-digest]')?.textContent).toBe(digest('1'));
  });
});
