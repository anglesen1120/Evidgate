// The authoring case editor's one data path: the manual-case read and save, its version
// timeline, and the restore of one stored version. Every call goes through the generated SDK and
// `ApiGateway`, so a refusal reaches the editor as the same typed `ApiRequestError` every other
// screen consumes.
//
// Two refusals get a shape the editor needs beyond the typed error: a stale `If-Match` carries the
// server head the user must be shown, and a `422` names the refused fields. Both are re-thrown as
// subclasses of `ApiRequestError`, so an existing `isApiRequestError` catch still works.

import { inject, Injectable } from '@angular/core';
import {
  getAuthoringVersion,
  getManualCase,
  listAuthoringVersions,
  restoreAuthoringVersion,
  updateManualCase,
  type AuthoringRestoreResponse,
  type AuthoringVersionDetailDto,
  type AuthoringVersionListResponse,
  type ErrorEnvelopeDetail,
  type ListAuthoringVersionsData,
  type ManualCaseDetailDto,
  type UpdateManualCaseRequest,
} from '@evidgate/sdk';
import { ApiGateway } from '../http/api-gateway';
import { ApiRequestError, type ApiError } from '../http/api-error';

/** One read of a case together with the ETag its next write must quote. */
export interface AuthoringCaseHead {
  readonly view: ManualCaseDetailDto;
  readonly etag: string;
}

/** One stored version with the `content_hash` ETag the contract declares. */
export interface AuthoringVersionHead {
  readonly view: AuthoringVersionDetailDto;
  readonly etag: string;
}

/**
 * The timeline query for one case. The entity filters are fixed by the service — the timeline tab
 * shows a case's own history — so a caller cannot ask for another entity's rows by accident.
 */
export type AuthoringVersionsQuery = Omit<
  NonNullable<ListAuthoringVersionsData['query']>,
  'entity_id' | 'entity_kind'
>;

/**
 * A save refused because the case changed since it was read. `current` is the server head read
 * after the refusal, which is what the conflict panel diffs against.
 */
export class AuthoringCaseConflict extends ApiRequestError {
  constructor(
    error: ApiError,
    readonly current: AuthoringCaseHead,
  ) {
    super(error);
    this.name = 'AuthoringCaseConflict';
  }
}

/**
 * A save refused as invalid, with the contract's per-field pointers already grouped by pointer
 * (`/title`, `/steps/0/action`) so a form maps them to its own controls.
 */
export class AuthoringCaseValidation extends ApiRequestError {
  constructor(
    error: ApiError,
    readonly fields: Readonly<Record<string, readonly string[]>>,
  ) {
    super(error);
    this.name = 'AuthoringCaseValidation';
  }
}

/** Group the envelope's per-item reasons by their JSON pointer; pointerless details are dropped. */
function pointerFields(
  details: readonly ErrorEnvelopeDetail[],
): Readonly<Record<string, readonly string[]>> {
  const fields: Record<string, string[]> = {};
  for (const detail of details) {
    if (detail.path === undefined) continue;
    const reasons = (fields[detail.path] ??= []);
    reasons.push(detail.reason);
  }
  return fields;
}

@Injectable({ providedIn: 'root' })
export class AuthoringEditorDataService {
  private readonly gateway = inject(ApiGateway);

  async readCase(
    project: string,
    caseId: string,
    signal?: AbortSignal,
  ): Promise<AuthoringCaseHead> {
    const response = await this.gateway.callWithResponse((client) =>
      getManualCase({
        client,
        path: { p: project, m: caseId },
        ...(signal === undefined ? {} : { signal }),
      }),
    );
    return { view: response.data, etag: response.headers.get('ETag') ?? '' };
  }

  /**
   * Saves the edited members under the loaded ETag. The contract's `200` body is only the new
   * version number, so the full case the editor renders is read back once, in the same intent.
   */
  async updateCase(
    project: string,
    caseId: string,
    body: UpdateManualCaseRequest,
    etag: string,
    key: string,
  ): Promise<AuthoringCaseHead> {
    try {
      await this.gateway.call((client) =>
        updateManualCase({
          client,
          path: { p: project, m: caseId },
          body,
          headers: { 'If-Match': etag, 'Idempotency-Key': key },
        }),
      );
    } catch (cause) {
      throw await this.refusal(cause, project, caseId);
    }
    return this.readCase(project, caseId);
  }

  /**
   * The timeline of one case. `entity_kind`/`entity_id` are always sent together: the contract
   * refuses an id without its kind with a `422`, and the keyset order is the server's.
   */
  versions(
    project: string,
    caseId: string,
    query: AuthoringVersionsQuery,
    signal?: AbortSignal,
  ): Promise<AuthoringVersionListResponse> {
    return this.gateway.call((client) =>
      listAuthoringVersions({
        client,
        path: { p: project },
        query: { ...query, entity_kind: 'manual_test_case', entity_id: caseId },
        ...(signal === undefined ? {} : { signal }),
      }),
    );
  }

  async version(
    project: string,
    versionId: string,
    signal?: AbortSignal,
  ): Promise<AuthoringVersionHead> {
    const response = await this.gateway.callWithResponse((client) =>
      getAuthoringVersion({
        client,
        path: { p: project, v: versionId },
        ...(signal === undefined ? {} : { signal }),
      }),
    );
    return { view: response.data, etag: response.headers.get('ETag') ?? '' };
  }

  /**
   * Re-applies one stored version. The route declares no body and no `If-Match` (the version id
   * already names the state to re-apply), so only the `Idempotency-Key` travels.
   */
  restoreVersion(
    project: string,
    versionId: string,
    key: string,
  ): Promise<AuthoringRestoreResponse> {
    return this.gateway.call((client) =>
      restoreAuthoringVersion({
        client,
        path: { p: project, v: versionId },
        headers: { 'Idempotency-Key': key },
      }),
    );
  }

  /**
   * The error a failed save becomes. A stale tag is answered with the fresh server head so the
   * caller can diff; when that read fails too, the original refusal is the honest answer — a
   * conflict object without a current value would claim more than the service knows. A `422` whose
   * details name no field (a parent or cap refusal) keeps its plain mapped form.
   */
  private async refusal(cause: unknown, project: string, caseId: string): Promise<unknown> {
    if (!(cause instanceof ApiRequestError)) return cause;
    if (cause.error.kind === 'validation') {
      const fields = pointerFields(cause.error.details);
      return Object.keys(fields).length === 0
        ? cause
        : new AuthoringCaseValidation(cause.error, fields);
    }
    if (cause.error.kind !== 'precondition_failed') return cause;
    try {
      return new AuthoringCaseConflict(cause.error, await this.readCase(project, caseId));
    } catch {
      return cause;
    }
  }
}
