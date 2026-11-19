import { computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, type ParamMap } from '@angular/router';
import {
  patchState,
  signalStore,
  withComputed,
  withHooks,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import type { ExportAuthoringData } from '@evidgate/sdk';
import { SessionStore } from '../../../core/session/session.store';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { createRetryAfterWait } from '../../../core/http/retry';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { ImportExportDataService, type AuthoringDownload } from './import-export-data.service';
import { AUTHORING_READ, IMPEX_COPY, importFailure } from './import-export.copy';
import type { AuthoringFormat } from './local-file-preview';

type ExportKind = 'nodes' | 'items' | 'cases';
export interface ExportDraft {
  readonly scope: 'project' | 'node' | 'selection' | 'filtered';
  readonly nodeId: string;
  readonly format: AuthoringFormat;
  readonly kinds: readonly ExportKind[];
}
interface ExportConfirmation {
  readonly project: string;
  readonly query: ExportAuthoringData['query'];
  readonly label: string;
}
const DEFAULT: ExportDraft = {
  scope: 'project',
  nodeId: '',
  format: 'csv',
  kinds: ['nodes', 'items', 'cases'],
};
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * A stable, order-independent identity for the route's query parameters. `ParamMap.toString()`
 * would stringify the map object itself (`[object Object]`); the sorted name/value pairs change
 * exactly when the requested export scope does.
 */
function parameterKey(params: ParamMap | undefined): string {
  return JSON.stringify(
    params?.keys
      .slice()
      .sort()
      .map((key) => [key, params.getAll(key)]) ?? [],
  );
}

export const ExportStore = signalStore(
  withState({
    current: null as {
      readonly project: string;
      readonly context: string;
      readonly draft: ExportDraft;
    } | null,
    confirmation: null as ExportConfirmation | null,
    pending: false,
    error: null as ApiError | null,
    completedScope: '',
    empty: false,
    activityProject: null as string | null,
    activityContext: '',
  }),
  withProps(() => ({
    _session: inject(SessionStore),
    _policy: inject(NarrowMutationPolicy),
    _data: inject(ImportExportDataService),
    _params: toSignal(inject(ActivatedRoute).queryParamMap),
    _wait: createRetryAfterWait(),
  })),
  withComputed((store) => ({
    draft: computed<ExportDraft>(() => {
      const current = store.current();
      if (
        current &&
        current.project === store._session.currentProject()?.id &&
        current.context === parameterKey(store._params())
      )
        return current.draft;
      const params = store._params(),
        node = params?.get('node_id') ?? '';
      const scope = params?.has('ids')
        ? 'selection'
        : params?.has('filter')
          ? 'filtered'
          : node
            ? 'node'
            : 'project';
      return { ...DEFAULT, nodeId: node, scope };
    }),
    canRead: computed(() => store._session.has(AUTHORING_READ)),
    retryReady: computed(() => store._wait.ready()),
    feedbackVisible: computed(
      () =>
        store.activityProject() === store._session.currentProject()?.id &&
        store.activityContext() === parameterKey(store._params()),
    ),
  })),
  withComputed((store) => ({
    unavailableReason: computed(() => {
      const draft = store.draft();
      if (draft.scope === 'selection' || draft.scope === 'filtered')
        return IMPEX_COPY.selectionUnavailable;
      if (draft.scope === 'node' && !draft.nodeId.trim()) return IMPEX_COPY.noNode;
      if (draft.scope === 'node' && !UUID.test(draft.nodeId)) return IMPEX_COPY.nodeInvalid;
      if (!draft.kinds.length) return IMPEX_COPY.noKinds;
      return null;
    }),
    scopeLabel: computed(() =>
      store.draft().scope === 'node'
        ? `${IMPEX_COPY.nodeScope}: ${store.draft().nodeId}`
        : `${IMPEX_COPY.projectScope}: ${store._session.currentProject()?.name ?? ''}`,
    ),
  })),
  withComputed((store) => ({
    query: computed<ExportAuthoringData['query'] | null>(() => {
      if (store.unavailableReason()) return null;
      const draft = store.draft();
      return {
        format: draft.format,
        ...(draft.scope === 'node' ? { node_id: draft.nodeId } : {}),
        include: draft.kinds.join(','),
      };
    }),
  })),
  withMethods((store) => {
    let controller: AbortController | null = null;
    let unregister: (() => void) | null = null;
    const reset = (): void => {
      controller?.abort();
      store._wait.clear();
      patchState(store, {
        current: null,
        confirmation: null,
        pending: false,
        error: null,
        completedScope: '',
        empty: false,
        activityProject: null,
        activityContext: '',
      });
    };
    return {
      edit(change: Partial<ExportDraft>): void {
        const project = store._session.currentProject()?.id;
        if (
          !project ||
          !store.canRead() ||
          (('scope' in change || 'nodeId' in change) && !store._policy.allowsMutation())
        )
          return;
        patchState(store, {
          current: {
            project,
            context: parameterKey(store._params()),
            draft: { ...store.draft(), ...change },
          },
          confirmation: null,
          error: null,
          completedScope: '',
          empty: false,
        });
      },
      openConfirmation(): void {
        const project = store._session.currentProject()?.id,
          query = store.query();
        if (!project || !query || !store.canRead() || store.pending() || !store._wait.ready())
          return;
        patchState(store, { confirmation: { project, query, label: store.scopeLabel() } });
      },
      closeConfirmation(): void {
        patchState(store, { confirmation: null });
      },
      async confirmDownload(): Promise<AuthoringDownload | null> {
        const confirmation = store.confirmation();
        if (
          !confirmation ||
          store.pending() ||
          !store.canRead() ||
          !store._wait.ready() ||
          confirmation.project !== store._session.currentProject()?.id ||
          JSON.stringify(confirmation.query) !== JSON.stringify(store.query())
        )
          return null;
        patchState(store, {
          confirmation: null,
          pending: true,
          error: null,
          completedScope: '',
          empty: false,
          activityProject: confirmation.project,
          activityContext: parameterKey(store._params()),
        });
        const active = new AbortController();
        controller = active;
        const owns = (): boolean =>
          !active.signal.aborted &&
          confirmation.project === store._session.currentProject()?.id &&
          JSON.stringify(confirmation.query) === JSON.stringify(store.query());
        try {
          const file = await store._data.exportFile(
            confirmation.project,
            confirmation.query,
            active.signal,
          );
          if (!owns() || !store.canRead()) return null;
          patchState(store, { completedScope: confirmation.label, empty: file.blob.size === 0 });
          return file.blob.size === 0 ? null : file;
        } catch (cause) {
          if (!owns()) return null;
          const error: ApiError = isApiRequestError(cause)
            ? cause.error
            : {
                kind: 'unexpected',
                status: 0,
                code: 'export_response_invalid',
                message: '',
                requestId: null,
                retryable: false,
                retryAfterSeconds: null,
                details: [],
              };
          patchState(store, { error: { ...error, message: importFailure(error, true) } });
          store._wait.start(error.retryAfterSeconds);
          return null;
        } finally {
          if (controller === active) {
            controller = null;
            patchState(store, { pending: false });
          }
        }
      },
      _start(): void {
        unregister = store._session.onSessionEnd(reset);
      },
      _stop(): void {
        reset();
        unregister?.();
      },
    };
  }),
  withHooks({ onInit: (store) => store._start(), onDestroy: (store) => store._stop() }),
);
