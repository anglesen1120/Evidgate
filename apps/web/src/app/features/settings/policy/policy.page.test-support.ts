import { ComponentHarness } from '@angular/cdk/testing';
import { signal } from '@angular/core';
import { vi } from 'vitest';
import type {
  PolicyActivationStateDto,
  PolicyRevisionDto,
  PolicyRevisionSummaryDto,
} from '@evidgate/sdk';
import type { ApiError } from '../../../core/http/api-error';
import type { PolicyReadView } from './policy-reads';
import type { PublishResult } from './policy.store';

export const revision: PolicyRevisionDto = {
  id: '182003de-f396-481b-8578-d18baf086204',
  project_id: 'project',
  revision_no: 2,
  hash: 'abcdef0123456789',
  state: 'active',
  body: { flaky_budget: 1 },
  created_by: 'actor',
  created_at: '2026-10-26T00:00:00Z',
  version: 1,
};

export const activation: PolicyActivationStateDto = {
  project_id: 'project',
  policy_revision_id: revision.id,
  hash: revision.hash,
  mode: 'advisory',
  activated_by: 'actor',
  activated_at: '2026-10-26T00:00:00Z',
};

const head: PolicyRevisionSummaryDto = revision;

export class PolicyOverviewHarness extends ComponentHarness {
  static hostSelector = 'app-policy-page';
  readonly mode = this.locatorForOptional('[data-overview-mode] dd');
  readonly head = this.locatorForOptional('[data-overview-head] dd');
  readonly activeNumber = this.locatorForOptional('[data-overview-active] app-number-value');
  readonly more = this.locatorFor('[data-page="next"]');
  readonly viewRevision = this.locatorFor('[data-policy-view]');
  readonly viewDialog = this.documentRootLocatorFactory().locatorFor('[data-policy-view-dialog]');
  readonly optionalViewDialog = this.documentRootLocatorFactory().locatorForOptional(
    '[data-policy-view-dialog]',
  );
  readonly comparison = this.locatorFor('#policy-diff-target');
}

export class FakePolicyStore {
  readonly phase = signal<'idle' | 'ready' | 'loading' | 'not_found' | 'error'>('ready');
  readonly projectId = signal<string | null>('project');
  readonly openedRevision = signal<PolicyRevisionSummaryDto | null>(null);
  readonly revisions = signal<readonly PolicyRevisionSummaryDto[]>([revision]);
  readonly headRevisionId = signal<string | null>(revision.id);
  readonly mode = signal<string | null>(null);
  readonly hasMore = signal(false);
  readonly page = signal(1);
  readonly pageSize = signal(20);
  readonly paging = signal(false);
  readonly publishing = signal(false);
  readonly loadError = signal<ApiError | null>(null);
  readonly reads = {
    activation: signal<PolicyReadView<PolicyActivationStateDto>>({
      phase: 'ready',
      value: activation,
      error: null,
    }),
    head: signal<PolicyReadView<PolicyRevisionSummaryDto>>({
      phase: 'ready',
      value: head,
      error: null,
    }),
    busy: signal(false),
    waiting: signal(false),
    retry: vi.fn(),
  };
  readonly open = vi.fn(() => Promise.resolve());
  readonly loadBody = vi.fn(() => Promise.resolve(revision));
  readonly publish = vi.fn<(body: string) => Promise<PublishResult>>(() =>
    Promise.resolve({ kind: 'created', revision }),
  );
  readonly reload = vi.fn(() => Promise.resolve());
  readonly loadMore = vi.fn(() => Promise.resolve());
  readonly previousPage = vi.fn(() => Promise.resolve());
  readonly setPageSize = vi.fn(() => Promise.resolve());
  readonly retryPage = vi.fn(() => Promise.resolve());
  readonly reset = vi.fn();
}
