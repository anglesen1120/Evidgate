import { ComponentHarness, manualChangeDetection } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import type { ProjectDto, RunPage } from '@evidgate/sdk';
import { firstValueFrom, Subject } from 'rxjs';
import { AuditDataService } from '../../core/data/audit-data.service';
import { MembersDataService } from '../../core/data/members-data.service';
import { OnboardingDataService } from '../../core/data/onboarding-data.service';
import { QuotaDataService } from '../../core/data/quota-data.service';
import { SessionStore } from '../../core/session/session.store';
import { ApiRequestError } from '../../core/http/api-error';
import { mapNetworkError } from '../../core/http/error-mapper';
import { OrgOverviewPage } from './org-overview.page';
import { OrgOverviewStore } from './org-overview.store';

class OverviewHarness extends ComponentHarness {
  static hostSelector = 'app-org-overview';
  readonly text = async () => (await this.host()).text();
  readonly widgets = this.locatorForAll('app-dashboard-widget');
  readonly errors = this.locatorForAll('app-error-pattern');
  readonly decisions = this.locatorForAll('app-decision-chip');
  readonly retryButtons = this.locatorForAll('button[data-run-summary-retry]');
  readonly projectLinks = this.locatorForAll('a');
  readonly runSummary = this.locatorFor('[data-run-summary]');
}

const project: ProjectDto = {
  id: 'p1',
  org_id: 'o1',
  name: 'Shop',
  slug: 'shop',
  status: 'active',
  default_environment_id: null,
  created_at: '2026-11-01T00:00:00Z',
  updated_at: '2026-11-01T00:00:00Z',
  version: 1,
  retention_days: null,
  monthly_run_quota: null,
  case_prefix: 'SHOP',
};
const runPage: RunPage = {
  items: [
    {
      run_id: 'r1',
      state: 'collecting',
      created_at: '2026-11-23T00:00:00Z',
      ref: {
        kind: null,
        name: null,
        pr_number: null,
        head_sha: null,
        base_sha: null,
        trigger_event: null,
        is_fork: false,
      },
      unverified_ref: false,
      completeness: null,
      parent_run_id: null,
      rerun_scope: null,
      decision: null,
    },
  ],
  next_cursor: null,
};
describe('OrgOverviewPage', () => {
  const grants = signal(new Set(['project:read', 'membership:read', 'usage:read', 'audit:read']));
  const members = { listMemberships: vi.fn() };
  const projects = { listProjects: vi.fn(), listRuns: vi.fn() };
  const quota = { summary: vi.fn() };
  const audit = { list: vi.fn() };
  beforeEach(() => {
    grants.set(new Set(['project:read', 'membership:read', 'usage:read', 'audit:read']));
    members.listMemberships.mockReset().mockResolvedValue({ items: [], next_cursor: null });
    projects.listProjects.mockReset().mockResolvedValue({ items: [], next_cursor: null });
    projects.listRuns.mockReset().mockResolvedValue(runPage);
    quota.summary.mockReset().mockResolvedValue({
      period: '2026-11',
      project: null,
      metrics: [],
      next_cursor: null,
    });
    audit.list.mockReset().mockResolvedValue({ items: [], next_cursor: null });
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: SessionStore,
          useValue: {
            currentOrg: signal({ id: 'o1', name: 'Acme' }),
            has: (code: string) => grants().has(code),
          },
        },
        {
          provide: OnboardingDataService,
          useValue: projects,
        },
        { provide: MembersDataService, useValue: members },
        {
          provide: QuotaDataService,
          useValue: quota,
        },
        {
          provide: AuditDataService,
          useValue: audit,
        },
      ],
    });
  });
  it('renders independent successful empty widgets without fabricated credit', async () => {
    const fixture = TestBed.createComponent(OrgOverviewPage);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await harness.widgets()).toHaveLength(4);
    const text = await harness.text();
    expect(text).toContain('No projects to show.');
    expect(text).toContain('No organization activity recorded.');
    expect(text).toContain('Not metered');
    expect(await harness.errors()).toHaveLength(0);
  });
  it('keeps other widgets visible when memberships fail', async () => {
    members.listMemberships.mockRejectedValue(new Error('offline'));
    const fixture = TestBed.createComponent(OrgOverviewPage);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await harness.errors()).toHaveLength(1);
    expect(await harness.text()).toContain('No projects to show.');
    expect(await harness.text()).toContain('Not metered');
  });
  it('hides unauthorized widgets and shows one neutral access explanation', async () => {
    grants.set(new Set());
    const fixture = TestBed.createComponent(OrgOverviewPage);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await harness.widgets()).toHaveLength(0);
    expect(await harness.errors()).toHaveLength(0);
    expect(await harness.text()).toContain('No dashboard data is available');
    expect(members.listMemberships).not.toHaveBeenCalled();
  });
  it('hides a widget whose server permission was revoked rather than rendering a 403 card', async () => {
    members.listMemberships.mockRejectedValue(
      new ApiRequestError({ ...mapNetworkError(), kind: 'forbidden', status: 403 }),
    );
    const fixture = TestBed.createComponent(OrgOverviewPage);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await harness.widgets()).toHaveLength(3);
    expect(await harness.errors()).toHaveLength(0);
    expect(await harness.text()).not.toContain('Members and invitations');
  });

  it('hides decisions separately from runs and never labels hidden decisions as not evaluated', async () => {
    grants.set(new Set(['project:read', 'run:read']));
    projects.listProjects.mockResolvedValue({ items: [project], next_cursor: null });
    const fixture = TestBed.createComponent(OrgOverviewPage);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await harness.decisions()).toHaveLength(0);
    expect(await harness.text()).toContain('Latest run (UTC)');
    expect(await harness.text()).not.toContain('Latest decision');
    expect(await harness.text()).not.toContain('Not evaluated');
    grants.set(new Set(['project:read', 'run:read', 'decision:read']));
    expect(await harness.decisions()).toHaveLength(1);
    expect(await harness.text()).toContain('Latest decision');
  });

  it('keeps the project name and View link but hides forbidden run facts and retry', async () => {
    grants.set(new Set(['project:read', 'run:read', 'decision:read']));
    projects.listProjects.mockResolvedValue({ items: [project], next_cursor: null });
    projects.listRuns.mockRejectedValue(
      new ApiRequestError({ ...mapNetworkError(), kind: 'forbidden', status: 403 }),
    );
    const fixture = TestBed.createComponent(OrgOverviewPage);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await harness.text()).toContain('Shop');
    expect(await harness.projectLinks()).toHaveLength(1);
    expect(await harness.decisions()).toHaveLength(0);
    expect(await harness.retryButtons()).toHaveLength(0);
    expect(await harness.text()).not.toContain('Latest run (UTC)');
    expect(await harness.text()).not.toContain('Couldn’t load latest run.');
    expect(await harness.text()).not.toContain('No dashboard data is available');
  });

  it('offers one retry for transient run failures and moves focus before replacing it', async () => {
    grants.set(new Set(['project:read', 'run:read', 'decision:read']));
    projects.listProjects.mockResolvedValue({
      items: [project, { ...project, id: 'p2', name: 'Other shop' }],
      next_cursor: null,
    });
    projects.listRuns.mockRejectedValue(new Error('offline'));
    const fixture = TestBed.createComponent(OrgOverviewPage);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    const buttons = await harness.retryButtons();
    expect(buttons).toHaveLength(1);
    expect(await harness.text()).toContain('Couldn’t load latest run.');
    projects.listRuns.mockResolvedValue(runPage);
    await buttons[0]?.focus();
    await buttons[0]?.click();
    expect(await (await harness.runSummary()).isFocused()).toBe(true);
    expect(await harness.retryButtons()).toHaveLength(0);
    expect(await harness.decisions()).toHaveLength(2);
  });

  it('uses the effective widget predicates for no-access after all server reads return 403', async () => {
    const denied = new ApiRequestError({ ...mapNetworkError(), kind: 'forbidden', status: 403 });
    projects.listProjects.mockRejectedValue(denied);
    members.listMemberships.mockRejectedValue(denied);
    quota.summary.mockRejectedValue(denied);
    audit.list.mockRejectedValue(denied);
    const fixture = TestBed.createComponent(OrgOverviewPage);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await harness.widgets()).toHaveLength(0);
    expect(await harness.text()).toContain('No dashboard data is available');
    members.listMemberships.mockResolvedValue({ items: [], next_cursor: null });
    grants.set(new Set());
    await harness.text();
    grants.set(new Set(['membership:read']));
    expect(await harness.widgets()).toHaveLength(1);
    expect(await harness.text()).not.toContain('No dashboard data is available');
  });

  it('shows Loading instead of Latest unavailable while run enrichment is pending', async () => {
    grants.set(new Set(['project:read', 'run:read', 'decision:read']));
    const pending = new Subject<RunPage>();
    projects.listProjects.mockResolvedValue({ items: [project], next_cursor: null });
    projects.listRuns.mockReturnValue(firstValueFrom(pending));
    const fixture = TestBed.createComponent(OrgOverviewPage);
    await manualChangeDetection(async () => {
      const store = fixture.debugElement.injector.get(OrgOverviewStore);
      await vi.waitFor(() => {
        TestBed.tick();
        fixture.detectChanges();
        expect(store.projectRows()[0]?.runStatus).toBe('loading');
      });
      const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
      expect(await harness.text()).toContain('Loading');
      expect(await harness.text()).not.toContain('Latest unavailable');
    });
    pending.next(runPage);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await harness.decisions()).toHaveLength(1);
  });
});
