import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { DataTableComponent } from '../../../../shared/ui/data-table/data-table.component';

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
  releases?: readonly { releaseId: string; name: string | null }[];
  selectedReleaseId?: string | null;
  release?: ReleaseDetailViewModel | null;
  status?: ReleaseStatus;
  error?: ApiError | null;
  retryEligible?: boolean;
}

function fullDigest(row: ParentNode): string | null {
  return (
    row.querySelector(
      '[data-release-digest] .identifier-accessible bdi, [data-release-digest] code.full-value bdi',
    )?.textContent ?? null
  );
}

function digestCopy(row: ParentNode): HTMLButtonElement | null {
  return row.querySelector('[data-release-digest] app-copy-value-button button');
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
      releases: overrides.releases ?? [{ releaseId: 'rel-1', name: RELEASE.name }],
      selectedReleaseId: overrides.selectedReleaseId ?? 'rel-1',
      release: overrides.release === undefined ? RELEASE : overrides.release,
      status: overrides.status ?? 'ready',
      error: overrides.error === undefined ? null : overrides.error,
      retryEligible: overrides.retryEligible ?? false,
    };
    const fixture = TestBed.createComponent(ReleasePanelComponent);
    fixture.componentRef.setInput('runId', RUN_ID);
    fixture.componentRef.setInput('releases', inputs.releases);
    fixture.componentRef.setInput('selectedReleaseId', inputs.selectedReleaseId);
    fixture.componentRef.setInput('release', inputs.release);
    fixture.componentRef.setInput('status', inputs.status);
    fixture.componentRef.setInput('error', inputs.error);
    fixture.componentRef.setInput('retryEligible', inputs.retryEligible);
    await fixture.whenStable();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  it('keeps the sibling-card inset for both bound and unbound release content', async () => {
    const { root, fixture } = await render();
    const section = root.querySelector('[data-release-panel]');
    expect(section?.classList.contains('p-(--eg-space-5)')).toBe(true);
    expect(section?.classList.contains('gap-(--eg-space-5)')).toBe(true);
    fixture.componentRef.setInput('releases', []);
    await fixture.whenStable();
    expect(root.querySelector('[data-release-unbound]')).not.toBeNull();
    expect(section?.classList.contains('p-(--eg-space-5)')).toBe(true);
  });

  it('lists every served component digest in order and marks the bound component', async () => {
    const { root } = await render();
    const rows = [...root.querySelectorAll('tr[data-row-id]')];
    expect(rows).toHaveLength(2);
    expect(rows.map((row) => row.querySelector('bdi')?.textContent)).toEqual(['api', 'web']);
    expect(rows.map(fullDigest)).toEqual([
      `Digest for component api: ${digest('1')}`,
      `Digest for component web: ${digest('2')}`,
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
    expect(root.querySelector('app-data-table .caption')?.textContent?.trim()).toBe(
      'Release components',
    );
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

  it('renders unbound copy for an empty releases list', async () => {
    const { root } = await render({ releases: [] });
    expect(root.querySelector('[data-release-unbound]')?.textContent).toContain(
      'No release bound to this run',
    );
    expect(root.querySelector('table')).toBeNull();
  });

  it('shows no selector for one release', async () => {
    const { root } = await render();
    expect(root.querySelector('[role="combobox"]')).toBeNull();
    expect(root.querySelector('table')).not.toBeNull();
  });

  it('labels and selects two releases with the id as a missing-name fallback', async () => {
    const { fixture, root } = await render({
      releases: [
        { releaseId: 'rel-1', name: 'Web\u202e2026' },
        { releaseId: 'rel-2', name: null },
      ],
    });
    const trigger = root.querySelector<HTMLButtonElement>('[role="combobox"]')!;
    expect(root.querySelector('label')?.getAttribute('for')).toBe(trigger.id);
    expect(root.querySelector('label')?.textContent).toBe('Releases bound to this run');
    expect(trigger.getAttribute('aria-describedby')).toBe('release-choice-name');
    expect(trigger.textContent?.trim()).toBe('Web⟨U+202E⟩2026');
    const selected = vi.fn();
    fixture.componentInstance.selectRelease.subscribe(selected);
    trigger.click();
    await fixture.whenStable();
    const options = [...document.querySelectorAll<HTMLElement>('[role="option"]')];
    expect(options.map((option) => option.textContent?.trim())).toEqual([
      'Web⟨U+202E⟩2026',
      'rel-2',
    ]);
    expect(options.map((option) => option.getAttribute('aria-selected'))).toEqual([
      'true',
      'false',
    ]);
    options[1]?.click();
    await fixture.whenStable();
    expect(selected).toHaveBeenCalledExactlyOnceWith('rel-2');
    fixture.componentRef.setInput('selectedReleaseId', 'rel-2');
    await fixture.whenStable();
    expect(trigger.textContent?.trim()).toBe('rel-2');
  });

  it.each(['', '   ', '\t\n'])('uses the release id for a blank selector name %j', async (name) => {
    const { fixture, root } = await render({
      releases: [
        { releaseId: 'rel-1', name: RELEASE.name },
        { releaseId: 'rel-2', name },
      ],
    });
    root.querySelector<HTMLButtonElement>('[role="combobox"]')?.click();
    await fixture.whenStable();
    const options = document.querySelectorAll('[role="option"]');
    expect(options[1]?.textContent?.trim()).toBe('rel-2');
  });

  it('renders the loading state before a release lands', async () => {
    const { root } = await render({ release: null, status: 'loading' });
    expect(root.querySelector('app-loading-pattern')).not.toBeNull();
    expect(root.querySelector('table')).toBeNull();
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

  it('keeps 100 component digests recoverable in order without an inner row cap', async () => {
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
    const rows = [...root.querySelectorAll('tr[data-row-id]')];
    expect(rows).toHaveLength(100);
    const served = rows.map(fullDigest);
    const expected = components.map(
      (component) => `Digest for component ${component.componentKey}: ${component.digest}`,
    );
    expect(served).toEqual(expected);
    expect(served.every((value, index) => value?.length === expected[index]!.length)).toBe(true);
    expect(root.querySelector('.eg-table-long')).toBeNull();
    expect(rows[99]?.textContent).toContain('Bound');
    expect(rows[0]?.textContent).not.toContain('Bound');
    // Shared auto-layout roles budget identifiers; narrow layouts use shared full-fact records.
    expect(root.classList.contains('wrap-anywhere')).toBe(true);
    expect(root.querySelector('table')?.classList.contains('eg-table-layout')).toBe(true);
    expect(digestCopy(rows[0]!)?.getAttribute('aria-label')).toBe(
      'Copy Digest for component web-0',
    );
  });

  it('copies the exact full digest and announces the component', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', { clipboard: { writeText } });
    const { fixture, root } = await render();
    digestCopy(root)?.click();
    await fixture.whenStable();
    expect(writeText).toHaveBeenCalledWith(digest('1'));
    expect(root.querySelector('[role="status"]')?.textContent).toBe(
      'Digest for component api copied.',
    );
  });

  it('offers manual copying when clipboard access fails', async () => {
    vi.stubGlobal('navigator', {
      clipboard: { writeText: vi.fn().mockRejectedValue(new Error('denied')) },
    });
    const { fixture, root } = await render();
    digestCopy(root)?.click();
    await fixture.whenStable();
    expect(root.querySelector('[role="status"]')?.textContent).toContain('Could not copy');
    expect(fullDigest(root)).toBe(`Digest for component api: ${digest('1')}`);
    root
      .querySelector<HTMLButtonElement>('[data-release-digest] button[aria-haspopup="dialog"]')!
      .click();
    await fixture.whenStable();
    expect(document.querySelector('[role="dialog"] code.full-value bdi')?.textContent).toBe(
      digest('1'),
    );
  });

  it('escapes a format character in the label and the announcement of a copied component', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', { clipboard: { writeText } });
    const componentKey = 'web\u202eapi';
    const { fixture, root } = await render({
      release: {
        ...RELEASE,
        components: [{ componentKey, digest: digest('3'), environmentRevisionId: 'env-api-3' }],
        boundRuns: [{ runId: RUN_ID, componentKey }],
      },
    });
    const button = digestCopy(root);
    expect(button?.getAttribute('aria-label')).toBe('Copy Digest for component web⟨U+202E⟩api');
    button?.click();
    await fixture.whenStable();
    expect(writeText).toHaveBeenCalledWith(digest('3'));
    expect(root.querySelector('[role="status"]')?.textContent).toBe(
      'Digest for component web⟨U+202E⟩api copied.',
    );
    expect(root.querySelector('[role="status"]')?.textContent).not.toContain('\u202e');
  });

  it('refuses to copy a digest whose visible form differs from the served value', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', { clipboard: { writeText } });
    const served = `sha256:${'4'.repeat(63)}\u202e`;
    const { fixture, root } = await render({
      release: {
        ...RELEASE,
        components: [{ componentKey: 'api', digest: served, environmentRevisionId: 'env-api-3' }],
      },
    });
    expect(fullDigest(root)).toBe(`Digest for component api: sha256:${'4'.repeat(63)}⟨U+202E⟩`);
    digestCopy(root)?.click();
    await fixture.whenStable();
    expect(writeText).not.toHaveBeenCalled();
    expect(root.querySelector('[role="status"]')?.textContent).toContain('Could not copy');
  });

  it('keeps short names intrinsic and gives digest and revision columns independent budgets', async () => {
    const { fixture } = await render();
    const table = fixture.debugElement.query(By.directive(DataTableComponent))
      .componentInstance as DataTableComponent<unknown>;
    expect(table.flow()).toBe('page');
    expect(table.fitToContainer()).toBe(true);
    expect(table.columns().map((column) => column.width?.preferred)).toEqual([
      '1%',
      'auto',
      'auto',
    ]);
    expect(table.columns()[1]?.width?.min).toBe('calc(24ch + 2 * var(--eg-space-4))');
    expect(table.columns()[2]?.width?.min).toBe('calc(24ch + 2 * var(--eg-space-4))');
  });

  it('preserves every character of a long supplied name in the compact selector and open options', async () => {
    const name = `  ${'Customer supplied release name '.repeat(8)}  `;
    const { fixture, root } = await render({
      releases: [
        { releaseId: 'rel-1', name },
        { releaseId: 'rel-2', name: 'Other' },
      ],
    });
    const trigger = root.querySelector<HTMLButtonElement>('[role="combobox"]')!;
    expect(trigger.querySelector('bdi')?.textContent).toBe(name);
    expect(trigger.querySelector('bdi')?.classList.contains('truncate')).toBe(true);
    expect(root.querySelector('#release-choice-name')?.textContent).toBe(name);
    trigger.click();
    await fixture.whenStable();
    expect(document.querySelector('[role="option"] bdi')?.textContent).toBe(name);
  });

  it('does not select a fabricated, missing or multiple release value', async () => {
    const { fixture } = await render({
      releases: [
        { releaseId: 'rel-1', name: 'One' },
        { releaseId: 'rel-2', name: 'Two' },
      ],
    });
    const selected = vi.fn();
    fixture.componentInstance.selectRelease.subscribe(selected);
    const select = fixture.debugElement.query(By.css('hlm-select'));
    for (const value of ['not-served', undefined, ['rel-2']]) {
      select.triggerEventHandler('valueChange', value);
    }
    await fixture.whenStable();
    expect(selected).not.toHaveBeenCalled();
    select.triggerEventHandler('valueChange', 'rel-2');
    expect(selected).toHaveBeenCalledExactlyOnceWith('rel-2');
  });
  it('renders every component fact in shared narrow records without clipping the digest', async () => {
    const { fixture, root } = await render();
    fixture.debugElement
      .query(By.directive(DataTableComponent))
      .injector.get(DataTableComponent)
      .narrow.set(true);
    await fixture.whenStable();
    const records = root.querySelectorAll('.eg-table-record');
    expect(records).toHaveLength(2);
    expect(records[0]?.querySelectorAll('dt')).toHaveLength(3);
    expect(records[0]?.textContent).toContain('api');
    expect(records[0]?.textContent).toContain(digest('1'));
    expect(records[0]?.textContent).toContain('env-api-3');
    expect(root.querySelector('.eg-table-narrow')).not.toBeNull();
  });
});
