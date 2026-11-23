import { ComponentHarness, manualChangeDetection } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import type { ProjectDto, RunPage } from '@evidgate/sdk';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { firstValueFrom, Subject } from 'rxjs';
import { AuditDataService } from '../../core/data/audit-data.service';
import { MembersDataService } from '../../core/data/members-data.service';
import { OnboardingDataService } from '../../core/data/onboarding-data.service';
import { QuotaDataService } from '../../core/data/quota-data.service';
import { SessionStore } from '../../core/session/session.store';
import { ApiRequestError } from '../../core/http/api-error';
import { mapNetworkError } from '../../core/http/error-mapper';
import { PageHeaderComponent } from '../../shared/ui/page-header';
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
  readonly titleIcon = this.locatorFor('app-page-header h1 hlm-icon');
  readonly projectTitles = this.locatorForAll('.projects h2');
  readonly projectTable = this.locatorFor('.projects app-data-table');
  readonly tableRole = this.locatorFor('.projects [role="table"]');
  readonly tableCaption = this.locatorFor('.projects app-data-table > .caption');
  readonly decisionPrefix = this.locatorFor('app-decision-chip .verdict__label');
  readonly usageTerms = this.locatorForAll('.usage dl.hlm-field-grid > div.grid-cols-2 > dt');
  readonly usageValues = this.locatorForAll('.usage dl.hlm-field-grid > div.grid-cols-2 > dt + dd');
  readonly countNotice = this.locatorFor('.projects [data-project-count]');
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
  it('uses a decorative icon inside the page heading', async () => {
    const fixture = TestBed.createComponent(OrgOverviewPage);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await (await harness.titleIcon()).getAttribute('aria-hidden')).toBe('true');
    const header = fixture.debugElement.query(By.directive(PageHeaderComponent));
    expect(header.injector.get(PageHeaderComponent).icon()).toBe('navGrid');
  });

  it('shows one project title and uses the visually hidden caption to name the table', async () => {
    projects.listProjects.mockResolvedValue({ items: [project], next_cursor: null });
    const fixture = TestBed.createComponent(OrgOverviewPage);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    const titles = await harness.projectTitles();
    expect(await Promise.all(titles.map((title) => title.text()))).toEqual(['My projects']);
    const caption = await harness.tableCaption();
    expect(await caption.text()).toBe('My projects');
    expect(await (await harness.tableRole()).getAttribute('aria-labelledby')).toBe(
      await caption.getAttribute('id'),
    );
    expect(await (await harness.projectTable()).hasClass('[&>.caption]:sr-only')).toBe(true);
  });

  it('pairs results usage and limit with their values and preserves period and token context', async () => {
    quota.summary.mockResolvedValue({
      period: '2026-11',
      metrics: [
        { metric: 'results', used: 62, limit: null },
        { metric: 'ai_tokens', used: 0, limit: null },
      ],
    });
    const fixture = TestBed.createComponent(OrgOverviewPage);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    const terms = await harness.usageTerms();
    const values = await harness.usageValues();
    expect(await Promise.all(terms.map((term) => term.text()))).toEqual(['Used', 'Limit']);
    expect(await Promise.all(values.map((value) => value.text()))).toEqual(['62', 'Unavailable']);
    const text = await harness.text();
    expect(text).toContain('Period: 2026-11');
    expect(text).toContain('Results');
    expect(text).toMatch(/AI tokens\s*0/);
  });

  it.each([
    [1, null, 'Showing 1 project'],
    [2, null, 'Showing 2 projects'],
    [1, 'next', 'Showing 1 project; more available'],
    [2, 'next', 'Showing 2 projects; more available'],
  ] as const)('describes %s loaded projects with cursor %s', async (count, cursor, expected) => {
    projects.listProjects.mockResolvedValue({
      items: count === 1 ? [project] : [project, { ...project, id: 'p2' }],
      next_cursor: cursor,
    });
    const fixture = TestBed.createComponent(OrgOverviewPage);
    const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
    expect(await (await harness.countNotice()).text()).toBe(expected);
  });

  it('uses one Vietnamese plural form for all counts in both paging states', () => {
    const catalog = new DOMParser().parseFromString(
      readFileSync(
        resolve(dirname(fileURLToPath(import.meta.url)), '../../../locale/messages.vi.xlf'),
        'utf8',
      ),
      'application/xml',
    );
    for (const [key, suffix] of [
      ['projects-count', ''],
      ['projects-more', '; còn dự án khác'],
    ]) {
      const target = catalog.querySelector(`trans-unit[id="orgoverview.${key}"] target`);
      expect(target?.querySelector('x')?.getAttribute('id')).toBe('INTERPOLATION');
      target?.querySelector('x')?.replaceWith('N');
      expect(target?.textContent?.trim()).toBe(
        `{VAR_PLURAL, plural, other {Hiển thị N dự án${suffix}}}`,
      );
    }
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
    const prefix = await harness.decisionPrefix();
    expect(await prefix.hasClass('sr-only')).toBe(true);
    expect(await prefix.getAttribute('aria-hidden')).toBeNull();
    expect(await prefix.text()).toBe('Decision:');
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

  it.each([false, true])(
    'shows one run-read error per project with decision access=%s',
    async (canDecisions) => {
      grants.set(new Set(['project:read', 'run:read', ...(canDecisions ? ['decision:read'] : [])]));
      projects.listProjects.mockResolvedValue({ items: [project], next_cursor: null });
      projects.listRuns.mockRejectedValue(new Error('offline'));
      const fixture = TestBed.createComponent(OrgOverviewPage);
      const harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, OverviewHarness);
      expect((await harness.text()).match(/Couldn’t load latest run\./g)).toHaveLength(1);
      expect((await harness.text()).includes('Latest decision')).toBe(canDecisions);
      expect(await harness.decisions()).toHaveLength(0);
    },
  );

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
    expect(await (await harness.runSummary()).getAttribute('tabindex')).toBe('-1');
    expect(await (await harness.runSummary()).getAttribute('aria-label')).toBeNull();
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
