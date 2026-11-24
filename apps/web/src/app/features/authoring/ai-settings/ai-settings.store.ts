import { computed, effect, inject, linkedSignal, resource } from '@angular/core';
import {
  patchState,
  signalStore,
  withComputed,
  withHooks,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import type {
  AiSettingsDocument,
  AiSettingsTemplateRef,
  AiSettingsVersionListResponse,
} from '@evidgate/sdk';
import {
  AiSettingsDataService,
  type AiSettingsHead,
} from '../../../core/data/ai-settings-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { newUuid } from '../../../core/http/random-id';
import { createRetryAfterWait } from '../../../core/http/retry';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { AI_READ, AI_WRITE, normalized, submission } from './ai-settings.model';

interface SettingsScope {
  readonly org: string;
  readonly project: string;
}
interface HeadSource {
  readonly scope: SettingsScope | undefined;
  readonly value: AiSettingsHead | undefined;
}
interface HistoryPage extends AiSettingsVersionListResponse {
  readonly page: number;
  readonly cursors: readonly string[];
}
interface HistorySource {
  readonly scope: SettingsScope | undefined;
  readonly size: number;
  readonly value: HistoryPage | undefined;
  readonly blocked: boolean;
}

export const AiSettingsStore = signalStore(
  withState({
    saving: false,
    failure: null as ApiError | null,
    conflictOpen: false,
    saved: '',
    selectedVersion: 0,
    compareVersion: 0,
  }),
  withProps(() => ({
    data: inject(AiSettingsDataService),
    session: inject(SessionStore),
    viewport: inject(NarrowMutationPolicy),
    retryWait: createRetryAfterWait(),
  })),
  withProps((store) => {
    const scope = computed<SettingsScope | undefined>(
      () => {
        const org = store.session.currentOrg()?.id;
        const project = store.session.currentProject()?.id;
        return org && project && store.session.has(AI_READ) ? { org, project } : undefined;
      },
      { equal: (left, right) => left?.org === right?.org && left?.project === right?.project },
    );
    const pageSize = linkedSignal(() => {
      scope();
      return 20;
    });
    const target = linkedSignal(() => {
      scope();
      pageSize();
      return { page: 1, cursors: [] as string[], cursor: undefined as string | undefined };
    });
    const head = resource({
      params: scope,
      loader: ({ params, abortSignal }) => store.data.read(params.project, abortSignal),
    });
    const history = resource({
      params: () => {
        const current = scope();
        return current ? { ...current, target: target(), size: pageSize() } : undefined;
      },
      loader: async ({ params, abortSignal }) => ({
        ...(await store.data.versions(
          params.project,
          { limit: params.size, ...(params.target.cursor ? { cursor: params.target.cursor } : {}) },
          abortSignal,
        )),
        page: params.target.page,
        cursors: params.target.cursors,
      }),
    });
    const historyPage = linkedSignal<HistorySource, HistoryPage | undefined>({
      source: () => {
        const error = history.error();
        return {
          scope: scope(),
          size: pageSize(),
          value: history.hasValue() ? history.value() : undefined,
          blocked:
            isApiRequestError(error) &&
            ['forbidden', 'not_found', 'unauthenticated'].includes(error.error.kind),
        };
      },
      computation: (source, previous) =>
        source.blocked
          ? undefined
          : (source.value ??
            (source.scope === previous?.source.scope && source.size === previous?.source.size
              ? previous?.value
              : undefined)),
    });
    const templates = resource({
      params: scope,
      loader: ({ params, abortSignal }) => store.data.templates(params.project, abortSignal),
    });
    const conflict = resource({
      params: () => (store.conflictOpen() ? scope() : undefined),
      loader: ({ params, abortSignal }) => store.data.read(params.project, abortSignal),
    });
    const detail = resource({
      params: () => {
        const current = scope();
        return current && store.selectedVersion() > 0
          ? { ...current, version: store.selectedVersion() }
          : undefined;
      },
      loader: ({ params, abortSignal }) =>
        store.data.version(params.project, params.version, abortSignal),
    });
    const comparison = resource({
      params: () => {
        const current = scope();
        return current && store.compareVersion() > 0
          ? { ...current, version: store.compareVersion() }
          : undefined;
      },
      loader: ({ params, abortSignal }) =>
        store.data.version(params.project, params.version, abortSignal),
    });
    const base = linkedSignal<HeadSource, AiSettingsHead | undefined>({
      source: () => ({ scope: scope(), value: head.hasValue() ? head.value() : undefined }),
      computation: (source, previous) =>
        source.value ??
        (source.scope?.project === previous?.source.scope?.project &&
        source.scope?.org === previous?.source.scope?.org
          ? previous?.value
          : undefined),
    });
    const draft = linkedSignal(() => base()?.view.settings);
    const reason = linkedSignal(() => {
      base();
      return '';
    });
    const template = linkedSignal<AiSettingsTemplateRef | null>(
      () => base()?.view.template ?? null,
    );
    return {
      scope,
      head,
      history,
      target,
      pageSize,
      historyPage,
      cursor: computed(() => target().cursor),
      cursorPath: computed(() => historyPage()?.cursors ?? []),
      page: computed(() => historyPage()?.page ?? 1),
      cursorCycle: computed(() => {
        const page = historyPage();
        return !!page?.next_cursor && page.cursors.includes(page.next_cursor);
      }),
      templates,
      conflict,
      detail,
      comparison,
      base,
      draft,
      reason,
      template,
      templatePage: computed(() => (templates.hasValue() ? templates.value() : undefined)),
      currentConflict: computed(() => (conflict.hasValue() ? conflict.value() : undefined)),
      versionDetail: computed(() => (detail.hasValue() ? detail.value() : undefined)),
      comparedVersion: computed(() => (comparison.hasValue() ? comparison.value() : undefined)),
      intent: { body: '', key: '' },
    };
  }),
  withComputed((store) => ({
    canWrite: computed(() => store.session.has(AI_WRITE)),
    error: computed(() => {
      const error = store.head.error();
      return isApiRequestError(error) ? error.error : null;
    }),
    dirty: computed(
      () =>
        JSON.stringify(store.draft()) !== JSON.stringify(store.base()?.view.settings) ||
        store.reason() !== '' ||
        JSON.stringify(store.template()) !== JSON.stringify(store.base()?.view.template),
    ),
  })),
  withMethods((store) => ({
    edit(document: AiSettingsDocument): void {
      store.draft.set(document);
      patchState(store, { saved: '', failure: store.retryWait.ready() ? null : store.failure() });
    },
    setReason(reason: string): void {
      store.reason.set(reason);
      patchState(store, { saved: '', failure: store.retryWait.ready() ? null : store.failure() });
    },
    useTemplate(settings: AiSettingsDocument, template: AiSettingsTemplateRef): void {
      if (!store.canWrite() || !store.viewport.allowsMutation()) return;
      store.draft.set(settings);
      store.template.set(template);
      patchState(store, { saved: '', failure: store.retryWait.ready() ? null : store.failure() });
    },
    selectVersion(version: number): void {
      patchState(store, { selectedVersion: version });
    },
    compareWith(version: number): void {
      patchState(store, { compareVersion: version });
    },
    nextPage(): void {
      const page = store.historyPage();
      const next = page?.next_cursor;
      if (!page || !next || store.history.isLoading() || store.cursorCycle()) return;
      store.target.set({ page: page.page + 1, cursor: next, cursors: [...page.cursors, next] });
    },
    previousPage(): void {
      const page = store.historyPage();
      if (!page || page.page <= 1 || store.history.isLoading()) return;
      const cursors = page.cursors.slice(0, -1);
      store.target.set({ page: page.page - 1, cursor: cursors.at(-1), cursors });
    },
    setPageSize(size: number): void {
      if ([20, 50, 100].includes(size) && size !== store.pageSize()) store.pageSize.set(size);
    },
    firstPage(): void {
      store.target.set({ page: 1, cursor: undefined, cursors: [] });
    },
    closeConflict(): void {
      patchState(store, { conflictOpen: false });
    },
    reloadConflict(): void {
      const current = store.currentConflict();
      if (store.saving()) return;
      if (!current) {
        store.conflict.reload();
        return;
      }
      store.head.set(current);
      patchState(store, { conflictOpen: false, failure: null, saved: '' });
    },
    async save(): Promise<boolean> {
      const scope = store.scope();
      const base = store.base();
      const draft = store.draft();
      if (
        !scope ||
        !base?.etag ||
        !draft ||
        !store.canWrite() ||
        !store.viewport.allowsMutation() ||
        store.saving() ||
        !store.retryWait.ready()
      )
        return false;
      const template = store.template();
      const reason = normalized(store.reason());
      const body = {
        settings: submission(draft),
        ...(template ? { template } : {}),
        ...(reason ? { reason } : {}),
      };
      const serialized = JSON.stringify({
        project: scope.project,
        org: scope.org,
        etag: base.etag,
        body,
      });
      if (store.intent.body !== serialized) {
        store.intent.body = serialized;
        store.intent.key = newUuid();
      }
      patchState(store, { saving: true, failure: null, saved: '' });
      try {
        const result = await store.data.save(scope.project, body, base.etag, store.intent.key);
        if (store.scope() !== scope) return false;
        store.head.set(result);
        store.target.set({ page: 1, cursor: undefined, cursors: [] });
        store.history.reload();
        store.intent.body = '';
        patchState(store, { saved: result.appended ? 'saved' : 'unchanged' });
        return true;
      } catch (error) {
        if (!isApiRequestError(error)) throw error;
        if (store.scope() === scope) {
          patchState(store, {
            failure: error.error,
            conflictOpen: error.error.kind === 'precondition_failed',
          });
          if (error.error.kind === 'rate_limited' || error.error.kind === 'unavailable')
            store.retryWait.start(error.error.retryAfterSeconds);
        }
        return false;
      } finally {
        patchState(store, { saving: false });
      }
    },
  })),
  withHooks((store) => ({
    onInit() {
      let previous = store.scope();
      effect(() => {
        const current = store.scope();
        if (current === previous) return;
        previous = current;
        store.intent.body = '';
        store.retryWait.clear();
        patchState(store, {
          failure: null,
          conflictOpen: false,
          saved: '',
          selectedVersion: 0,
          compareVersion: 0,
        });
      });
    },
    onDestroy() {
      store.retryWait.clear();
    },
  })),
);
