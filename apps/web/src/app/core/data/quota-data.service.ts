// The org's usage summary: the read behind the Quota & retention tab and the app-shell badge.
//
// It lives in `core/data` with the other data services because `apps/web/lint` owns the HTTP
// boundary there: a store never imports an `@evidgate/sdk` operation itself. The pattern is
// `audit-data.service.ts` — the generated operation is called with the app's one client,
// `ApiGateway.call` decides success versus typed failure, and no wire type is declared here.

import { inject, Injectable } from '@angular/core';
import { getUsageSummary, type UsageSummaryDto } from '@evidgate/sdk';

import { ApiGateway } from '../http/api-gateway';

@Injectable({ providedIn: 'root' })
export class QuotaDataService {
  private readonly gateway = inject(ApiGateway);

  /**
   * The current period's counters for `orgId`, as served. `signal` cancels the read when the org
   * changes while it is in flight.
   *
   * The refusal is not reported as a quota signal: this read is what the signal asks for, so
   * reporting its own failure would ask for the next read without end.
   */
  summary(orgId: string, signal?: AbortSignal): Promise<UsageSummaryDto> {
    return this.gateway.call(
      // `signal` is spread only when present: the SDK option does not accept an explicit
      // `undefined` under `exactOptionalPropertyTypes`.
      (client) =>
        getUsageSummary({
          client,
          path: { o: orgId },
          ...(signal === undefined ? {} : { signal }),
        }),
      { reportQuota: false },
    );
  }
}
