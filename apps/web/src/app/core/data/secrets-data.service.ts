// 09.7.1-T3 (plan 09.7.1 §2 AC3, §5 "secret references" row, §6 ingest-token rotation states,
// §8 S3, §10 T3.1/T3.2): the three calls the Settings > Secrets tab makes — the project's
// ingest-token metadata page, the rotation that mints a successor, and (09.7.1-F15, plan §4
// `.../settings/secrets/` row — `SecretsStore` holds `ingestTokens` and `ciBindings`, metadata
// rows only — and §2 AC3) the project's CI-binding metadata page, which is the read-only
// counterpart of the same tab: a binding is listed, never created, patched or disabled from here.
//
// It lives in `core/data` for the reason every data service does: `apps/web/lint` owns the HTTP
// boundary there (`no-http-outside-core`), so a page, store or component calls this instead of
// importing an `@evidgate/sdk` operation. The pattern is `members-data.service.ts`: the generated
// operation is called with the app's one client, `ApiGateway.call` decides success versus typed
// failure, and no wire type is declared here — every signature is an SDK type
// (`no-hand-written-wire-dto`).
//
// Two contract facts shape the signatures, exactly as they do in `OnboardingDataService`:
//
// 1. The list is ONE page as served (`next_cursor` included), so the caller owns the cursor loop
//    and keeps the rows it already renders while the next page is on its way (00 §9.1, W7 "never
//    load all"). `limit: 200` is the contract's maximum page size for this route.
// 2. The rotate route takes no caller-owned `Idempotency-Key` (contracts/openapi.yaml: a response
//    carrying a one-time secret is never replayed), so there is no `Intent` parameter to accept —
//    the app-wide interceptor still stamps a fresh key per request, which by construction is not a
//    replayable intent. Guarding one in-flight submit — so a double click cannot mint two
//    successors — is the store's job, and a `409` (`invalid_transition`, `token_revoked`) reaches
//    the caller as the typed failure.
//
// The rotate answer's `secret` is a plaintext value. It is a return value on its way to the
// one-time reveal slot and is never put in a signal by this layer or by its caller's store.

import { inject, Injectable } from '@angular/core';
import {
  listCiBindings,
  listIngestTokens,
  rotateIngestToken,
  type CiBindingListResponse,
  type IngestTokenCreatedResponse,
  type IngestTokenListResponse,
  type ListCiBindingsData,
  type ListIngestTokensData,
} from '@evidgate/sdk';

import { ApiGateway } from '../http/api-gateway';

/** The query of `GET /v1/projects/{p}/ingest-tokens` without its optionality, so `{}` is legal. */
type IngestTokenListQuery = NonNullable<ListIngestTokensData['query']>;

/** The query of `GET /v1/projects/{p}/ci-bindings`, same shape: `{}` is a legal query. */
type CiBindingListQuery = NonNullable<ListCiBindingsData['query']>;

/** The contract's maximum page size for this route (00 §9.1: `limit` ≤ 200). */
const INGEST_TOKEN_PAGE_LIMIT = 200;

/** The same maximum for the CI-binding route: one page, never a dump (00 §9.1, W7). */
const CI_BINDING_PAGE_LIMIT = 200;

@Injectable({ providedIn: 'root' })
export class SecretsDataService {
  private readonly gateway = inject(ApiGateway);

  /**
   * One page of the project's ingest-token metadata
   * (`GET /v1/projects/{p}/ingest-tokens`, 00 §9.1 cursor page). Metadata only: the secret and its
   * hash are never listed, which is what lets the tab show a token without ever being able to show
   * its value again (AC3).
   *
   * The page is returned as served, so the store keeps the rows it already renders while the next
   * page is on its way and a broken cursor costs the page it was following rather than the list on
   * screen.
   */
  listIngestTokens(projectId: string, cursor?: string): Promise<IngestTokenListResponse> {
    const query: IngestTokenListQuery = { limit: INGEST_TOKEN_PAGE_LIMIT };
    if (cursor !== undefined) {
      query.cursor = cursor;
    }
    return this.gateway.call((client) =>
      listIngestTokens({ client, path: { p: projectId }, query }),
    );
  }

  /**
   * One page of the project's CI-binding metadata (`GET /v1/projects/{p}/ci-bindings`,
   * 09.7.1-F15; `project:read`, 00 §9.1 cursor page). A `CiBindingView` is metadata by contract:
   * the repository, its allowlists, the verification `status` and the timestamps — the challenge
   * (`binding_challenge`) exists only in the create answer and is never listed, which is why this
   * read can show what a project's CI is bound to without being able to replay the secret.
   *
   * The view is list-only: the contract offers no `GET /ci-bindings/{b}`, and the only writes are
   * `PATCH`/`DELETE`, so nothing on this route can create, update or disable a binding — the tab
   * is a read-only view of what exists.
   *
   * The page is returned as served, so the caller keeps the rows it already renders while the next
   * page is on its way and owns the cursor loop (00 §9.1, W7 "never load all").
   */
  listCiBindings(projectId: string, cursor?: string): Promise<CiBindingListResponse> {
    const query: CiBindingListQuery = { limit: CI_BINDING_PAGE_LIMIT };
    if (cursor !== undefined) {
      query.cursor = cursor;
    }
    return this.gateway.call((client) => listCiBindings({ client, path: { p: projectId }, query }));
  }

  /**
   * Mint a successor for one ingest token (`POST /v1/ingest-tokens/{t}/rotate`, step AC3's rotate
   * action; `ingest_token:write`). The old token keeps verifying until the answer's
   * `overlap_ends_at`, and the successor's plaintext `secret` is returned exactly once.
   *
   * The route takes no `Idempotency-Key` — the answer carries a one-time secret and is never
   * replayed — so no `Intent` is accepted: the caller guards one in-flight submit instead, which
   * is what stops a double click from minting two successors.
   */
  rotateIngestToken(tokenId: string): Promise<IngestTokenCreatedResponse> {
    return this.gateway.call((client) => rotateIngestToken({ client, path: { t: tokenId } }));
  }
}
