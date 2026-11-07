// A project's retention windows: the read behind the settings Retention panel and its upsert.
//
// It lives in `core/data` with the other data services because `apps/web/lint` owns the HTTP
// boundary there: a store never imports an `@evidgate/sdk` operation itself. The pattern is
// `quota-data.service.ts` — the generated operation is called with the app's one client,
// `ApiGateway.call` decides success versus typed failure, and no wire type is declared here.
import { inject, Injectable } from '@angular/core';
import { getRetentionPolicy, setRetentionPolicy, type RetentionPolicy } from '@evidgate/sdk';

import { ApiGateway } from '../http/api-gateway';

@Injectable({ providedIn: 'root' })
export class RetentionDataService {
  private readonly gateway = inject(ApiGateway);

  /** The stored windows for `project`, or the platform defaults when it has no row. */
  read(project: string, signal?: AbortSignal): Promise<RetentionPolicy> {
    return this.gateway.call((client) =>
      getRetentionPolicy({
        client,
        path: { p: project },
        ...(signal === undefined ? {} : { signal }),
      }),
    );
  }

  /** Upserts the windows and answers what the server stored. */
  save(project: string, body: RetentionPolicy): Promise<RetentionPolicy> {
    return this.gateway.call((client) =>
      setRetentionPolicy({ client, path: { p: project }, body }),
    );
  }
}
