// 09.7.1-T6 (plan 09.7.1 §5 audit row, §8 input-limits row, §10 T6.1/T6.2; 02.7.1 §5): the org's
// audit page the Settings audit tab renders and the CSV export walks, one cursor page at a time.
//
// It lives in `core/data` with the rest of the data services because `apps/web/lint` owns the
// HTTP boundary there: a page, store or component never imports an `@evidgate/sdk` operation (the
// `no-http-outside-core` rule), it calls one of these instead. The pattern is
// `members-data.service.ts`: the generated SDK operation is called with the app's one client,
// `ApiGateway.call` decides success versus typed failure, and no wire type is declared here —
// every signature is an SDK type.
//
// Only the list is exposed. The contract also declares `exportAudit`, which streams the server's
// own JSONL/CSV, but AC7 fixes a bounded client-side export (at most 10,000 rows over repeated
// cursors) and the server audits every export call, so the screen reads pages through this one
// operation and renders the CSV itself (`features/settings/audit/audit-csv.ts`).

import { inject, Injectable } from '@angular/core';
import { listAudit, type AuditListResponse, type ListAuditData } from '@evidgate/sdk';

import { ApiGateway } from '../http/api-gateway';

/**
 * The filter and page-size half of `listAudit`'s query. `cursor` is not in it: the cursor is a
 * parameter of {@link AuditDataService.list}, because the caller owns the loop (a page at a time
 * on screen, a bounded walk for the export). Every member is optional, and an empty string means
 * "no filter" — the shape the audit filter set holds for a filter the user left alone.
 */
export type AuditListQuery = Omit<NonNullable<ListAuditData['query']>, 'cursor'>;

@Injectable({ providedIn: 'root' })
export class AuditDataService {
  private readonly gateway = inject(ApiGateway);

  /**
   * One page of the org's audit log (`GET /v1/orgs/{o}/audit`, 02.7.1, newest first). The page is
   * returned as served, so the caller owns the cursor loop — the viewer keeps the rows it already
   * renders while the next page is on its way, and a broken cursor costs the page it was
   * following rather than the list on screen.
   */
  list(orgId: string, query: AuditListQuery = {}, cursor?: string): Promise<AuditListResponse> {
    return this.gateway.call((client) =>
      listAudit({ client, path: { o: orgId }, query: sentQuery(query, cursor) }),
    );
  }
}

/**
 * The query the request actually carries: an absent member, and an empty string on one of the
 * free-text or date members, is dropped instead of being sent. The SDK's serializer skips
 * `undefined` and `null` but sends an empty string as `action=`, which the server reads as a
 * filter matching nothing — and the filter set holds `''` for every filter the user did not
 * narrow. (`actor_type` needs no such rule: an empty string is not one of its members.)
 */
function sentQuery(
  query: AuditListQuery,
  cursor: string | undefined,
): NonNullable<ListAuditData['query']> {
  const { actor_type: actorType, action, object_type: objectType, from, to, limit } = query;
  const sent: NonNullable<ListAuditData['query']> = {};
  if (actorType !== undefined) {
    sent.actor_type = actorType;
  }
  if (action !== undefined && action !== '') {
    sent.action = action;
  }
  if (objectType !== undefined && objectType !== '') {
    sent.object_type = objectType;
  }
  if (from !== undefined && from !== '') {
    sent.from = from;
  }
  if (to !== undefined && to !== '') {
    sent.to = to;
  }
  if (limit !== undefined) {
    sent.limit = limit;
  }
  if (cursor !== undefined && cursor !== '') {
    sent.cursor = cursor;
  }
  return sent;
}
