// The QuotaStore's read rules, pinned against a fake data service and the real tenant/permission
// signals (the harness both store specs share is `quota.store.test-support.ts`; the badge rules
// it reads out of the summary are `quota.store.badge.spec.ts`):
//
// - the read starts on `load()` only, once per organization, and never without `usage:read`;
// - an org switch refetches, and the previous org's counters leave the screen immediately;
// - a failure, a null answer or a revoked permission reads as `unavailable`, never as zero, and
//   the badge stays hidden;
// - a 429 `quota_exceeded` refusal asks for exactly one reload, a refusal that lands mid-read
//   asks for one trailing read when that read settles, and neither can ask for another one;
// - a reload keeps the counters already served on screen, while an org switch, an error or a
//   null answer hides them;
// - a paged answer (`next_cursor` names more projects) is kept as served: the org totals and the
//   badge come from the tenant counters, never from a sum of the page's project rows.

import type { WritableSignal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { QuotaExceededRelay } from '../../../core/http/quota-exceeded-relay';
import { TenantStore } from '../../../core/session/tenant.store';
import { QuotaStore } from './quota.store';
import {
  FakeQuotaDataService,
  metric,
  mountQuotaStore,
  project,
  serverFailure,
  summary,
} from './quota.store.test-support';

describe('QuotaStore', () => {
  let data: FakeQuotaDataService;
  let store: InstanceType<typeof QuotaStore>;
  let relay: QuotaExceededRelay;
  let permissions: WritableSignal<ReadonlySet<string>>;
  let settle: () => Promise<void>;
  let advance: () => Promise<void>;

  beforeEach(() => {
    ({ data, store, relay, permissions, settle, advance } = mountQuotaStore());
  });

  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('reads nothing until load(), then the active organization exactly once', async () => {
    TestBed.inject(TenantStore).setOrg('org-one');
    await settle();
    expect(data.calls).toEqual([]);
    expect(store.loadState()).toBe('idle');
    expect(store.usageSummary()).toBeNull();

    store.load();
    store.load();
    await settle();

    expect(data.calls).toHaveLength(1);
    expect(data.calls[0]?.orgId).toBe('org-one');
    expect(store.loadState()).toBe('ready');
    expect(store.usageSummary()).toEqual(summary());
    expect(store.badge()).toBeNull();
  });

  it('requests nothing without usage:read and reads as soon as the permission arrives', async () => {
    permissions.set(new Set());
    TestBed.inject(TenantStore).setOrg('org-one');
    store.load();
    await settle();

    expect(data.calls).toEqual([]);
    expect(store.loadState()).toBe('unavailable');
    expect(store.usageSummary()).toBeNull();

    // The positive control: only the permission changes.
    permissions.set(new Set(['usage:read']));
    await settle();

    expect(data.calls.map((call) => call.orgId)).toEqual(['org-one']);
    expect(store.loadState()).toBe('ready');
  });

  it('replaces the counters on an organization switch and drops the old org immediately', async () => {
    const tenant = TestBed.inject(TenantStore);
    tenant.setOrg('org-one');
    store.load();
    await settle();
    expect(store.usageSummary()).toEqual(summary());

    data.hold = true;
    tenant.setOrg('org-two');
    await advance();

    expect(data.calls.map((call) => call.orgId)).toEqual(['org-one', 'org-two']);
    expect(store.loadState()).toBe('loading');
    expect(store.usageSummary()).toBeNull();
    expect(store.badge()).toBeNull();

    data.release(summary(metric({ metric: 'ai_tokens', used: 1 })));
    await settle();

    expect(store.loadState()).toBe('ready');
    expect(store.usageSummary()?.metrics[0]?.metric).toBe('ai_tokens');
  });

  it('reads a null answer as unavailable and shows no badge', async () => {
    data.answer = null;
    TestBed.inject(TenantStore).setOrg('org-one');
    store.load();
    await settle();

    expect(data.calls).toHaveLength(1);
    expect(store.loadState()).toBe('unavailable');
    expect(store.usageSummary()).toBeNull();
    expect(store.badge()).toBeNull();
  });

  it('reads a failed read as unavailable, and reload() asks again', async () => {
    data.failure = serverFailure();
    TestBed.inject(TenantStore).setOrg('org-one');
    store.load();
    await settle();

    expect(store.loadState()).toBe('unavailable');
    expect(store.usageSummary()).toBeNull();
    expect(store.badge()).toBeNull();

    data.failure = null;
    store.reload();
    await settle();

    expect(data.calls).toHaveLength(2);
    expect(store.loadState()).toBe('ready');
  });

  it('drops the summary when the permission is revoked', async () => {
    TestBed.inject(TenantStore).setOrg('org-one');
    store.load();
    await settle();
    expect(store.usageSummary()).not.toBeNull();

    permissions.set(new Set());
    await settle();

    expect(store.usageSummary()).toBeNull();
    expect(store.loadState()).toBe('unavailable');
  });

  it('reloads once for a quota refusal and once more when that read lands', async () => {
    TestBed.inject(TenantStore).setOrg('org-one');
    store.load();
    await settle();
    expect(data.calls).toHaveLength(1);

    data.hold = true;
    relay.notify();
    await advance();
    // A burst of refusals during the read costs nothing extra.
    relay.notify();
    relay.notify();
    await advance();
    expect(data.calls).toHaveLength(2);

    data.release();
    await advance();
    // The refusal that arrived during the read is not dropped: one trailing read, no second one.
    expect(data.calls).toHaveLength(3);

    data.hold = false;
    data.release();
    await settle();
    expect(data.calls).toHaveLength(3);
    expect(store.loadState()).toBe('ready');
  });

  it('coalesces fifty concurrent refusals into one in-flight read and one trailing read', async () => {
    TestBed.inject(TenantStore).setOrg('org-one');
    store.load();
    await settle();
    expect(data.calls).toHaveLength(1);

    data.hold = true;
    for (let refusal = 0; refusal < 50; refusal += 1) {
      relay.notify();
    }
    await advance();
    expect(data.calls).toHaveLength(2);

    data.release();
    await advance();
    expect(data.calls).toHaveLength(3);

    data.hold = false;
    data.release();
    await settle();
    expect(data.calls).toHaveLength(3);
    expect(store.loadState()).toBe('ready');
  });

  it('still answers a refusal that landed while its read failed', async () => {
    TestBed.inject(TenantStore).setOrg('org-one');
    store.load();
    await settle();
    expect(data.calls).toHaveLength(1);

    data.hold = true;
    relay.notify();
    await advance();
    relay.notify();
    await advance();
    expect(data.calls).toHaveLength(2);

    // The read fails instead of resolving: the refusal still gets its one trailing read, and the
    // counters already served stay on screen meanwhile instead of flickering away.
    data.fail(serverFailure());
    await advance();
    expect(data.calls).toHaveLength(3);
    expect(store.loadState()).toBe('ready');
    expect(store.usageSummary()).toEqual(summary());

    data.hold = false;
    data.release(summary(metric({ metric: 'ai_tokens', warned_at: '2026-11-01T09:00:00Z' })));
    await settle();
    expect(store.loadState()).toBe('ready');
    expect(store.badge()).toEqual({ severity: 'warn', metric: 'ai_tokens', scope: 'tenant' });
  });

  it('keeps the served counters on screen during a reload and replaces them when it lands', async () => {
    const served = summary(metric({ metric: 'results', warned_at: '2026-11-01T09:00:00Z' }));
    data.answer = served;
    TestBed.inject(TenantStore).setOrg('org-one');
    store.load();
    await settle();
    expect(store.badge()).toEqual({ severity: 'warn', metric: 'results', scope: 'tenant' });

    data.hold = true;
    store.reload();
    await advance();

    // The read is in flight: the badge still names the served counter instead of flapping.
    expect(data.calls).toHaveLength(2);
    expect(store.loadState()).toBe('ready');
    expect(store.usageSummary()).toEqual(served);
    expect(store.badge()).toEqual({ severity: 'warn', metric: 'results', scope: 'tenant' });

    data.release(summary(metric({ metric: 'ai_tokens', blocked_at: '2026-11-02T09:00:00Z' })));
    await settle();

    expect(store.usageSummary()?.metrics[0]?.metric).toBe('ai_tokens');
    expect(store.badge()).toEqual({ severity: 'blocked', metric: 'ai_tokens', scope: 'tenant' });
  });

  it('hides the counters on a failed reload and never resurrects them on the next read', async () => {
    TestBed.inject(TenantStore).setOrg('org-one');
    store.load();
    await settle();
    expect(store.usageSummary()).not.toBeNull();

    data.failure = serverFailure();
    store.reload();
    await settle();
    expect(store.usageSummary()).toBeNull();
    expect(store.loadState()).toBe('unavailable');
    expect(store.badge()).toBeNull();

    // The retry stays hidden while it is in flight: the failed read cleared what was served.
    data.failure = null;
    data.hold = true;
    store.reload();
    await advance();
    expect(store.usageSummary()).toBeNull();
    expect(store.badge()).toBeNull();

    data.release(summary(metric({ metric: 'ai_tokens', warned_at: '2026-11-01T09:00:00Z' })));
    await settle();
    expect(store.badge()).toEqual({ severity: 'warn', metric: 'ai_tokens', scope: 'tenant' });
  });

  it('never answers a delayed read with the organization it was started for', async () => {
    const tenant = TestBed.inject(TenantStore);
    tenant.setOrg('org-one');
    store.load();
    await settle();
    expect(data.calls.map((call) => call.orgId)).toEqual(['org-one']);

    data.hold = true;
    relay.notify();
    await advance();
    relay.notify();
    await advance();
    expect(data.calls.map((call) => call.orgId)).toEqual(['org-one', 'org-one']);

    tenant.setOrg('org-two');
    await advance();
    expect(data.calls.map((call) => call.orgId)).toEqual(['org-one', 'org-one', 'org-two']);
    expect(store.usageSummary()).toBeNull();

    // Answering the abandoned org-one read asks for nothing more and shows nothing.
    data.release(summary(metric({ metric: 'ai_tokens', used: 1 })));
    await advance();
    expect(data.calls.map((call) => call.orgId)).toEqual(['org-one', 'org-one', 'org-two']);
    expect(store.usageSummary()).toBeNull();

    data.hold = false;
    data.release(summary(metric({ metric: 'egress_gb', used: 5 })));
    await settle();
    expect(store.usageSummary()?.metrics[0]?.metric).toBe('egress_gb');
  });

  it('keeps a paged answer as served: the badge reads the org totals, not the page rows', async () => {
    // The service pages the per-project breakdown: `next_cursor` names a further page, and every
    // page carries the same org totals. The badge must therefore read the tenant counter, never a
    // total summed from the page's project rows (which here add up below the warning line).
    data.answer = {
      ...summary(
        metric({
          metric: 'results',
          used: 900,
          limit: 1000,
          warned_at: '2026-11-01T09:00:00Z',
          projects: [
            project({ project_id: 'project-one', used: 10 }),
            project({ project_id: 'project-two', used: 20 }),
          ],
        }),
      ),
      next_cursor: 'cursor-page-two',
    };
    TestBed.inject(TenantStore).setOrg('org-one');
    store.load();
    await settle();

    // One read: the first page is the answer, and nothing follows the cursor on its own.
    expect(data.calls).toHaveLength(1);
    expect(store.loadState()).toBe('ready');
    expect(store.usageSummary()).toEqual(data.answer);
    expect(store.usageSummary()?.metrics[0]?.used).toBe(900);
    expect(store.usageSummary()?.metrics[0]?.projects.map((row) => row.project_id)).toEqual([
      'project-one',
      'project-two',
    ]);
    expect(store.badge()).toEqual({ severity: 'warn', metric: 'results', scope: 'tenant' });
  });

  it('clears the summary when the tenant is gone', async () => {
    const tenant = TestBed.inject(TenantStore);
    tenant.setOrg('org-one');
    store.load();
    await settle();
    expect(store.usageSummary()).not.toBeNull();

    tenant.clear();
    await settle();

    expect(store.usageSummary()).toBeNull();
    expect(store.loadState()).toBe('idle');
  });
});
