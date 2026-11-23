import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuditDataService } from '../../core/data/audit-data.service';
import { MembersDataService } from '../../core/data/members-data.service';
import { OnboardingDataService } from '../../core/data/onboarding-data.service';
import { QuotaDataService } from '../../core/data/quota-data.service';
import { SessionStore } from '../../core/session/session.store';
import { ApiRequestError } from '../../core/http/api-error';
import { mapNetworkError } from '../../core/http/error-mapper';
import { OrgOverviewPage } from './org-overview.page';

class OverviewHarness extends ComponentHarness {
  static hostSelector = 'app-org-overview';
  readonly text = async () => (await this.host()).text();
  readonly widgets = this.locatorForAll('app-dashboard-widget');
  readonly errors = this.locatorForAll('app-error-pattern');
}

describe('OrgOverviewPage', () => {
  const grants = signal(new Set(['project:read', 'membership:read', 'usage:read', 'audit:read']));
  const members = { listMemberships: vi.fn() };
  beforeEach(() => {
    grants.set(new Set(['project:read', 'membership:read', 'usage:read', 'audit:read']));
    members.listMemberships.mockReset().mockResolvedValue({ items: [], next_cursor: null });
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
          useValue: { listProjects: () => Promise.resolve({ items: [], next_cursor: null }) },
        },
        { provide: MembersDataService, useValue: members },
        {
          provide: QuotaDataService,
          useValue: {
            summary: () =>
              Promise.resolve({ period: '2026-11', project: null, metrics: [], next_cursor: null }),
          },
        },
        {
          provide: AuditDataService,
          useValue: { list: () => Promise.resolve({ items: [], next_cursor: null }) },
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
});
