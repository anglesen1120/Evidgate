import { inject, Injectable } from '@angular/core';
import type {
  AuthoringTreeResponse,
  ChecklistItemListResponse,
  ManualCaseListResponse,
} from '@evidgate/sdk';

import { ApiRequestError, type ApiError } from '../../core/http/api-error';
import {
  AuthoringReadsService,
  type ChecklistItemQuery,
  type ManualCaseQuery,
} from '../../core/data/authoring-reads.service';

/** One tree read: the roots, one parent's children, or one addressed node with its path. */
export interface AuthoringTreeRead {
  /** The parent whose live children page is wanted. */
  readonly parentId?: string;
  /** The node whose ancestors and first children page are wanted; adds `path` to the answer. */
  readonly nodeId?: string;
  /** The opaque `next_cursor` of the previous page; absent on the first read. */
  readonly cursor?: string;
  /** Rows per page; the data layer's own page size when absent. */
  readonly limit?: number;
}

/** The page size a tree read asks for when the caller names none. */
const AUTHORING_PAGE_LIMIT = 50;

/** A read without a project id is a caller bug, not a request the API could answer. */
const PROJECT_REQUIRED: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: 'project_required',
  message: 'The authoring read needs a project id.',
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};

/**
 * A page that hands back the cursor it was asked for cannot advance a walk: retrying it would
 * loop, so it is refused typed instead of shown as a shorter list.
 */
const CURSOR_REPEATED: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: 'cursor_repeated',
  message: 'The authoring page returned the cursor it was asked for.',
  requestId: null,
  retryable: true,
  details: [],
  retryAfterSeconds: null,
};

/**
 * The authoring reads the tree store consumes: the project guard, the tree read's projection and
 * the keyset contract live here, while the SDK call itself stays in `core`. A server refusal
 * arrives already typed from `ApiGateway`; nothing here re-reads a status or a code.
 */
@Injectable({ providedIn: 'root' })
export class AuthoringDataService {
  private readonly reads = inject(AuthoringReadsService);

  listTree(
    projectId: string,
    read: AuthoringTreeRead,
    signal: AbortSignal,
  ): Promise<AuthoringTreeResponse> {
    return this.page(projectId, read.cursor, (id) =>
      this.reads.listTree(
        id,
        {
          ...(read.parentId === undefined ? {} : { parent_id: read.parentId }),
          ...(read.nodeId === undefined ? {} : { node_id: read.nodeId, include: 'path,counts' }),
          ...(read.cursor === undefined ? {} : { cursor: read.cursor }),
          limit: read.limit ?? AUTHORING_PAGE_LIMIT,
        },
        signal,
      ),
    );
  }

  listChecklistItems(
    projectId: string,
    query: ChecklistItemQuery,
    signal: AbortSignal,
  ): Promise<ChecklistItemListResponse> {
    return this.page(projectId, query.cursor, (id) =>
      this.reads.listChecklistItems(id, query, signal),
    );
  }

  listManualCases(
    projectId: string,
    query: ManualCaseQuery,
    signal: AbortSignal,
  ): Promise<ManualCaseListResponse> {
    return this.page(projectId, query.cursor, (id) =>
      this.reads.listManualCases(id, query, signal),
    );
  }

  /**
   * Runs one page read under the two rules every authoring read shares: no request without a
   * project id, and no page that repeats the cursor it was asked with.
   */
  private async page<TPage extends { next_cursor: string | null }>(
    projectId: string,
    cursor: string | undefined,
    read: (projectId: string) => Promise<TPage>,
  ): Promise<TPage> {
    const id = projectId.trim();
    if (id === '') throw new ApiRequestError(PROJECT_REQUIRED);
    const page = await read(id);
    if (cursor !== undefined && page.next_cursor === cursor) {
      throw new ApiRequestError(CURSOR_REPEATED);
    }
    return page;
  }
}
