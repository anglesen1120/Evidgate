import { inject, Injectable } from '@angular/core';
import {
  getProjectAiSettings,
  saveProjectAiSettings,
  listProjectAiSettingsVersions,
  getProjectAiSettingsVersion,
  listProjectAiSettingsTemplates,
  type AiSettingsView,
  type SaveAiSettingsRequest,
  type ListProjectAiSettingsVersionsData,
} from '@evidgate/sdk';
import { ApiGateway } from '../http/api-gateway';

export interface AiSettingsHead {
  readonly view: AiSettingsView;
  readonly etag: string;
}
@Injectable({ providedIn: 'root' })
export class AiSettingsDataService {
  private readonly gateway = inject(ApiGateway);
  async read(project: string, signal?: AbortSignal): Promise<AiSettingsHead> {
    const response = await this.gateway.callWithResponse((client) =>
      getProjectAiSettings({
        client,
        path: { p: project },
        ...(signal === undefined ? {} : { signal }),
      }),
    );
    return { view: response.data, etag: response.headers.get('ETag') ?? '' };
  }
  async save(
    project: string,
    body: SaveAiSettingsRequest,
    etag: string,
    key: string,
  ): Promise<AiSettingsHead & { appended: boolean }> {
    const response = await this.gateway.callWithResponse((client) =>
      saveProjectAiSettings({
        client,
        path: { p: project },
        body,
        headers: { 'If-Match': etag, 'Idempotency-Key': key },
      }),
    );
    return {
      view: response.data,
      etag: response.headers.get('ETag') ?? '',
      appended: response.status === 201,
    };
  }
  versions(
    project: string,
    query: NonNullable<ListProjectAiSettingsVersionsData['query']>,
    signal?: AbortSignal,
  ) {
    return this.gateway.call((client) =>
      listProjectAiSettingsVersions({
        client,
        path: { p: project },
        query,
        ...(signal === undefined ? {} : { signal }),
      }),
    );
  }
  version(project: string, version: number, signal?: AbortSignal) {
    return this.gateway.call((client) =>
      getProjectAiSettingsVersion({
        client,
        path: { p: project, version_no: version },
        ...(signal === undefined ? {} : { signal }),
      }),
    );
  }
  templates(project: string, signal?: AbortSignal) {
    return this.gateway.call((client) =>
      listProjectAiSettingsTemplates({
        client,
        path: { p: project },
        ...(signal === undefined ? {} : { signal }),
      }),
    );
  }
}
