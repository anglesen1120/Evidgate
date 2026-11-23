import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import type { RunSummary } from '@evidgate/sdk';
import { SessionStore } from '../../core/session/session.store';
import { NavViewService } from '../../shell/nav/nav-view';
import { ProjectDashboardStore } from './dashboard/project-dashboard.store';
import { ProjectOverviewPage } from './overview.page';

class OverviewHarness extends ComponentHarness {
  static hostSelector = 'app-project-overview';
  readonly widgets = this.locatorForAll(
    '[data-dashboard-kpis], [data-dashboard-trend], [data-dashboard-recent], [data-dashboard-attention], [data-dashboard-authoring], [data-dashboard-ci], [data-dashboard-usage], [data-dashboard-activity]',
  );
  readonly text = async () => (await this.host()).text();
  readonly runViews = this.locatorForAll('[data-dashboard-run-view]');
  readonly failureLabel = this.locatorFor('[data-dashboard-failures] dt');
  readonly failureValue = this.locatorFor('[data-dashboard-failures] app-number-value');
  readonly failureDescription = this.locatorFor('[data-dashboard-failures] p');
  readonly attentionMetrics = this.locatorForAll('[data-dashboard-attention] app-dashboard-metric');
}

function read<T>(value: T) {
  return {
    value: signal<T | undefined>(value),
    visible: signal(true),
    loading: signal(false),
    error: signal(null),
    reload: vi.fn(),
  };
}
const RUN: RunSummary = {
  run_id: 'run-1',
  state: 'collecting',
  created_at: '2026-11-23T09:00:00Z',
  completeness: null,
  parent_run_id: null,
  rerun_scope: null,
  unverified_ref: false,
  decision: null,
  ref: {
    name: 'main',
    kind: 'branch',
    pr_number: null,
    head_sha: null,
    base_sha: null,
    trigger_event: 'push',
    is_fork: false,
  },
};

function mount(rows: readonly RunSummary[] = [RUN], complete = true, allowed = true) {
  const page = { items: [], next_cursor: null };
  const store = {
    runs: read({ items: rows, complete }),
    waivers: read(page),
    quarantine: read(page),
    incidents: read(page),
    bindings: read(page),
    cases: read(page),
    tree: read(page),
    changes: read(page),
    usage: read({ metrics: [], period: '2026-11', project: null, next_cursor: null }),
    audit: read(page),
    decisionsVisible: signal(true),
  };
  if (!allowed)
    for (const value of Object.values(store)) if ('visible' in value) value.visible.set(false);
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      {
        provide: SessionStore,
        useValue: {
          currentProject: signal({ id: 'p1', name: 'Project A', slug: 'a' }),
          currentOrg: signal({ id: 'o1', name: 'Organization A' }),
        },
      },
      { provide: NavViewService, useValue: { groups: signal([]) } },
    ],
  });
  TestBed.overrideComponent(ProjectOverviewPage, {
    set: { providers: [{ provide: ProjectDashboardStore, useValue: store }] },
  });
  const fixture = TestBed.createComponent(ProjectOverviewPage);
  return { fixture, store };
}

describe('ProjectOverviewPage dashboard', () => {
  afterEach(() => vi.restoreAllMocks());

  it('renders all eight independent widgets without treating collecting as PASS', async () => {
    const { fixture } = mount();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await harness.widgets()).toHaveLength(8);
    expect(await harness.text()).toContain('Still collecting');
    expect(await harness.text()).toContain('0%');
    expect(await harness.text()).toContain('Not evaluated');
    expect(await harness.runViews()).toHaveLength(1);
  });

  it('distinguishes successful empty runs from unavailable usage and hides denied widgets', async () => {
    const { fixture } = mount([]);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await harness.text()).toContain('No runs yet.');
    expect(await harness.text()).toContain('No usage metrics returned.');
    expect(await harness.text()).toContain('No data');
    expect(await harness.runViews()).toHaveLength(0);
  });

  it('labels truncated ascending run pages as not latest instead of recent', async () => {
    const { fixture } = mount([RUN], false);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await harness.text()).toContain('oldest loaded runs — not the latest');
    expect(await harness.text()).not.toContain('Recent runs');
  });

  it.each<{ decision: Exclude<RunSummary['decision'], undefined>; count: string }>([
    { decision: null, count: '0' },
    { decision: 'FAIL', count: '1' },
  ])(
    'withholds New FAILs for incomplete $decision samples and restores $count when complete',
    async ({ decision, count }) => {
      vi.spyOn(Date, 'now').mockReturnValue(Date.parse('2026-11-23T12:00:00Z'));
      const rows = [{ ...RUN, decision }];
      const { fixture, store } = mount(rows, false);
      const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
      const otherMetricText = await Promise.all(
        (await harness.attentionMetrics()).slice(1).map((metric) => metric.text()),
      );

      expect(await (await harness.failureLabel()).text()).toBe('New FAILs');
      expect(await (await harness.failureValue()).text()).toBe('Number unavailable');
      expect(await (await harness.failureDescription()).text()).toBe(
        'Not available (latest runs not loaded)',
      );

      store.runs.value.set({ items: rows, complete: true });

      expect(await (await harness.failureLabel()).text()).toBe('New FAILs');
      expect(await (await harness.failureValue()).text()).toBe(count);
      expect(await (await harness.failureDescription()).text()).toBe(
        'FAIL decisions among sampled runs created in the last 7 days.',
      );
      expect(
        await Promise.all(
          (await harness.attentionMetrics()).slice(1).map((metric) => metric.text()),
        ),
      ).toEqual(otherMetricText);
    },
  );

  it('withholds New FAILs when run sample completeness is unknown', async () => {
    const { fixture, store } = mount();
    store.runs.value.set(undefined);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);

    expect(await (await harness.failureLabel()).text()).toBe('New FAILs');
    expect(await (await harness.failureValue()).text()).toBe('Number unavailable');
    expect(await (await harness.failureDescription()).text()).toBe(
      'Not available (latest runs not loaded)',
    );
  });

  it('shows no restricted facts when every widget is denied', async () => {
    const { fixture } = mount([], true, false);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await harness.widgets()).toHaveLength(0);
    expect(await harness.text()).toContain('No dashboard data is available');
  });
});
