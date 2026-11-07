import { inject, Injectable } from '@angular/core';
import {
  createApiToken,
  listApiTokens,
  revokeApiToken,
  type ApiTokenCreatedResponse,
  type ApiTokenListResponse,
  type CreateApiTokenRequest,
} from '@evidgate/sdk';
import { ApiGateway } from '../http/api-gateway';
import { intentHeaders, type Intent } from '../http/intent';

@Injectable({ providedIn: 'root' })
export class ApiTokensService {
  private readonly gateway = inject(ApiGateway);

  list(project: string, cursor?: string, signal?: AbortSignal): Promise<ApiTokenListResponse> {
    return this.gateway.call((client) =>
      listApiTokens({
        client,
        path: { p: project },
        query: { limit: 50, ...(cursor === undefined ? {} : { cursor }) },
        ...(signal === undefined ? {} : { signal }),
      }),
    );
  }

  // The one-time response is returned to the reveal owner, never retained by the data layer.
  create(
    project: string,
    body: CreateApiTokenRequest,
    intent: Intent,
  ): Promise<ApiTokenCreatedResponse> {
    return this.gateway.call((client) =>
      createApiToken({ client, path: { p: project }, body, headers: intentHeaders(intent) }),
    );
  }

  async revoke(id: string, intent: Intent): Promise<void> {
    await this.gateway.call((client) =>
      revokeApiToken({ client, path: { t: id }, headers: intentHeaders(intent) }),
    );
  }
}
