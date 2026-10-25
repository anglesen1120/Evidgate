// 09.7.1-T3 (plan 09.7.1 §2 AC3, §5 "secret references" row, §6 ingest-token rotation states,
// §8 S3, §10 T3.1/T3.2): the two calls the Settings > Secrets tab makes — the project's
// ingest-token metadata page and the rotation that mints a successor.
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
  listIngestTokens,
  rotateIngestToken,
  type IngestTokenCreatedResponse,
  type IngestTokenListResponse,
  type ListIngestTokensData,
} from '@evidgate/sdk';

import { ApiGateway } from '../http/api-gateway';

/** The query of `GET /v1/projects/{p}/ingest-tokens` without its optionality, so `{}` is legal. */
type IngestTokenListQuery = NonNullable<ListIngestTokensData['query']>;

/** The contract's maximum page size for this route (00 §9.1: `limit` ≤ 200). */
const INGEST_TOKEN_PAGE_LIMIT = 200;

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
