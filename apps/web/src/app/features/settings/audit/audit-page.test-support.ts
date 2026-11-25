import { vi } from 'vitest';
import { ComponentHarness } from '@angular/cdk/testing';
import type { ComponentFixture } from '@angular/core/testing';
import type { AuditPage } from './audit.page';
import type { AuditEntryDto, AuditListResponse } from '@evidgate/sdk';
import type { AuditListQuery } from '../../../core/data/audit-data.service';
import type { ApiError } from '../../../core/http/api-error';

export class AuditSummaryHarness extends ComponentHarness {
  static hostSelector = 'app-audit-page';
  readonly values = this.locatorForAll('[data-audit-summary] [data-audit-value]');
  readonly next = this.locatorFor('[data-page="next"]');
  readonly previous = this.locatorFor('[data-page="previous"]');
  readonly retry = this.locatorFor('[data-audit-paging-error] button');
}

export async function openOptions(fixture: ComponentFixture<AuditPage>) {
  const toggle = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
    'button[aria-controls="audit-filter-panel"]',
  )!;
  if (toggle.getAttribute('aria-expanded') !== 'true') toggle.click();
  await fixture.whenStable();
}

export function entry(id: string, changes: Partial<AuditEntryDto> = {}): AuditEntryDto {
  return {
    id,
    seq: 1,
    actor_type: 'user',
    actor_id: 'member1',
    action: 'project.updated',
    prev_hash: 'previous',
    entry_hash: 'entry',
    created_at: '2026-10-25T00:00:00Z',
    ...changes,
  };
}
export const refused: ApiError = {
  kind: 'unavailable',
  status: 503,
  code: 'audit_unavailable',
  message: 'Server refuses this audit request. <b>Keep verbatim.</b>',
  requestId: 'request1',
  retryable: true,
  retryAfterSeconds: null,
  details: [],
};
/**
 * `Promise.withResolvers` is ES2024 and this project compiles against ES2022, so this spec carries
 * its own equivalent — the hand-rolled resolver that
 * `features/onboarding/steps/create-project.page.spec.ts` builds inline, lifted into one helper
 * because two cases here need it.
 */
export function deferred<T>(): {
  readonly promise: Promise<T>;
  readonly resolve: (value: T) => void;
} {
  let resolve: (value: T) => void = () => undefined;
  const promise = new Promise<T>((settle) => {
    resolve = settle;
  });
  return { promise, resolve };
}
export class FakeAuditDataService {
  readonly list = vi.fn<
    (org: string, query: AuditListQuery, cursor?: string) => Promise<AuditListResponse>
  >(() =>
    Promise.resolve({
      items: [
        entry('ordinary'),
        entry('operator', {
          actor_type: 'platform_operator',
          metadata: { support_grant_id: 'grant-42' },
        }),
      ],
      next_cursor: null,
    }),
  );
}
