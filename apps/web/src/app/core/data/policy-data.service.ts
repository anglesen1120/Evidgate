// The Settings > Policy tab's calls: one revision with its body, a cursor page of the project's
// revisions, the two project-level reads (activation and head), and the append that publishes a
// body. Every signature is an SDK type and every call goes through `ApiGateway`; the answers whose
// `ETag` matters go through `callWithResponse`, because a publish quotes the head in `If-Match`.

import { inject, Injectable } from '@angular/core';
import {
  createPolicyRevision,
  getPolicyActivation,
  getPolicyHead,
  getPolicyRevision,
  listPolicyRevisions,
  type ListPolicyRevisionsData,
  type PolicyActivationStateDto,
  type PolicyRevisionDto,
  type PolicyRevisionSummaryDto,
  type PolicyRevisionV1,
} from '@evidgate/sdk';

import { ApiGateway } from '../http/api-gateway';

/** The query of `GET /v1/policies/{id}/revisions` without its optionality, so `{}` is legal. */
type PolicyRevisionListQuery = NonNullable<ListPolicyRevisionsData['query']>;

/** The contract's maximum page size for this route (00 §9.1: `limit` ≤ 200). */
const POLICY_REVISION_PAGE_LIMIT = 200;

/** An `ETag`'s weak marker, which RFC 9110 allows in front of the quoted value. */
const WEAK_ETAG_PREFIX = 'W/';

/** The quotes an `ETag` value is wrapped in; `"` is the only character this strips. */
const ETAG_QUOTES = /"/g;

/** One page of the project's revisions, as served, plus the head the answer named. */
export interface PolicyRevisionPage {
  readonly items: readonly PolicyRevisionSummaryDto[];
  readonly nextCursor: string | null;
  /** The list answer's ETag with its quotes stripped: the head revision id; null when absent. */
  readonly headRevisionId: string | null;
}

/** One publish answer: the stored revision, and whether this call appended it (`201`). */
export interface PublishedRevision {
  readonly revision: PolicyRevisionDto;
  readonly created: boolean;
}

/** The project's head revision, plus the head id its answer's `ETag` named. */
export interface PolicyHead {
  readonly revision: PolicyRevisionSummaryDto;
  /** The `ETag` with its quotes stripped; `null` when the answer carried none. */
  readonly headRevisionId: string | null;
}

/**
 * The head revision id an `ETag` names, quotes and a weak marker removed: `"<uuid>"` becomes
 * `<uuid>`, which is the form `PolicyIfMatch.TryParse` accepts inside (and without) quotes. A
 * response with no `ETag` — a project with no revision yet — answers `null`, and so does an empty
 * value: no head means "send no `If-Match`", never an empty header.
 */
function headRevisionIdOf(headers: Headers): string | null {
  const etag = headers.get('ETag');
  if (etag === null) {
    return null;
  }
  const value = etag.startsWith(WEAK_ETAG_PREFIX) ? etag.slice(WEAK_ETAG_PREFIX.length) : etag;
  const unquoted = value.replace(ETAG_QUOTES, '').trim();
  return unquoted.length === 0 ? null : unquoted;
}

@Injectable({ providedIn: 'root' })
export class PolicyDataService {
  private readonly gateway = inject(ApiGateway);

  /** The project's activation (`GET /v1/projects/{p}/policy-activation`); `404` means none. */
  getActivation(projectId: string): Promise<PolicyActivationStateDto> {
    return this.gateway.call((client) => getPolicyActivation({ client, path: { p: projectId } }));
  }

  /**
   * The project's head revision (`GET /v1/projects/{p}/policies/head`): the newest revision, with
   * the `ETag` that names it. `404` is a project with no revision at all, not a failure.
   */
  async getHead(projectId: string): Promise<PolicyHead> {
    const { data, headers } = await this.gateway.callWithResponse((client) =>
      getPolicyHead({ client, path: { p: projectId } }),
    );
    return { revision: data, headRevisionId: headRevisionIdOf(headers) };
  }

  /**
   * One policy revision with its body (`GET /v1/policies/{id}`, AC3). Another tenant's revision, or
   * another project's for a project-scoped credential, is the same `404`; the store turns the
   * refusal into its not-found state so the two are indistinguishable on screen as well.
   */
  getRevision(revisionId: string): Promise<PolicyRevisionDto> {
    return this.gateway.call((client) => getPolicyRevision({ client, path: { id: revisionId } }));
  }

  /**
   * One page of the revisions of the project `anyRevisionId` belongs to
   * (`GET /v1/policies/{id}/revisions`, AC5; keyset on `(revision_no, id)`).
   *
   * The page is returned as served — rows, `next_cursor` and the answer's `ETag` — so the store
   * keeps the rows it already renders while the next page is on its way, and knows the head the
   * next publish must quote without a second request.
   */
  async listRevisions(anyRevisionId: string, cursor?: string): Promise<PolicyRevisionPage> {
    const query: PolicyRevisionListQuery = { limit: POLICY_REVISION_PAGE_LIMIT };
    if (cursor !== undefined) {
      query.cursor = cursor;
    }
    const { data, headers } = await this.gateway.callWithResponse((client) =>
      listPolicyRevisions({ client, path: { id: anyRevisionId }, query }),
    );
    return {
      items: data.items,
      nextCursor: data.next_cursor,
      headRevisionId: headRevisionIdOf(headers),
    };
  }

  /**
   * Appends a policy revision (`POST /v1/projects/{p}/policies`, AC1/AC4/AC5; `policy:write`).
   *
   * `ifMatch` is the head revision id the last list read named, sent quoted because that is the
   * form the route's own `ETag` has; `null` sends no header at all, which the contract defines as
   * "no precondition check". A stale or malformed value is a `412` the caller surfaces verbatim —
   * never retried here, and never retried with a different header.
   *
   * The body is `unknown` on purpose: the tab's textarea is the source, the store has already
   * refused a non-object and an oversized body, and the closed `policy-revision.v1` schema is the
   * authority on the rest (S83 P4) — so the one cast below hands the server's own validator the
   * text instead of an app-side guess at its shape.
   */
  async publish(
    projectId: string,
    body: unknown,
    ifMatch: string | null,
  ): Promise<PublishedRevision> {
    // A header map rather than a conditional object: `exactOptionalPropertyTypes` refuses an
    // explicit `undefined` for the operation's optional `headers`, and an empty map is exactly the
    // "no header" case the contract defines as "no precondition check".
    const headers: Record<string, string> = ifMatch === null ? {} : { 'If-Match': `"${ifMatch}"` };
    const { data, status } = await this.gateway.callWithResponse((client) =>
      createPolicyRevision({
        client,
        path: { p: projectId },
        body: body as PolicyRevisionV1,
        headers,
      }),
    );
    return { revision: data, created: status === 201 };
  }
}
