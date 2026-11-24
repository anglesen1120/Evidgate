import { inject, Injectable } from '@angular/core';
import { listAuthoringVersions } from '@evidgate/sdk';
import { ApiGateway } from '../http/api-gateway';

@Injectable({ providedIn: 'root' })
export class AuthoringVersionsDataService {
  private readonly gateway = inject(ApiGateway);

  list(project: string, signal?: AbortSignal, cursor?: string, limit = 20) {
    return this.gateway.call((client) =>
      listAuthoringVersions({
        client,
        path: { p: project },
        query: { source: 'import', limit, ...(cursor ? { cursor } : {}) },
        ...(signal ? { signal } : {}),
      }),
    );
  }
}
