// 00 §5.6 W5, ADR-0008 decision (a): `evidgate/no-http-outside-core` keeps every
// `@evidgate/sdk` value import inside `core/{http,data}`, because an SDK call is the wire, not the
// feature. The authoring export is the one operation of that screen the generated client cannot
// express from `core/http/api-gateway` alone: it is a `parseAs: 'blob'` download whose filename
// lives in `Content-Disposition`, so this seam owns the SDK call and the header contract, and the
// feature keeps the feature-shaped part (scope, refusal copy, retry pacing).
import { inject, Injectable } from '@angular/core';
import { exportAuthoring } from '@evidgate/sdk';
import type { ExportAuthoringData } from '@evidgate/sdk';
import { ApiGateway } from '../http/api-gateway';

/** One downloaded export: the bytes the server sent and the fixed name it chose for them. */
export interface AuthoringExportFile {
  readonly blob: Blob;
  readonly filename: string;
}

/**
 * The only filename the export route sends (`authoring-export.<format>`). A caller-supplied name
 * is never accepted: the server decides it, so a download cannot be steered by query data.
 */
const SERVER_FILENAME = /^attachment;\s*filename="(authoring-export\.(?:csv|json|xlsx))"$/i;

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
        ...(signal ? { signal } : {}),
      }),
    );
    // The same generator chooses the JSON response type even for parseAs: 'blob'. Narrow the
    // actual value rather than asserting that wire bytes match that generated JSON DTO.
    const blob: unknown = result.data;
    const disposition = result.headers.get('Content-Disposition');
    const filename = disposition ? SERVER_FILENAME.exec(disposition)?.[1] : undefined;
    if (!(blob instanceof Blob) || !filename)
      throw new Error('Export response is missing its file or fixed server filename');
    return { blob, filename };
  }
}
