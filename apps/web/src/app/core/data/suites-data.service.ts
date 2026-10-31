// The suite reads' data service. It exists for one reason: `evidgate/no-http-outside-core` keeps
// every generated `@evidgate/sdk` operation inside `core/{http,data}`, and `ApiGateway.call` is the
// single place where a 2xx body becomes a value and a refusal becomes a typed `ApiRequestError`.
// A page or store that called `getSuiteRevision` itself would bypass the interceptors (XSRF,
// request id, deadline) and the one failure classification.
//
// No wire type is declared here: every signature is an SDK type, so a contract rename reaches this
// file as a compile error rather than as a silently unread member.

import { inject, Injectable } from '@angular/core';
import { getSuiteRevision, type SuiteRevisionDto } from '@evidgate/sdk';

import { ApiGateway } from '../http/api-gateway';

@Injectable({ providedIn: 'root' })
export class SuitesDataService {
  private readonly gateway = inject(ApiGateway);

  /**
   * The full pinned content of one suite revision (`GET /v1/suite-revisions/{r}`): members,
   * dependency edges and the strategy documents, for both `draft` and the immutable `published`.
   *
   * The body is returned as served. The read carries no warnings member and no case names, so
   * nothing here invents either; a refusal arrives as the gateway's thrown `ApiRequestError`.
   */
  getRevision(revisionId: string): Promise<SuiteRevisionDto> {
    return this.gateway.call((client) => getSuiteRevision({ client, path: { r: revisionId } }));
  }
}
