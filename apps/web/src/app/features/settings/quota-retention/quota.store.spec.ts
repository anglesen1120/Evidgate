// The QuotaStore's rules, pinned against a fake data service and the real tenant/permission
// signals:
//
// - the read starts on `load()` only, once per organization, and never without `usage:read`;
// - an org switch refetches, and the previous org's counters leave the screen immediately;
// - a failure, a null answer or a revoked permission reads as `unavailable`, never as zero, and
//   the badge stays hidden;
// - a 429 `quota_exceeded` refusal asks for exactly one reload, and the reload cannot ask for
//   another one;
// - the badge names a blocked metric before a warned one and never infers a threshold from a
//   counter with no measurement.

import {
  ChangeDetectionStrategy,
  Component,
  provideZonelessChangeDetection,
  signal,
} from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import type { UsageSummaryDto, UsageSummaryMetricDto } from '@evidgate/sdk';

import { QuotaDataService } from '../../../core/data/quota-data.service';
import { ApiRequestError } from '../../../core/http/api-error';
import { QuotaExceededRelay } from '../../../core/http/quota-exceeded-relay';
import { SessionStore } from '../../../core/session/session.store';
import { TenantStore } from '../../../core/session/tenant.store';
import { QuotaStore } from './quota.store';

/** A mounted body: effects and `resource` settle inside a component's change detection. */
@Component({ template: '', changeDetection: ChangeDetectionStrategy.OnPush })
class HostComponent {}

function metric(changes: Partial<UsageSummaryMetricDto> = {}): UsageSummaryMetricDto {
  return {
    metric: 'results',
    scope: 'tenant',
    used: 120,
    limit: 1000,
    warned_at: null,
    blocked_at: null,
    retry_after_seconds: null,
    unavailable_reason: null,
    projects: [],
    ...changes,
  };
}

function summary(...metrics: UsageSummaryMetricDto[]): UsageSummaryDto {
  return { period: '2026-11', project: null, metrics };
}

/** The refusal a 5xx reaches the store as. */
function serverFailure(): ApiRequestError {
  return new ApiRequestError({
    kind: 'unexpected',
    status: 500,
    code: 'unexpected_status',
    message: 'The API answered 500.',
    requestId: 'req-quota-1',
    retryable: false,
    details: [],
    retryAfterSeconds: null,
  });
}

/** The summary reads the store makes, with the org each one named. */
class FakeQuotaDataService {
  readonly calls: Array<{ readonly orgId: string; readonly signal: AbortSignal | undefined }> = [];
  /** Resolves every call with this value; `null` is the "no summary" answer the badge must hide. */
  answer: UsageSummaryDto | null = summary();
  /** Rejects every call with this failure when set. */
  failure: Error | null = null;
  /** Holds every call open until the test settles it. */
  readonly held: Array<{
    readonly resolve: (value: UsageSummaryDto | null) => void;
    readonly reject: (error: unknown) => void;
  }> = [];
  hold = false;

  summary(orgId: string, signal?: AbortSignal): Promise<UsageSummaryDto | null> {
    this.calls.push({ orgId, signal });
    if (this.hold) {
      return new Promise<UsageSummaryDto | null>((resolve, reject) => {
        this.held.push({ resolve, reject });
      });
    }
    if (this.failure !== null) {
      return Promise.reject(this.failure);
    }
    return Promise.resolve(this.answer);
  }

  /** Answers the oldest held read. */
  release(value: UsageSummaryDto | null = this.answer): void {
    this.held.shift()?.resolve(value);
  }
}

describe('QuotaStore', () => {
  const permissions = signal<ReadonlySet<string>>(new Set(['usage:read']));
  let data: FakeQuotaDataService;
  let fixture: ComponentFixture<HostComponent>;
  let store: InstanceType<typeof QuotaStore>;
  let relay: QuotaExceededRelay;

  beforeEach(() => {
    permissions.set(new Set(['usage:read']));
    data = new FakeQuotaDataService();
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: QuotaDataService, useValue: data },
        { provide: SessionStore, useValue: { has: (code: string) => permissions().has(code) } },
      ],
    });
    fixture = TestBed.createComponent(HostComponent);
    store = TestBed.inject(QuotaStore);
    relay = TestBed.inject(QuotaExceededRelay);
  });

  afterEach(() => {
    TestBed.resetTestingModule();
  });

  /** Runs the application tick (the store's effect lives in the root injector), then settles. */
  async function settle(): Promise<void> {
    for (let pass = 0; pass < 3; pass += 1) {
      TestBed.tick();
      fixture.detectChanges();
      await fixture.whenStable();
    }
  }

  /**
   * Runs one tick and drains microtasks WITHOUT waiting for stability. A read the test holds open
   * keeps Angular's pending tasks non-zero, so `whenStable()` would wait for a promise the test
   * itself only settles later: use this to inspect the in-flight state instead.
   */
  async function advance(): Promise<void> {
    TestBed.tick();
    fixture.detectChanges();
    await Promise.resolve();
  }

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

  it('reloads exactly once for a quota refusal, and the reload never reloads itself', async () => {
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
    await settle();
    expect(data.calls).toHaveLength(2);
    expect(store.loadState()).toBe('ready');
  });

  it('names a warned metric, and a blocked metric wins over it', async () => {
    data.answer = summary(
      metric({ metric: 'results', warned_at: '2026-11-01T09:00:00Z' }),
      metric({ metric: 'ai_tokens', blocked_at: '2026-11-02T09:00:00Z' }),
    );
    TestBed.inject(TenantStore).setOrg('org-one');
    store.load();
    await settle();

    expect(store.badge()).toEqual({ severity: 'blocked', metric: 'ai_tokens' });
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

    expect(store.badge()).toEqual({ severity: 'warn', metric: 'results' });
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
