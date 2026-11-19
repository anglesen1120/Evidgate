// Blob downloads need response headers as well as bytes; this core seam keeps the SDK call
// and its fixed-filename contract out of the presentation layer.
import { inject, Injectable } from '@angular/core';
import { exportAuthoring } from '@evidgate/sdk';
import type { ExportAuthoringData } from '@evidgate/sdk';
import { ApiGateway } from '../http/api-gateway';
import {
  BINARY_RESPONSE,
  BINARY_TRANSPORT_HEADER,
  binaryTransport,
} from '../http/binary-transport';

export const AUTHORING_XLSX_MEDIA_TYPE =
  'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';

/** One downloaded export: the bytes the server sent and the fixed name it chose for them. */
export interface AuthoringExportFile {
  readonly blob: Blob;
  readonly filename: string;
}

/**
 * The only filenames the export route sends (`authoring-export.<format>`). A caller-supplied name
 * is never accepted: the server decides it, so a download cannot be steered by query data.
 */
const SERVER_FILENAME = /^authoring-export\.(?:csv|json|xlsx)$/;
const FILENAME_PARAMETER = /^\s*filename\s*=\s*(?:"([^"]*)"|([^";\s]+))\s*$/;

/**
 * `Results.Stream` writes `attachment; filename=<name>; filename*=UTF-8''<name>`, and the bare
 * name is a token rather than a quoted string. Read the parameters instead of the whole header,
 * accept one `filename` value, and refuse anything that is not the fixed name.
 */
function serverFilename(disposition: string | null): string | null {
  if (!disposition || /[\r\n]/.test(disposition)) return null;
  const [type, ...parameters] = disposition.split(';');
  if (type?.trim().toLowerCase() !== 'attachment') return null;
  let found: string | null = null;
  for (const parameter of parameters) {
    const match = FILENAME_PARAMETER.exec(parameter);
    if (!match) continue;
    if (found !== null) return null;
    found = match[1] ?? match[2] ?? '';
  }
  return found !== null && SERVER_FILENAME.test(found) ? found : null;
}

@Injectable({ providedIn: 'root' })
export class AuthoringImportExportGateway {
  private readonly gateway = inject(ApiGateway);

  async exportFile(
    project: string,
    query: ExportAuthoringData['query'],
    signal?: AbortSignal,
  ): Promise<AuthoringExportFile> {
    const result = await this.gateway.callWithResponse((client) =>
      exportAuthoring({
        client,
        path: { p: project },
        query,
        parseAs: 'blob',
        ...(query.format === 'xlsx'
          ? { headers: { [BINARY_TRANSPORT_HEADER]: binaryTransport(BINARY_RESPONSE) } }
          : {}),
        ...(signal ? { signal } : {}),
      }),
    );
    // The same generator chooses the JSON response type even for parseAs: 'blob'. Narrow the
    // actual value rather than asserting that wire bytes match that generated JSON DTO.
    const blob: unknown = result.data;
    const filename = serverFilename(result.headers.get('Content-Disposition'));
    if (
      !(blob instanceof Blob) ||
      !filename ||
      (query.format !== 'xlsx' && filename !== `authoring-export.${query.format}`)
    )
      throw new Error('Export response is missing its file or fixed server filename');
    if (
      query.format === 'xlsx' &&
      (blob.type !== AUTHORING_XLSX_MEDIA_TYPE || filename !== 'authoring-export.xlsx')
    )
      throw new Error('Export response has an invalid workbook media type or filename');
    return { blob, filename };
  }
}
