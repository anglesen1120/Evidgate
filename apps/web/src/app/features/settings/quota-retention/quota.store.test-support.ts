// The harness the QuotaStore specs share: a mounted host (the store's effect and its `resource`
// settle inside a component's change detection), the wire builders a test describes its answers
// with, and the fake data service whose reads the test controls.

import {
  ChangeDetectionStrategy,
  Component,
  provideZonelessChangeDetection,
  signal,
  type WritableSignal,
} from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import type { UsageSummaryDto, UsageSummaryMetricDto, UsageSummaryProjectDto } from '@evidgate/sdk';

import { QuotaDataService } from '../../../core/data/quota-data.service';
import { ApiRequestError } from '../../../core/http/api-error';
import { QuotaExceededRelay } from '../../../core/http/quota-exceeded-relay';
import { SessionStore } from '../../../core/session/session.store';
import { QuotaStore } from './quota.store';

/** A mounted body: effects and `resource` settle inside a component's change detection. */
@Component({ template: '', changeDetection: ChangeDetectionStrategy.OnPush })
export class HostComponent {}

export function metric(changes: Partial<UsageSummaryMetricDto> = {}): UsageSummaryMetricDto {
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

export function summary(...metrics: UsageSummaryMetricDto[]): UsageSummaryDto {
  return { period: '2026-11', project: null, metrics, next_cursor: null };
}

/** One project's counter under a metric; below both lines unless the test says otherwise. */
export function project(changes: Partial<UsageSummaryProjectDto> = {}): UsageSummaryProjectDto {
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

/** The refusal a 5xx reaches the store as. */
export function serverFailure(): ApiRequestError {
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
export class FakeQuotaDataService {
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

  /** Fails the oldest held read. */
  fail(error: unknown): void {
    this.held.shift()?.reject(error);
  }
}

/** One mounted store, its fake data service and the two ways a spec lets a read settle. */
export interface QuotaHarness {
  readonly data: FakeQuotaDataService;
  readonly fixture: ComponentFixture<HostComponent>;
  readonly store: InstanceType<typeof QuotaStore>;
  readonly relay: QuotaExceededRelay;
  /** The codes the fake session reports; changing it re-runs or stops the read. */
  readonly permissions: WritableSignal<ReadonlySet<string>>;
  /** Runs the application tick (the store's effect lives in the root injector), then settles. */
  readonly settle: () => Promise<void>;
  /**
   * Runs one tick and drains microtasks WITHOUT waiting for stability. A read the test holds open
   * keeps Angular's pending tasks non-zero, so `whenStable()` would wait for a promise the test
   * itself only settles later: use this to inspect the in-flight state instead.
   */
  readonly advance: () => Promise<void>;
}

/** Configures the TestBed (the caller resets it) and mounts the store with `usage:read` held. */
export function mountQuotaStore(): QuotaHarness {
  const permissions = signal<ReadonlySet<string>>(new Set(['usage:read']));
  const data = new FakeQuotaDataService();
  TestBed.configureTestingModule({
    imports: [HostComponent],
    providers: [
      provideZonelessChangeDetection(),
      { provide: QuotaDataService, useValue: data },
      { provide: SessionStore, useValue: { has: (code: string) => permissions().has(code) } },
    ],
  });
  const fixture = TestBed.createComponent(HostComponent);
  const store = TestBed.inject(QuotaStore);
  const relay = TestBed.inject(QuotaExceededRelay);

  async function settle(): Promise<void> {
    for (let pass = 0; pass < 3; pass += 1) {
      TestBed.tick();
      fixture.detectChanges();
      await fixture.whenStable();
    }
  }

  async function advance(): Promise<void> {
    TestBed.tick();
    fixture.detectChanges();
    await Promise.resolve();
  }

  return { data, fixture, store, relay, permissions, settle, advance };
}
