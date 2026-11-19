import type { HttpRequest } from '@angular/common/http';
import type {
  ApplyAuthoringChangesRequest,
  ApplyAuthoringChangesResponse,
  AuthoringChangeListItem,
  AuthoringChangePageResponse,
} from '@evidgate/sdk';
import initial from '../../../../../../fixtures/web-api/authoring-pending-list/input.json';
import applied from '../../../../../../fixtures/web-api/authoring-pending-apply/input.json';
import type { FixtureEntry } from './fixture-catalog-entries';
import { findFixtureEntry } from './fixture-catalog';

const ETAG = '"batch-7"';
export class AuthoringPendingFixtureScenario {
  private readonly queues = new Map<string, readonly AuthoringChangeListItem[]>();
  private readonly receipts = new Map<string, ApplyAuthoringChangesResponse>();
  entry(
    request: HttpRequest<unknown>,
    path: string,
    variantFor: (operation: string) => string,
  ): FixtureEntry | undefined {
    const match = /^\/projects\/([^/]+)\/authoring\/changes(:apply)?$/.exec(path);
    if (!match?.[1]) return undefined;
    const project = match[1];
    const rows = this.queues.get(project) ?? (initial.body as AuthoringChangePageResponse).items;
    if (request.method === 'GET' && !match[2] && variantFor('listAuthoringChanges') === 'ok') {
      const ids = new URL(request.urlWithParams, 'https://fixture.invalid').searchParams
        .getAll('ids')
        .flatMap((value) => value.split(','));
      return this.answer(
        'listAuthoringChanges',
        {
          items: ids.length
            ? rows
                .filter((row) => ids.includes(row.change_id))
                .map((row) => ({ ...row, stale: false }))
            : rows,
          next_cursor: null,
        },
        ids.length ? ETAG : undefined,
      );
    }
    if (request.method !== 'POST' || !match[2]) return undefined;
    const variant = variantFor('applyAuthoringChanges');
    if (variant !== 'ok') return findFixtureEntry('applyAuthoringChanges', variant);
    if (request.headers.get('If-Match') !== ETAG)
      return findFixtureEntry('applyAuthoringChanges', 'conflict');
    const body = (
      typeof request.body === 'string' ? JSON.parse(request.body) : request.body
    ) as ApplyAuthoringChangesRequest;
    if (!Array.isArray(body?.change_ids)) return undefined;
    const key = project + ':' + request.headers.get('Idempotency-Key');
    const replay = this.receipts.get(key);
    if (replay) return this.answer('applyAuthoringChanges', replay);
    const selected = rows.filter((row) => body.change_ids.includes(row.change_id));
    if (
      selected.length !== body.change_ids.length ||
      selected.some((row) => row.change_type === 'merge' || row.change_type === 'split')
    )
      return findFixtureEntry('applyAuthoringChanges', 'conflict');
    const receipt: ApplyAuthoringChangesResponse = {
      ...applied.body,
      applied_change_ids: selected.map((row) => row.change_id),
      versions: selected
        .filter((row) => row.change_type !== 'comment')
        .map((row) => ({
          entity_kind: 'checklist_item',
          entity_id: row.target_id,
          version_no: row.base_version + 1,
        })),
    };
    this.receipts.set(key, receipt);
    this.queues.set(
      project,
      rows.filter((row) => !body.change_ids.includes(row.change_id)),
    );
    return this.answer('applyAuthoringChanges', receipt);
  }
  private answer(operation: string, body: unknown, etag?: string): FixtureEntry {
    return {
      name: 'authoring-pending-review-state',
      document: {
        operation_id: operation,
        variant: 'ok',
        status: 200,
        headers: etag ? { ETag: etag } : {},
        latency_ms: 0,
        body,
      },
    };
  }
}
