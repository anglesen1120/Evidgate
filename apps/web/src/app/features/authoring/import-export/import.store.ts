import { computed, inject, resource } from '@angular/core';
import {
  patchState,
  signalStore,
  withComputed,
  withHooks,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import type { AuthoringVersionListResponse, ImportResponse } from '@evidgate/sdk';
import { SessionStore } from '../../../core/session/session.store';
import { createIntent } from '../../../core/http/intent';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { createRetryAfterWait } from '../../../core/http/retry';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { ImportExportDataService } from './import-export-data.service';
import { AUTHORING_READ, AUTHORING_WRITE, IMPEX_COPY, importFailure } from './import-export.copy';
import { IMPORT_BYTE_CAP, parseLocalPreview, type AuthoringFormat } from './local-file-preview';

interface ImportWork {
  readonly project: string | null;
  readonly file: File | null;
  readonly format: AuthoringFormat;
  readonly key: string | null;
  readonly commitKey: string | null;
  readonly pending: boolean;
  readonly stage: 'preview' | 'confirm';
  readonly report: ImportResponse | null;
  readonly committed: ImportResponse | null;
  readonly error: ApiError | null;
}
const EMPTY: ImportWork = {
  project: null,
  file: null,
  format: 'csv',
  key: null,
  commitKey: null,
  pending: false,
  stage: 'preview',
  report: null,
  committed: null,
  error: null,
};

function tooLarge(file: File): ApiError {
  const error: ApiError = {
    kind: 'payload_too_large',
    status: 413,
    code: 'payload_too_large',
    message: '',
    requestId: null,
    retryable: false,
    retryAfterSeconds: null,
    details: [
      { path: '/file', reason: 'body_too_large', limit: IMPORT_BYTE_CAP, actual: file.size },
    ],
  };
  return { ...error, message: importFailure(error) };
}

export const ImportStore = signalStore(
  withState({ current: EMPTY, historyCursor: null as string | null }),
  withProps(() => ({
    _session: inject(SessionStore),
    _policy: inject(NarrowMutationPolicy),
    _data: inject(ImportExportDataService),
    _wait: createRetryAfterWait(),
    _historyWait: createRetryAfterWait(),
  })),
  withComputed((store) => ({
    work: computed(() =>
      store.current().project === store._session.currentProject()?.id ? store.current() : EMPTY,
    ),
    canWrite: computed(() => store._session.has(AUTHORING_WRITE)),
    canReadHistory: computed(() => store._session.has(AUTHORING_READ)),
  })),
  withProps((store) => ({
    // The preview is a local read of the picked file, so its resource must reload only when the
    // file or the chosen format changes. Comparing by identity keeps a refusal or a server report
    // (which also changes `work()`) from dropping rows already parsed from the file.
    _localSource: computed(
      () => {
        const work = store.work();
        return work.file && work.file.size <= IMPORT_BYTE_CAP
          ? { file: work.file, format: work.format }
          : undefined;
      },
      {
        equal: (previous, next) =>
          previous?.file === next?.file && previous?.format === next?.format,
      },
    ),
  })),
  withProps((store) => ({
    local: resource({
      params: () => store._localSource(),
      loader: async ({ params }) =>
        params.format === 'xlsx'
          ? parseLocalPreview('', params.format)
          : parseLocalPreview(await params.file.text(), params.format),
    }),
  })),
  withProps((store) => ({
    _historySource: computed(
      () => {
        const work = store.work();
        return work.file && work.project && store.canReadHistory()
          ? {
              project: work.project,
              batch: work.committed?.batch_id ?? null,
              cursor: store.historyCursor(),
            }
          : undefined;
      },
      {
        equal: (a, b) =>
          a?.project === b?.project && a?.batch === b?.batch && a?.cursor === b?.cursor,
      },
    ),
  })),
  withProps((store) => {
    let snapshot: { project: string; page: AuthoringVersionListResponse } | null = null;
    return {
      _forgetHistory: () => {
        snapshot = null;
      },
      history: resource({
        params: () => store._historySource(),
        loader: async ({ params, abortSignal }) => {
          try {
            const page = await store._data.listVersions(
              params.project,
              abortSignal,
              params.cursor ?? undefined,
            );
            if (!abortSignal.aborted) {
              snapshot = { project: params.project, page };
              store._historyWait.clear();
            }
            return { page, error: null };
          } catch (cause) {
            if (!isApiRequestError(cause)) throw cause;
            if (!abortSignal.aborted) store._historyWait.start(cause.error.retryAfterSeconds);
            const page = snapshot?.project === params.project ? snapshot.page : null;
            const error = {
              ...cause.error,
              message: page ? IMPEX_COPY.historyStale : IMPEX_COPY.historyFailed,
            };
            return { page, error };
          }
        },
      }),
    };
  }),
  withComputed((store) => ({
    retryReady: computed(() => store._wait.ready()),
    historyRetryReady: computed(() => store._historyWait.ready()),
    canConfirm: computed(() => {
      const work = store.work();
      return (
        store.canWrite() &&
        work.format !== 'xlsx' &&
        !work.pending &&
        work.report !== null &&
        work.committed === null &&
        work.error === null
      );
    }),
  })),
  withMethods((store) => {
    let controller: AbortController | null = null;
    let unregister: (() => void) | null = null;
    const update = (value: Partial<ImportWork>): void =>
      patchState(store, { current: { ...store.current(), ...value } });
    const refreshHistory = (): void => {
      if (!store.historyRetryReady() || store.history.isLoading() || !store._historySource())
        return;
      if (store.historyCursor()) patchState(store, { historyCursor: null });
      else store.history.reload();
    };
    const owns = (work: ImportWork): boolean =>
      work.key === store.work().key && work.project === store._session.currentProject()?.id;
    const cancel = (): void => {
      controller?.abort();
      controller = null;
      update({ pending: false });
    };
    const reset = (): void => {
      cancel();
      store._wait.clear();
      store._historyWait.clear();
      store._forgetHistory();
      patchState(store, { current: EMPTY, historyCursor: null });
    };

    async function send(dryRun: boolean): Promise<void> {
      const work = store.work();
      if (
        !work.file ||
        !work.project ||
        !work.key ||
        work.format === 'xlsx' ||
        work.pending ||
        !store._policy.allowsMutation() ||
        !store._session.has(AUTHORING_WRITE) ||
        !store._wait.ready()
      )
        return;
      if (work.file.size > IMPORT_BYTE_CAP) {
        update({ error: tooLarge(work.file), report: null });
        return;
      }
      if (!dryRun && !store.canConfirm() && !(work.stage === 'confirm' && work.error?.retryable))
        return;
      if (dryRun && work.stage === 'confirm') return;
      const intentKey = dryRun ? work.key : (work.commitKey ?? createIntent().idempotencyKey);
      const active = new AbortController();
      controller = active;
      update({
        pending: true,
        error: null,
        stage: dryRun ? 'preview' : 'confirm',
        ...(!dryRun ? { commitKey: intentKey } : {}),
        ...(dryRun ? { report: null, committed: null } : {}),
      });
      try {
        const report = await store._data.importFile(
          work.project,
          work.file,
          work.format,
          dryRun,
          intentKey,
          active.signal,
        );
        if (owns(work) && !active.signal.aborted) {
          update(dryRun ? { report } : { committed: report });
          if (!dryRun && report.batch_id !== null) patchState(store, { historyCursor: null });
        }
      } catch (cause) {
        if (active.signal.aborted || !owns(work)) return;
        if (!isApiRequestError(cause)) throw cause;
        const error = { ...cause.error, message: importFailure(cause.error) };
        update({ error, ...(error.kind === 'validation' ? { report: null } : {}) });
        store._wait.start(error.retryAfterSeconds);
      } finally {
        if (controller === active) controller = null;
        if (owns(work) && !active.signal.aborted) update({ pending: false });
      }
    }

    return {
      selectFile(file: File | null): void {
        if (!store._policy.allowsMutation() || !store.canWrite()) return;
        reset();
        const format = file?.name.toLowerCase().endsWith('.json')
          ? 'json'
          : file?.name.toLowerCase().endsWith('.xlsx')
            ? 'xlsx'
            : 'csv';
        update({
          file,
          project: store._session.currentProject()?.id ?? null,
          format,
          key: file ? createIntent().idempotencyKey : null,
        });
        if (file && file.size > IMPORT_BYTE_CAP) update({ error: tooLarge(file) });
      },
      setFormat(format: AuthoringFormat): void {
        if (
          !store._policy.allowsMutation() ||
          !store.canWrite() ||
          store.work().pending ||
          format === 'xlsx' ||
          store.work().file?.name.toLowerCase().endsWith('.xlsx') ||
          format === store.work().format
        )
          return;
        cancel();
        store._wait.clear();
        update({
          format,
          key: store.work().file ? createIntent().idempotencyKey : null,
          commitKey: null,
          report: null,
          committed: null,
          error: null,
          stage: 'preview',
        });
      },
      preview: () => send(true),
      confirm: () => send(false),
      retry: () => send(store.work().stage === 'preview'),
      cancel,
      refreshHistory,
      olderHistory(): void {
        const cursor = store.history.hasValue() ? store.history.value().page?.next_cursor : null;
        if (cursor && store.historyRetryReady() && !store.history.isLoading())
          patchState(store, { historyCursor: cursor });
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
