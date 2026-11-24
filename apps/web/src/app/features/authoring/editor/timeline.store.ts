import { computed, inject, linkedSignal, resource } from '@angular/core';
import {
  patchState,
  signalStore,
  withComputed,
  withHooks,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import type { AuthoringVersionListResponse } from '@evidgate/sdk';
import {
  AuthoringEditorDataService,
  type AuthoringVersionHead,
} from '../../../core/data/authoring-editor-data.service';
import { ApiRequestError, isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { newUuid } from '../../../core/http/random-id';
import { createRetryAfterWait } from '../../../core/http/retry';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { EditorStore, type EditorScope } from './editor.store';

interface TimelinePage extends AuthoringVersionListResponse {
  readonly page: number;
  readonly cursors: readonly string[];
}
interface TimelineSource {
  readonly scope: EditorScope | undefined;
  readonly size: number;
  readonly page: TimelinePage | undefined;
}
async function readVersion(
  data: AuthoringEditorDataService,
  scope: EditorScope,
  id: string,
  signal: AbortSignal,
): Promise<AuthoringVersionHead> {
  const result = await data.version(scope.project, id, signal);
  if (result.view.entity_kind !== 'manual_test_case' || result.view.entity_id !== scope.caseId)
    throw new ApiRequestError({
      kind: 'not_found',
      status: 404,
      code: 'not_found',
      message: '',
      details: [],
      retryable: false,
      requestId: null,
      retryAfterSeconds: null,
    });
  return result;
}
export const TimelineStore = signalStore(
  withState({
    restoring: false,
    restoreFailure: null as ApiError | null,
    restored: 0,
    mutationScope: undefined as EditorScope | undefined,
  }),
  withProps(() => ({
    editor: inject(EditorStore),
    data: inject(AuthoringEditorDataService),
    viewport: inject(NarrowMutationPolicy),
    retryWait: createRetryAfterWait(),
  })),
  withProps((store) => {
    const scope = computed(() => store.editor.scope());
    const pageSize = linkedSignal(() => {
      scope();
      return 20;
    });
    const target = linkedSignal(() => {
      scope();
      pageSize();
      return { page: 1, cursors: [] as readonly string[], cursor: undefined as string | undefined };
    });
    const selected = linkedSignal(() => {
      scope();
      return '';
    });
    const compared = linkedSignal(() => {
      scope();
      return '';
    });
    const history = resource({
      params: () => {
        const current = scope();
        return current ? { scope: current, target: target(), size: pageSize() } : undefined;
      },
      loader: async ({ params, abortSignal }) => ({
        ...(await store.data.versions(
          params.scope.project,
          params.scope.caseId,
          { limit: params.size, ...(params.target.cursor ? { cursor: params.target.cursor } : {}) },
          abortSignal,
        )),
        page: params.target.page,
        cursors: params.target.cursors,
      }),
    });
    const historyPage = linkedSignal<TimelineSource, TimelinePage | undefined>({
      source: () => ({
        scope: scope(),
        size: pageSize(),
        page: history.hasValue() ? history.value() : undefined,
      }),
      computation: (source, previous) =>
        source.page ??
        (source.scope === previous?.source.scope && source.size === previous?.source.size
          ? previous?.value
          : undefined),
    });
    const detail = resource({
      params: () => {
        const current = scope();
        return current && selected() ? { scope: current, id: selected() } : undefined;
      },
      loader: ({ params, abortSignal }) =>
        readVersion(store.data, params.scope, params.id, abortSignal),
    });
    const comparison = resource({
      params: () => {
        const current = scope();
        return current && compared() ? { scope: current, id: compared() } : undefined;
      },
      loader: ({ params, abortSignal }) =>
        readVersion(store.data, params.scope, params.id, abortSignal),
    });
    return {
      scope,
      target,
      pageSize,
      page: computed(() => historyPage()?.page ?? 1),
      cursor: computed(() => target().cursor),
      cursorPath: computed(() => historyPage()?.cursors ?? []),
      selected,
      compared,
      history,
      rows: computed(() => historyPage()?.items ?? []),
      detail,
      comparison,
      versionDetail: computed(() => (detail.hasValue() ? detail.value() : undefined)),
      comparedDetail: computed(() => (comparison.hasValue() ? comparison.value() : undefined)),
      nextCursor: computed(() => historyPage()?.next_cursor ?? null),
      intent: { target: '', key: '' },
    };
  }),
  withComputed((store) => ({
    cursorCycle: computed(() => {
      const cursor = store.nextCursor();
      return !!cursor && store.cursorPath().includes(cursor);
    }),
    failure: computed(() =>
      store.scope() === store.mutationScope() ? store.restoreFailure() : null,
    ),
    appendedVersion: computed(() =>
      store.scope() === store.mutationScope() ? store.restored() : 0,
    ),
    refreshRequired: computed(
      () =>
        store.restored() > 0 && !store.rows().some((row) => row.version_no === store.restored()),
    ),
  })),
  withMethods((store) => ({
    selectVersion(id: string): void {
      if (store.rows().some((row) => row.version_id === id)) store.selected.set(id);
    },
    compareVersion(id: string): void {
      if (store.selected() && store.rows().some((row) => row.version_id === id))
        store.compared.set(id);
    },
    nextPage(): void {
      const next = store.nextCursor();
      if (!next || store.history.isLoading() || store.cursorCycle()) return;
      store.target.set({
        page: store.page() + 1,
        cursor: next,
        cursors: [...store.cursorPath(), next],
      });
    },
    previousPage(): void {
      if (store.page() <= 1 || store.history.isLoading()) return;
      const cursors = store.cursorPath().slice(0, -1);
      store.target.set({ page: store.page() - 1, cursor: cursors.at(-1), cursors });
    },
    setPageSize(size: number): void {
      if ([20, 50, 100].includes(size) && size !== store.pageSize()) store.pageSize.set(size);
    },
    refresh(): void {
      store.target.set({ page: 1, cursor: undefined, cursors: [] });
    },
    async restore(id: string): Promise<boolean> {
      const scope = store.scope();
      const detail = store.versionDetail();
      if (
        !scope ||
        !detail ||
        detail.view.version_id !== id ||
        !store.editor.canWrite() ||
        !store.viewport.allowsMutation() ||
        store.restoring() ||
        !store.retryWait.ready() ||
        !store.editor.beginRestore()
      )
        return false;
      const intent = JSON.stringify({ scope, id });
      if (store.intent.target !== intent) {
        store.intent.target = intent;
        store.intent.key = newUuid();
      }
      patchState(store, {
        restoring: true,
        restoreFailure: null,
        restored: 0,
        mutationScope: scope,
      });
      try {
        const result = await store.data.restoreVersion(scope.project, id, store.intent.key);
        if (store.scope() !== scope) return false;
        patchState(store, { restored: result.version });
        store.intent.target = '';
        store.target.set({ page: 1, cursor: undefined, cursors: [] });
        store.editor.endRestore();
        store.editor.refreshAfterRestore();
        return true;
      } catch (error) {
        if (!isApiRequestError(error)) throw error;
        if (store.scope() === scope) {
          patchState(store, { restoreFailure: error.error });
          if (error.error.kind === 'unavailable' || error.error.kind === 'rate_limited')
            store.retryWait.start(error.error.retryAfterSeconds);
        }
        return false;
      } finally {
        store.editor.endRestore();
        patchState(store, { restoring: false });
      }
    },
  })),
  withHooks((store) => ({
    onDestroy() {
      store.retryWait.clear();
    },
  })),
);
