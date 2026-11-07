// The QuotaStore badge rules, pinned against the fake data service both store specs share
// (`quota.store.test-support.ts`):
//
// - a blocked counter wins over a warned one;
// - a project crossing, read from the metric's org-wide count, is named as well as the tenant's;
// - a counter with no measurement never proves a threshold, so it is never read as a warning.

import { TestBed } from '@angular/core/testing';

import { TenantStore } from '../../../core/session/tenant.store';
import { QuotaStore } from './quota.store';
import {
  FakeQuotaDataService,
  metric,
  mountQuotaStore,
  project,
  summary,
} from './quota.store.test-support';

describe('QuotaStore badge', () => {
  let data: FakeQuotaDataService;
  let store: InstanceType<typeof QuotaStore>;
  let settle: () => Promise<void>;

  beforeEach(() => {
    ({ data, store, settle } = mountQuotaStore());
  });

  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('names a warned metric, and a blocked metric wins over it', async () => {
    data.answer = summary(
      metric({ metric: 'results', warned_at: '2026-11-01T09:00:00Z' }),
      metric({ metric: 'ai_tokens', blocked_at: '2026-11-02T09:00:00Z' }),
    );
    TestBed.inject(TenantStore).setOrg('org-one');
    store.load();
    await settle();

    expect(store.badge()).toEqual({ severity: 'blocked', metric: 'ai_tokens', scope: 'tenant' });
  });

  it('names a warned project counter while the tenant counter is below its warning line', async () => {
    data.answer = summary(
      metric({
        metric: 'results',
        used: 10,
        limit: 1000,
        projects: [
          project({ project_id: 'project-one', used: 90, warned_at: '2026-11-01T09:00:00Z' }),
        ],
        projects_status: { blocked_count: 0, warned_count: 1 },
      }),
    );
    TestBed.inject(TenantStore).setOrg('org-one');
    store.load();
    await settle();

    expect(store.badge()).toEqual({ severity: 'warn', metric: 'results', scope: 'project' });
  });

  it('names a blocked project counter and prefers it over a warned tenant counter', async () => {
    data.answer = summary(
      metric({ metric: 'results', used: 900, limit: 1000, warned_at: '2026-11-01T09:00:00Z' }),
      metric({
        metric: 'ai_tokens',
        used: 10,
        limit: 100,
        projects: [
          project({
            project_id: 'project-two',
            used: 100,
            blocked_at: '2026-11-02T09:00:00Z',
            warned_at: '2026-11-01T09:00:00Z',
          }),
        ],
        projects_status: { blocked_count: 1, warned_count: 0 },
      }),
    );
    TestBed.inject(TenantStore).setOrg('org-one');
    store.load();
    await settle();

    expect(store.badge()).toEqual({ severity: 'blocked', metric: 'ai_tokens', scope: 'project' });
  });

  it('reads a warned metric as warn and never infers a threshold from an unmeasured counter', async () => {
    data.answer = summary(
      metric({ metric: 'egress_gb', used: null, limit: null, warned_at: '2026-11-01T09:00:00Z' }),
    );
    TestBed.inject(TenantStore).setOrg('org-one');
    store.load();
    await settle();
    expect(store.badge()).toBeNull();

    data.answer = summary(
      metric({ metric: 'egress_gb', used: null, limit: null, warned_at: '2026-11-01T09:00:00Z' }),
      metric({ metric: 'results', warned_at: '2026-11-01T09:00:00Z' }),
    );
    store.reload();
    await settle();

    expect(store.badge()).toEqual({ severity: 'warn', metric: 'results', scope: 'tenant' });
  });
});
