import { inject, Injectable } from '@angular/core';
import type {
  ExportAuthoringData,
  ImportAuthoringData,
  ImportAuthoringErrors,
  ImportAuthoringResponses,
} from '@evidgate/sdk';
import {
  AuthoringImportExportGateway,
  AUTHORING_XLSX_MEDIA_TYPE,
  type AuthoringExportFile,
} from '../../../core/data/authoring-import-export-gateway';
import { AuthoringVersionsDataService } from '../../../core/data/authoring-versions-data.service';
import { ApiRequestError } from '../../../core/http/api-error';
import { ApiGateway } from '../../../core/http/api-gateway';
import {
  BINARY_REQUEST,
  BINARY_TRANSPORT_HEADER,
  binaryTransport,
} from '../../../core/http/binary-transport';
import { IMPORT_BYTE_CAP, type AuthoringFormat } from './local-file-preview';
import { IMPEX_COPY, importSizeError } from './import-export.copy';

export type ExportQuery = ExportAuthoringData['query'];
export type AuthoringDownload = AuthoringExportFile;
const MEDIA_TYPE: Readonly<Record<AuthoringFormat, string>> = {
  csv: 'text/csv',
  json: 'application/json',
  xlsx: AUTHORING_XLSX_MEDIA_TYPE,
};

async function assertCsvEncoding(file: File, signal?: AbortSignal): Promise<void> {
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
    if (format !== 'xlsx' && file.name.toLowerCase().endsWith('.xlsx'))
      throw new ApiRequestError({
        kind: 'validation',
        status: 422,
        code: 'format_mismatch',
        message: IMPEX_COPY.formatMismatch,
        requestId: null,
        retryable: false,
        retryAfterSeconds: null,
        details: [{ path: '/format', reason: 'unsupported_format' }],
      });
    if (format === 'xlsx' && file.size > IMPORT_BYTE_CAP)
      throw new ApiRequestError(importSizeError(file, format));
    signal?.throwIfAborted();
    if (format === 'csv') await assertCsvEncoding(file, signal);
    // The generator types only the JSON media body; the serializer preserves the original
    // file for every format without asserting that binary content is a JSON DTO.
    const url: ImportAuthoringData['url'] = '/projects/{p}/authoring/import';
    return this.gateway.call((client) =>
      client.post<ImportAuthoringResponses, ImportAuthoringErrors>({
        url,
        path: { p: project },
        query: { format, dry_run: dryRun },
        headers: {
          'Idempotency-Key': key,
          'Content-Type': MEDIA_TYPE[format],
          ...(format === 'xlsx'
            ? { [BINARY_TRANSPORT_HEADER]: binaryTransport(BINARY_REQUEST) }
            : {}),
        },
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
