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
import { ApiGateway } from '../../../core/http/api-gateway';
import type { AuthoringFormat } from './local-file-preview';

export type ExportQuery = ExportAuthoringData['query'];
export type AuthoringDownload = AuthoringExportFile;
const MEDIA_TYPE: Readonly<Record<AuthoringFormat, string>> = {
  csv: 'text/csv',
  json: 'application/json',
  xlsx: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
};

@Injectable()
export class ImportExportDataService {
  private readonly gateway = inject(ApiGateway);
  private readonly authoring = inject(AuthoringImportExportGateway);

  importFile(
    project: string,
    file: File,
    format: AuthoringFormat,
    dryRun: boolean,
    key: string,
    signal?: AbortSignal,
  ) {
    // The generated multi-media operation types only its JSON body. The configured SDK client
    // still owns URL serialization, XSRF/interceptors and error mapping; its raw serializer sends the file unchanged.
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
