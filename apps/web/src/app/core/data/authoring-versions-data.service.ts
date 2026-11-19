import { inject, Injectable } from '@angular/core';
import { listAuthoringVersions } from '@evidgate/sdk';
import { ApiGateway } from '../http/api-gateway';

@Injectable({ providedIn: 'root' })
export class AuthoringVersionsDataService {
  private readonly gateway = inject(ApiGateway);

  list(project: string, signal?: AbortSignal, cursor?: string) {
    return this.gateway.call((client) =>
      listAuthoringVersions({
        client,
        path: { p: project },
        query: { source: 'import', limit: 50, ...(cursor ? { cursor } : {}) },
        ...(signal ? { signal } : {}),
      }),
    );
  }
}
