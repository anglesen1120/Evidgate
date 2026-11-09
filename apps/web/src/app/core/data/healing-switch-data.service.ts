// The healing kill switch: the read behind the availability page and the tenant/project write.
//
// It lives in `core/data` because `apps/web/lint` owns the HTTP boundary there: the generated
// operation is called with the app's one client and `ApiGateway.call` decides success versus a
// typed failure, so no wire type is declared here.
import { inject, Injectable } from '@angular/core';
import {
  getHealingSwitch,
  setHealingSwitch,
  type HealingSwitchResolutionView,
  type HealingSwitchSetV1,
  type HealingSwitchView,
} from '@evidgate/sdk';

import { ApiGateway } from '../http/api-gateway';
import { intentHeaders, type Intent } from '../http/intent';

@Injectable({ providedIn: 'root' })
export class HealingSwitchDataService {
  private readonly gateway = inject(ApiGateway);

  /** The three stored scopes, the mode in force and the dependency observations for `project`. */
  read(project: string, signal?: AbortSignal): Promise<HealingSwitchResolutionView> {
    return this.gateway.call((client) =>
      getHealingSwitch({
        client,
        query: { project_id: project },
        ...(signal === undefined ? {} : { signal }),
      }),
    );
  }

  /**
   * Sets one scope's switch and answers the stored row. `intent` supplies the mandatory
   * `Idempotency-Key`; the XSRF header is added by the client's interceptor.
   */
  set(body: HealingSwitchSetV1, intent: Intent): Promise<HealingSwitchView> {
    return this.gateway.call((client) =>
      setHealingSwitch({ client, body, headers: intentHeaders(intent) }),
    );
  }
}
