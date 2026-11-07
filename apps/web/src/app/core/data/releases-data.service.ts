import { inject, Injectable } from '@angular/core';
import { getRelease, type ReleaseDetail } from '@evidgate/sdk';

import { ApiGateway } from '../http/api-gateway';

/** The release reads the run-detail surface needs; every signature is an SDK type. */
@Injectable({ providedIn: 'root' })
export class ReleasesDataService {
  private readonly gateway = inject(ApiGateway);

  /**
   * One release with its components and bindings (`GET /v1/releases/{id}`).
   *
   * The served document is current, so it is returned exactly as served: the panel narrows the
   * bindings to its run and never fills a missing member. An unknown or foreign id rejects with
   * the gateway's typed refusal, so the panel renders not-found instead of an empty release.
   */
  get(releaseId: string): Promise<ReleaseDetail> {
    return this.gateway.call((client) => getRelease({ client, path: { id: releaseId } }));
  }
}
