import { inject, Injectable } from '@angular/core';
import {
  applyAuthoringChanges,
  discardAuthoringChange,
  getAuthoringChange,
  listAuthoringChanges,
  listChecklistItems,
  queueAuthoringChange,
} from '@evidgate/sdk';
import type { ListChecklistItemsData, QueueAuthoringChangeData } from '@evidgate/sdk';
import { ApiGateway } from '../http/api-gateway';

@Injectable({ providedIn: 'root' })
export class AuthoringPendingDataService {
  private readonly gateway = inject(ApiGateway);

  listPending(project: string, signal?: AbortSignal, cursor?: string) {
    return this.gateway.call((client) =>
      listAuthoringChanges({
        client,
        path: { p: project },
        query: { state: 'pending', limit: 200, ...(cursor ? { cursor } : {}) },
        ...(signal ? { signal } : {}),
      }),
    );
  }

  async selection(project: string, ids: readonly string[], signal?: AbortSignal) {
    const result = await this.gateway.callWithResponse((client) =>
      listAuthoringChanges({
        client,
        path: { p: project },
        query: { ids: [...ids] },
        ...(signal ? { signal } : {}),
      }),
    );
    const batchEtag = result.headers.get('ETag');
    if (!batchEtag) throw new Error('Selection response is missing its batch ETag');
    return { items: result.data.items, batchEtag };
  }

  detail(project: string, id: string, signal?: AbortSignal) {
    return this.gateway.call((client) =>
      getAuthoringChange({
        client,
        path: { p: project, c: id },
        ...(signal ? { signal } : {}),
      }),
    );
  }

  queue(
    project: string,
    body: QueueAuthoringChangeData['body'],
    key: string,
    signal?: AbortSignal,
  ) {
    return this.gateway.call((client) =>
      queueAuthoringChange({
        client,
        path: { p: project },
        body,
        headers: { 'Idempotency-Key': key },
        ...(signal ? { signal } : {}),
      }),
    );
  }

  discard(project: string, id: string, key: string, signal?: AbortSignal) {
    return this.gateway.call((client) =>
      discardAuthoringChange({
        client,
        path: { p: project, c: id },
        headers: { 'Idempotency-Key': key },
        ...(signal ? { signal } : {}),
      }),
    );
  }

  apply(project: string, etag: string, ids: readonly string[], key: string, signal?: AbortSignal) {
    return this.gateway.call((client) =>
      applyAuthoringChanges({
        client,
        path: { p: project },
        body: { change_ids: [...ids] },
        headers: { 'If-Match': etag, 'Idempotency-Key': key },
        ...(signal ? { signal } : {}),
      }),
    );
  }

  checklistItems(
    project: string,
    signal?: AbortSignal,
    cursor?: string,
    query?: ListChecklistItemsData['query'],
  ) {
    return this.gateway.call((client) =>
      listChecklistItems({
        client,
        path: { p: project },
        query: { limit: 200, ...query, ...(cursor ? { cursor } : {}) },
        ...(signal ? { signal } : {}),
      }),
    );
  }
}
