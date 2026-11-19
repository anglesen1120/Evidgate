import { inject, Injectable } from '@angular/core';
import type {
  ExportAuthoringData,
  ImportAuthoringData,
  ImportAuthoringErrors,
  ImportAuthoringResponses,
} from '@evidgate/sdk';
import {
  AuthoringImportExportGateway,
  type AuthoringExportFile,
} from '../../../core/data/authoring-import-export-gateway';
import { AuthoringVersionsDataService } from '../../../core/data/authoring-versions-data.service';
import { ApiRequestError } from '../../../core/http/api-error';
import { ApiGateway } from '../../../core/http/api-gateway';
import type { AuthoringFormat } from './local-file-preview';
import { IMPEX_COPY } from './import-export.copy';

export type ExportQuery = ExportAuthoringData['query'];
export type AuthoringDownload = AuthoringExportFile;
const MEDIA_TYPE: Readonly<Record<'csv' | 'json', string>> = {
  csv: 'text/csv',
  json: 'application/json',
};

@Injectable()
export class ImportExportDataService {
  private readonly gateway = inject(ApiGateway);
  private readonly authoring = inject(AuthoringImportExportGateway);
  private readonly versions = inject(AuthoringVersionsDataService);

  listVersions(project: string, signal?: AbortSignal, cursor?: string) {
    return this.versions.list(project, signal, cursor);
  }

  async importFile(
    project: string,
    file: File,
    format: AuthoringFormat,
    dryRun: boolean,
    key: string,
    signal?: AbortSignal,
  ) {
    if (format === 'xlsx' || file.name.toLowerCase().endsWith('.xlsx'))
      throw new ApiRequestError({
        kind: 'validation',
        status: 422,
        code: 'format_unavailable',
        message: IMPEX_COPY.xlsx,
        requestId: null,
        retryable: false,
        retryAfterSeconds: null,
        details: [{ path: '/format', reason: 'unsupported_format' }],
      });
    signal?.throwIfAborted();
    if (format === 'csv') {
      const bytes = await file.arrayBuffer();
      try {
        new TextDecoder('utf-8', { fatal: true }).decode(bytes);
      } catch {
        throw new ApiRequestError({
          kind: 'validation',
          status: 422,
          code: 'invalid_utf8',
          message: IMPEX_COPY.invalidUtf8,
          requestId: null,
          retryable: false,
          retryAfterSeconds: null,
          details: [{ path: '/file', reason: 'invalid_utf8' }],
        });
      }
      signal?.throwIfAborted();
    }
    // The generated multi-media operation types only its JSON body. CSV/JSON use the existing
    // text SDK transport; XLSX is refused above without sending a request.
    const url: ImportAuthoringData['url'] = '/projects/{p}/authoring/import';
    return this.gateway.call((client) =>
      client.post<ImportAuthoringResponses, ImportAuthoringErrors>({
        url,
        path: { p: project },
        query: { format, dry_run: dryRun },
        headers: { 'Idempotency-Key': key, 'Content-Type': MEDIA_TYPE[format] },
        body: file,
        bodySerializer: () => file,
        ...(signal ? { signal } : {}),
      }),
    );
  }

  exportFile(
    project: string,
    query: ExportQuery,
    signal?: AbortSignal,
  ): Promise<AuthoringDownload> {
    return this.authoring.exportFile(project, query, signal);
  }
}
