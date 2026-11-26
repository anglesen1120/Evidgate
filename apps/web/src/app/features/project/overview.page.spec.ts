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
    '[data-dashboard-kpis], [data-dashboard-trend], [data-dashboard-recent], [data-dashboard-attention], [data-dashboard-authoring], [data-dashboard-usage], [data-dashboard-activity]',
  );
  readonly text = async () => (await this.host()).text();
  readonly runViews = this.locatorForAll('[data-dashboard-run-view]');
  readonly subjects = this.locatorForAll('app-dashboard-runs bdi');
  readonly registerCopy = this.locatorForAll('app-dashboard-runs app-copyable-identifier');
  readonly subjectTime = this.locatorFor('app-dashboard-runs time:not([title])');
  readonly strips = this.locatorForAll('[data-dashboard-kpis] dl');
  readonly readings = this.locatorForAll('[data-dashboard-kpis] dd');
  readonly totals = this.locatorForAll('[role="toolbar"] button .trend-total');
  readonly failureLabel = this.locatorFor('[data-dashboard-failures] dt');
  readonly failureValue = this.locatorFor('[data-dashboard-failures] app-number-value');
  readonly failureDescription = this.locatorFor('[data-dashboard-failures] p');
  readonly attentionMetrics = this.locatorForAll('[data-dashboard-attention] app-dashboard-metric');
  readonly emptyIcons = this.locatorForAll('app-empty-pattern hlm-icon path');
  readonly incidents = this.locatorFor('[data-dashboard-incidents]');
  readonly incidentControls = this.locatorForAll('[data-dashboard-incidents] button');
  readonly incidentErrors = this.locatorForAll(
    '[data-dashboard-incidents] app-error-pattern, [data-dashboard-incidents] [role="alert"]',
  );
  readonly title = this.locatorFor('h1');
  readonly subtitle = this.locatorFor('app-page-header .header-description');
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
  const currentProject = signal<{ id: string; name: string; slug: string } | null>({
    id: 'p1',
    name: 'Project A',
    slug: 'a',
  });
  const store = {
    runs: read({ items: rows, complete }),
    waivers: read(page),
    quarantine: read(page),
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
          currentProject,
          currentOrg: signal({ id: 'o1', name: 'Organization A' }),
          has: (permission: string) => allowed && permission === 'healing:triage',
        },
      },
      { provide: NavViewService, useValue: { groups: signal([]) } },
    ],
  });
  TestBed.overrideComponent(ProjectOverviewPage, {
    set: { providers: [{ provide: ProjectDashboardStore, useValue: store }] },
  });
  const fixture = TestBed.createComponent(ProjectOverviewPage);
  return { fixture, store, currentProject };
}

describe('ProjectOverviewPage dashboard', () => {
  afterEach(() => vi.restoreAllMocks());

  it('renders all seven independent widgets without treating collecting as PASS', async () => {
    const { fixture } = mount();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await harness.widgets()).toHaveLength(7);
    expect(await harness.text()).toContain('Still collecting');
    expect(await harness.text()).toContain('0%');
    expect(await harness.text()).toContain('Not evaluated');
    expect(await harness.runViews()).toHaveLength(1);
  });

  it('identifies runs by safe reference names and keeps identifiers out of the register', async () => {
    const { fixture } = mount([{ ...RUN, ref: { ...RUN.ref, name: 'main\u202E' } }]);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await (await harness.subjects())[0]!.text()).toBe('main⟨U+202E⟩');
    expect(await harness.registerCopy()).toHaveLength(0);
  });

  it('uses the absolute creation time as the subject when the run has no reference', async () => {
    const { fixture } = mount([{ ...RUN, ref: { ...RUN.ref, name: null } }]);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await (await harness.subjectTime()).getAttribute('datetime')).toBe(RUN.created_at);
    expect(await harness.registerCopy()).toHaveLength(0);
  });

  it('scopes capped counts to the latest 100 even when all source pages loaded', async () => {
    vi.spyOn(Date, 'now').mockReturnValue(Date.parse('2026-11-24T12:00:00Z'));
    const rows = Array.from({ length: 101 }, (_, i): RunSummary => ({
      ...RUN,
      run_id: `run-${i}`,
      decision: i === 0 ? 'FAIL' : 'PASS',
    }));
    const { fixture } = mount(rows);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await (await harness.readings())[0]!.text()).toMatch(/latest 100/i);
    expect(await (await harness.failureValue()).text()).toBe('0');
  });

  it('distinguishes dated counts, the entire sample and the last seven days', async () => {
    vi.spyOn(Date, 'now').mockReturnValue(Date.parse('2026-11-24T12:00:00Z'));
    const decisions = (pass: number, fail: number, inconclusive: number) => [
      ...Array<NonNullable<RunSummary['decision']>>(pass).fill('PASS'),
      ...Array<NonNullable<RunSummary['decision']>>(fail).fill('FAIL'),
      ...Array<NonNullable<RunSummary['decision']>>(inconclusive).fill('INCONCLUSIVE'),
    ];
    const rows = [decisions(1, 1, 1), decisions(18, 7, 5)].flatMap((day, index) =>
      day.map((decision, i) => ({
        ...RUN,
        run_id: `${index}-${i}`,
        created_at: `2026-11-${index ? '23' : '15'}T00:00:00Z`,
        decision,
      })),
    );
    const { fixture } = mount(rows);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await harness.strips()).toHaveLength(1);
    const readings = await Promise.all((await harness.readings()).map((item) => item.text()));
    expect(readings.slice(1, 4)).toEqual([
      expect.stringMatching(/57\.6%.*19\s*\/\s*33/s),
      expect.stringMatching(/24\.2%.*8\s*\/\s*33/s),
      expect.stringMatching(/18\.2%.*6\s*\/\s*33/s),
    ]);
    expect(await Promise.all((await harness.totals()).map((total) => total.text()))).toEqual([
      '3',
      '30',
    ]);
    expect(await (await harness.failureValue()).text()).toBe('7');
    expect(await (await harness.failureDescription()).text()).toMatch(/7 days.*8/s);
    expect(await harness.runViews()).toHaveLength(5);
  });

  it('shows incidents as neutral unavailable text without an error or retry control', async () => {
    const { fixture } = mount();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await (await harness.incidents()).text()).toBe('Incidents Not available yet');
    expect(await harness.incidentControls()).toHaveLength(0);
    expect(await harness.incidentErrors()).toHaveLength(0);
  });

  it('keeps the overview title and both project and organization context', async () => {
    const { fixture } = mount();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await (await harness.title()).text()).toBe('Project overview');
    expect(await (await harness.subtitle()).text()).toBe('Project A · Organization A');
  });

  it('distinguishes successful empty runs from unavailable usage and hides denied widgets', async () => {
    const { fixture } = mount([]);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await harness.text()).toContain('No runs yet.');
    expect(await harness.text()).toContain('No usage metrics returned.');
    expect(await harness.text()).toContain('No data');
    expect(await harness.runViews()).toHaveLength(0);
    expect(
      await Promise.all((await harness.emptyIcons()).map((path) => path.getAttribute('d'))),
    ).toEqual([
      'M4 20V4M4 20h16M8 16v-4M12 16V8M16 16v-6',
      'M5 3h14v18H5z M10 8l5 4-5 4z',
      'M4 20V4M4 20h16M8 16v-4M12 16V8M16 16v-6',
      'M7 4h10v16H7zM10 8h4M10 12h4M10 16h4',
    ]);
  });

  it('labels truncated ascending run pages as not latest instead of recent', async () => {
    const { fixture } = mount([RUN], false);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await (await harness.readings())[0]?.text()).toMatch(/oldest pages/i);
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

  it('uses a dashboard icon when no project is selected and exposes no widgets', async () => {
    const { fixture, currentProject } = mount();
    currentProject.set(null);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);

    expect(await harness.widgets()).toHaveLength(0);
    const icons = await harness.emptyIcons();
    expect(icons).toHaveLength(1);
    expect(await icons[0]?.getAttribute('d')).toBe(
      'M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z',
    );
  });

  it('shows no restricted facts when every widget is denied', async () => {
    const { fixture } = mount([], true, false);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await harness.widgets()).toHaveLength(0);
    expect(await harness.text()).toContain('No dashboard data is available');
    const icons = await harness.emptyIcons();
    expect(icons).toHaveLength(1);
    expect(await icons[0]?.getAttribute('d')).toBe(
      'M12 2l8 3v6c0 5-4 9-8 11-4-2-8-6-8-11V5z M8 12l3 3 5-6',
    );
  });
});
