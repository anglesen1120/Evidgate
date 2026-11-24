import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { UsageSummaryDto, UsageSummaryMetricDto, UsageSummaryProjectDto } from '@evidgate/sdk';

import { By } from '@angular/platform-browser';
import { ActivatedRoute } from '@angular/router';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { DataTableComponent } from '../../../shared/ui/data-table/data-table.component';
import { useLightweightTablePresentation } from '../../../shared/ui/data-table/data-table-volume.testing';
import { QuotaPanelComponent } from './quota-panel.component';
import { QuotaStore, type QuotaLoadState } from './quota.store';

provideLayoutTestStubs();
class QuotaPanelHarness extends ComponentHarness {
  static hostSelector = 'app-quota-panel';
  readonly metrics = this.locatorForAll('[data-quota-metric]');
  readonly pageTitle = this.locatorForOptional('h1#quota-title');
  readonly sectionTitle = this.locatorForOptional('h2#quota-title');
  readonly subsectionTitles = this.locatorForAll('h3');
  readonly metricHeading = this.locatorFor('[data-quota-metric] h3');
  readonly projectHeading = this.locatorFor('[data-quota-projects] h3');
  readonly projectRows = this.locatorForAll('app-data-table [data-row-id]');
  readonly viewActions = this.locatorForAll('[data-quota-view]');
  readonly recordedRows = this.locatorForAll('table[aria-labelledby="quota-state-title"] tbody tr');
  readonly pagers = this.locatorForAll('app-data-table-pager');
  readonly pagerNext = this.locatorFor('app-data-table-pager [data-page="next"]');
  readonly pagerPrevious = this.locatorFor('app-data-table-pager [data-page="previous"]');
  readonly pageError = this.locatorForOptional('[data-quota-page-error]');
  readonly retryPage = this.locatorFor('[data-quota-page-error] button');
  readonly sizeTrigger = this.locatorFor('app-data-table-pager hlm-select-trigger button');
  readonly sizeOptions = this.documentRootLocatorFactory().locatorForAll('[role="option"]');
  readonly retry = this.locatorFor('button');
}

function metric(changes: Partial<UsageSummaryMetricDto> = {}): UsageSummaryMetricDto {
  return {
    metric: 'egress_gb',
    scope: 'tenant',
    used: null,
    limit: null,
    warned_at: null,
    blocked_at: null,
    retry_after_seconds: null,
    unavailable_reason: 'The egress producer is unavailable.',
    projects: [],
    projects_status: { blocked_count: 0, warned_count: 0 },
    ...changes,
  };
}

/** One project's counter under a metric; below both lines unless the test says otherwise. */
function project(changes: Partial<UsageSummaryProjectDto> = {}): UsageSummaryProjectDto {
  return {
    project_id: 'project-one',
    scope: 'project',
    used: 1,
    limit: 100,
    warned_at: null,
    blocked_at: null,
    retry_after_seconds: null,
    ...changes,
  };
}

describe('QuotaPanelComponent', () => {
  const usageSummary = signal<UsageSummaryDto | null>(null);
  const loadState = signal<QuotaLoadState>('ready');
  const load = vi.fn();
  const reload = vi.fn();
  const projectPage = signal(1);
  const projectPageSize = signal(20);
  const projectPaging = signal(false);
  const projectPagingError = signal(false);
  const nextProjectPage = vi.fn();
  const previousProjectPage = vi.fn();
  const setProjectPageSize = vi.fn();
  const retryProjectPage = vi.fn();

  beforeEach(() => {
    usageSummary.set({
      period: '2026-11',
      project: null,
      metrics: [metric()],
      next_cursor: null,
    });
    loadState.set('ready');
    projectPage.set(1);
    projectPageSize.set(20);
    projectPaging.set(false);
    projectPagingError.set(false);
    vi.clearAllMocks();
    TestBed.configureTestingModule({
      imports: [QuotaPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        {
          provide: QuotaStore,
          useValue: {
            usageSummary,
            loadState,
            load,
            reload,
            projectPage,
            projectPageSize,
            projectPaging,
            projectPagingError,
            hasNextProjectPage: () => usageSummary()?.next_cursor != null,
            nextProjectPage,
            previousProjectPage,
            setProjectPageSize,
            retryProjectPage,
          },
        },
      ],
    });
  });
  afterEach(() => TestBed.resetTestingModule());

  async function render() {
    const fixture = TestBed.createComponent(QuotaPanelComponent);
    await fixture.whenStable();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, QuotaPanelHarness);
    return { fixture, harness, root: fixture.nativeElement as HTMLElement };
  }

  it.each([1, 2])('uses heading level %s for the quota title', async (level) => {
    TestBed.configureTestingModule({
      providers: [
        { provide: ActivatedRoute, useValue: { snapshot: { data: { pageHeadingLevel: level } } } },
      ],
    });
    usageSummary.update((summary) =>
      summary ? { ...summary, metrics: [metric({ projects: [project()] })] } : null,
    );
    const { harness } = await render();
    const page = await harness.pageTitle();
    const section = await harness.sectionTitle();

    expect(await (level === 1 ? page : section)?.text()).toBe('Usage and AI credits');
    expect(level === 1 ? section : page).toBeNull();
    const subsections = await harness.subsectionTitles();
    expect(subsections.length).toBeGreaterThan(0);
    for (const title of subsections) {
      expect(await title.getAttribute('aria-level')).toBe(level === 1 ? '2' : null);
    }
  });

  it('renders a null metric as unavailable, never zero, without exposing service diagnostics', async () => {
    const { harness, root } = await render();
    const row = (await harness.metrics())[0];
    expect(row).toBeDefined();
    expect(await row?.text()).toContain('Unavailable');
    expect(await row?.text()).not.toContain('0');
    expect(root.querySelector('[data-quota-meter]')).toBeNull();
    expect(root.querySelector('[data-quota-reason]')?.textContent?.trim()).toBe(
      'Not measured yet.',
    );
    expect(root.textContent).not.toContain('egress producer');
    expect(root.querySelector('[data-quota-storage]')?.textContent).not.toContain('0');
    root.querySelector<HTMLButtonElement>('[data-quota-view]')?.click();
    await vi.waitFor(() => {
      const dialog = document.querySelector('[data-quota-dialog]');
      expect(dialog?.querySelector('[data-quota-detail-used]')?.textContent?.trim()).toBe(
        'Unavailable',
      );
      expect(dialog?.querySelector('[data-quota-detail-limit]')?.textContent?.trim()).toBe(
        'Unavailable',
      );
      expect(dialog?.textContent).not.toContain('egress producer');
      expect(dialog?.textContent).not.toContain('No alert recorded');
    });
  });

  it.each(['future_reason_code', 'Storage roll-up of 06.5.2 is not running.', null])(
    'uses the generic localized explanation for unavailable reason %s',
    async (reason) => {
      usageSummary.set({
        period: '2026-11',
        project: null,
        metrics: [metric({ unavailable_reason: reason })],
        next_cursor: null,
      });
      const { root } = await render();
      expect(root.querySelector('[data-quota-reason]')?.textContent?.trim()).toBe(
        'Not measured yet.',
      );
      if (reason) expect(root.textContent).not.toContain(reason);
    },
  );
  it('renders a measured zero as zero rather than unavailable', async () => {
    usageSummary.set({
      period: '2026-11',
      project: null,
      metrics: [metric({ used: 0, limit: 100, unavailable_reason: null })],
      next_cursor: null,
    });
    const { root } = await render();
    expect(root.querySelector('[data-quota-used]')?.textContent?.trim()).toBe('0');
    expect(root.querySelector('[data-quota-limit]')?.textContent?.trim()).toBe('100');
    expect(root.querySelector<HTMLElement>('[data-quota-meter] > div')?.style.width).toBe('0%');
    expect(load).toHaveBeenCalledOnce();
    expect(root.querySelector('[data-quota-reason]')).toBeNull();
  });

  it.each(['warn', 'blocked'] as const)('names the served %s state', async (severity) => {
    usageSummary.set({
      period: '2026-11',
      project: null,
      metrics: [
        metric({
          used: 80,
          limit: 100,
          unavailable_reason: null,
          warned_at: '2026-11-01T00:00:00Z',
          blocked_at: severity === 'blocked' ? '2026-11-02T00:00:00Z' : null,
        }),
      ],
      next_cursor: null,
    });
    const { root } = await render();
    expect(root.textContent).toContain(severity === 'blocked' ? 'Limit reached' : 'Near limit');
    expect(root.querySelector<HTMLElement>('[data-quota-meter] > div')?.style.width).toBe('80%');
    expect(root.querySelector('time')?.textContent).toContain('UTC');
    expect(root.querySelector('time')?.getAttribute('datetime')).toBe('2026-11-01T00:00:00Z');
  });

  it('announces loading without presenting stale counters', async () => {
    loadState.set('loading');
    const { root } = await render();
    expect(root.querySelector('[aria-busy]')?.getAttribute('aria-busy')).toBe('true');
    expect(root.querySelector('[role="status"]')?.textContent).toContain('Loading');
    expect(root.querySelector('[data-quota-metric]')).toBeNull();
  });

  it('offers one explicit retry for an unavailable summary', async () => {
    loadState.set('unavailable');
    usageSummary.set(null);
    const { harness, root } = await render();
    expect(root.querySelector('[data-quota-unavailable]')).not.toBeNull();
    await (await harness.retry()).click();
    expect(reload).toHaveBeenCalledOnce();
  });

  it('distinguishes an empty metric list from an unavailable summary', async () => {
    usageSummary.set({ period: '2026-11', project: null, metrics: [], next_cursor: null });
    const { root } = await render();
    expect(root.querySelector('[data-quota-empty]')).not.toBeNull();
    expect(root.querySelector('[data-quota-unavailable]')).toBeNull();
  });

  it('reserves the short View label in both the project and recorded-state tables', async () => {
    usageSummary.set({
      period: '2026-11',
      project: null,
      metrics: [metric({ projects: [project()] })],
      next_cursor: null,
    });
    const { harness } = await render();
    const actions = await harness.viewActions();
    expect(actions).toHaveLength(2);
    for (const action of actions) expect(await action.hasClass('short-action')).toBe(true);
  });

  it('distinguishes the organization counter from its project breakdown in heading navigation', async () => {
    usageSummary.set({
      period: '2026-11',
      project: null,
      metrics: [metric({ metric: 'results', projects: [project()] })],
      next_cursor: null,
    });
    const { harness } = await render();
    expect(await (await harness.metricHeading()).text()).toBe('Results');
    expect(await (await harness.projectHeading()).text()).toBe('Results by project');
  });

  it('renders the project counters, reading a null limit as unavailable and a zero as zero', async () => {
    usageSummary.set({
      period: '2026-11',
      project: null,
      metrics: [
        metric({
          used: 120,
          limit: 1000,
          unavailable_reason: null,
          projects: [
            project({ project_id: 'project-one', used: 7, limit: null }),
            project({
              project_id: 'project-two',
              used: 0,
              limit: 100,
              warned_at: '2026-11-01T00:00:00Z',
            }),
          ],
        }),
      ],
      next_cursor: null,
    });
    const { harness, root } = await render();
    const rows = await harness.projectRows();
    expect(rows).toHaveLength(2);
    expect(await rows[0]?.text()).toContain('project-one');
    expect(root.querySelectorAll('[data-quota-project-used]')[0]?.textContent).toContain('7');
    const unavailable = root.querySelectorAll('[data-quota-project-limit]')[0]?.textContent;
    expect(unavailable).toContain('Unavailable');
    expect(unavailable).not.toContain('0');
    expect(root.querySelectorAll('[data-quota-project-used]')[1]?.textContent).toContain('0');
    expect(await rows[1]?.text()).toContain('Near limit');
    const trigger = root.querySelector<HTMLButtonElement>(
      '[data-row-id="project-two"] [data-quota-view]',
    );
    trigger?.focus();
    trigger?.click();
    await vi.waitFor(() => expect(document.querySelector('[role="dialog"]')).not.toBeNull());
    const dialog = document.querySelector<HTMLElement>('[role="dialog"]');
    expect(dialog?.textContent).toContain('project-two');
    expect(dialog?.textContent).toContain('Egress (GB)');
    expect(dialog?.textContent).not.toContain('project-one');
    expect(dialog?.querySelector('[data-quota-detail-used]')?.textContent?.trim()).toBe('0');
    expect(dialog?.querySelector('[data-quota-detail-limit]')?.textContent?.trim()).toBe('100');
    expect(dialog?.querySelector('time')?.getAttribute('datetime')).toBe('2026-11-01T00:00:00Z');
    dialog?.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }),
    );
    await vi.waitFor(() => expect(document.querySelector('[role="dialog"]')).toBeNull());
    expect(document.activeElement).toBe(trigger);
  });

  it('makes all returned project rows inspectable through the shared table', async () => {
    usageSummary.set({
      period: '2026-11',
      project: null,
      metrics: [
        metric({
          used: 120,
          limit: 1000,
          unavailable_reason: null,
          projects: Array.from({ length: 7 }, (_unused, index) =>
            project({ project_id: `project-${index}` }),
          ),
        }),
      ],
      next_cursor: null,
    });
    const { fixture, harness, root } = await render();
    expect(await harness.projectRows()).toHaveLength(7);
    expect(root.querySelector('[data-row-id="project-6"]')).not.toBeNull();
    const table = fixture.debugElement.query(By.directive(DataTableComponent))
      .componentInstance as DataTableComponent<UsageSummaryProjectDto>;
    expect(table.rows()).toHaveLength(7);
    expect(root.textContent).not.toContain('additional projects');
    expect(nextProjectPage).not.toHaveBeenCalled();
    expect(previousProjectPage).not.toHaveBeenCalled();
    expect(setProjectPageSize).not.toHaveBeenCalled();
  });

  it('derives the metric ids from the row index rather than the served metric key', async () => {
    usageSummary.set({
      period: '2026-11',
      project: null,
      metrics: [
        metric({ metric: 'results', unavailable_reason: null }),
        metric({ metric: 'egress_gb', unavailable_reason: null }),
      ],
      next_cursor: null,
    });
    const { root } = await render();
    const sections = root.querySelectorAll('[data-quota-metric]');
    expect(sections[0]?.querySelector('h3')?.id).toBe('quota-metric-0');
    expect(sections[1]?.querySelector('h3')?.id).toBe('quota-metric-1');
    expect(sections[0]?.getAttribute('aria-labelledby')).toBe('quota-metric-0');
    expect(sections[1]?.getAttribute('aria-labelledby')).toBe('quota-metric-1');
  });
  it('keeps exact fractions, large counters and measurement labels without compact notation', async () => {
    usageSummary.set({
      period: '2026-11',
      project: null,
      next_cursor: null,
      metrics: [metric({ used: 123456789.1234, limit: 987654321, unavailable_reason: null })],
    });
    const { root } = await render();
    expect(root.querySelector('[data-quota-used]')?.textContent?.trim()).toBe('123,456,789.1234');
    expect(root.querySelector('[data-quota-limit]')?.textContent?.trim()).toBe('987,654,321');
  });
  it('passes every row beyond the 200-row rendering boundary to the shared bounded renderer', async () => {
    useLightweightTablePresentation();
    const projects = Array.from({ length: 201 }, (_unused, index) =>
      project({ project_id: `project-${index}` }),
    );
    usageSummary.set({
      period: '2026-11',
      project: null,
      metrics: [metric({ projects })],
      next_cursor: 'next-page',
    });
    const { fixture, root } = await render();
    const table = fixture.debugElement.query(By.directive(DataTableComponent))
      .componentInstance as DataTableComponent<UsageSummaryProjectDto>;
    expect(table.rows()).toHaveLength(201);
    expect(root.querySelector('[data-quota-projects-more]')?.textContent).toContain(
      'All counters returned',
    );
    const next = Array.from(
      root.querySelectorAll<HTMLButtonElement>('app-data-table nav button'),
    ).find((button) => button.textContent?.includes('Next'));
    expect(next).toBeDefined();
    next?.click();
    await fixture.whenStable();
    expect(root.querySelector('[data-row-id="project-200"]')).not.toBeNull();
  });

  it('uses one coordinated server pager after all project tables without paging recorded metric state', async () => {
    usageSummary.set({
      period: '2026-11',
      project: null,
      next_cursor: 'next',
      metrics: [
        metric({ metric: 'results', used: 900, projects: [project()] }),
        metric({ metric: 'ai_tokens', used: 800, projects: [project()] }),
      ],
    });
    const { harness } = await render();
    expect(await harness.pagers()).toHaveLength(1);
    expect(await harness.projectRows()).toHaveLength(2);
    expect(await harness.recordedRows()).toHaveLength(2);
    expect(await (await harness.pagers())[0]?.text()).toContain('Page 1');
    await (await harness.pagerNext()).click();
    expect(nextProjectPage).toHaveBeenCalledOnce();
    expect(reload).not.toHaveBeenCalled();
  });

  it('retains project rows with explicit page-error recovery and preserves pager state on View', async () => {
    projectPage.set(2);
    projectPageSize.set(50);
    projectPagingError.set(true);
    usageSummary.set({
      period: '2026-11',
      project: null,
      next_cursor: null,
      metrics: [metric({ projects: [project()] })],
    });
    const { harness } = await render();
    expect(await harness.projectRows()).toHaveLength(1);
    expect(await (await harness.pageError())?.text()).toContain('previous page is still shown');
    expect(await (await harness.pagers())[0]?.text()).toContain('Page 2');
    await (await harness.retryPage()).click();
    expect(retryProjectPage).toHaveBeenCalledOnce();
    await (await harness.viewActions())[0]?.click();
    expect(projectPage()).toBe(2);
    expect(projectPageSize()).toBe(50);
    expect(setProjectPageSize).not.toHaveBeenCalled();
    expect(reload).not.toHaveBeenCalled();
  });

  it('keeps one pager available on an empty final project page and exposes Previous', async () => {
    projectPage.set(2);
    const { harness } = await render();
    expect(await harness.projectRows()).toHaveLength(0);
    expect(await harness.pagers()).toHaveLength(1);
    expect(await (await harness.pagerNext()).getAttribute('aria-disabled')).toBe('true');
    await (await harness.pagerPrevious()).click();
    expect(previousProjectPage).toHaveBeenCalledOnce();
  });

  it.each([50, 100])('passes selected size %s to the coordinated store control', async (size) => {
    const { harness } = await render();
    expect(await (await harness.sizeTrigger()).text()).toBe('20');
    await (await harness.sizeTrigger()).click();
    const options = await harness.sizeOptions();
    const labels = await Promise.all(options.map((option) => option.text()));
    expect(labels).toEqual(['20', '50', '100']);
    const option = options[labels.indexOf(String(size))];
    if (!option) throw new Error('Missing requested page size');
    await option.click();
    expect(setProjectPageSize).toHaveBeenCalledExactlyOnceWith(size);
    expect(nextProjectPage).not.toHaveBeenCalled();
  });
});
