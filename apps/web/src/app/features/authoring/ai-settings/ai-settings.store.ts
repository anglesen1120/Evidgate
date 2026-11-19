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
import type { AiSettingsDocument, AiSettingsTemplateRef } from '@evidgate/sdk';
import {
  AiSettingsDataService,
  type AiSettingsHead,
} from '../../../core/data/ai-settings-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { newUuid } from '../../../core/http/random-id';
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

export const AiSettingsStore = signalStore(
  withState({
    saving: false,
    failure: null as ApiError | null,
    conflictOpen: false,
    saved: '',
    cursor: undefined as string | undefined,
    cursorPath: [] as string[],
    cursorCycle: false,
    selectedVersion: 0,
    compareVersion: 0,
  }),
  withProps(() => ({
    data: inject(AiSettingsDataService),
    session: inject(SessionStore),
    viewport: inject(NarrowMutationPolicy),
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
    const head = resource({
      params: scope,
      loader: ({ params, abortSignal }) => store.data.read(params.project, abortSignal),
    });
    const history = resource({
      params: () => {
        const current = scope();
        return current ? { ...current, cursor: store.cursor() } : undefined;
      },
      loader: ({ params, abortSignal }) =>
        store.data.versions(
          params.project,
          { limit: 50, ...(params.cursor ? { cursor: params.cursor } : {}) },
          abortSignal,
        ),
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
      templates,
      conflict,
      detail,
      comparison,
      base,
      draft,
      reason,
      template,
      historyPage: computed(() => (history.hasValue() ? history.value() : undefined)),
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
      patchState(store, { saved: '', failure: null });
    },
    setReason(reason: string): void {
      store.reason.set(reason);
      patchState(store, { saved: '', failure: null });
    },
    useTemplate(settings: AiSettingsDocument, template: AiSettingsTemplateRef): void {
      if (!store.canWrite() || !store.viewport.allowsMutation()) return;
      store.draft.set(settings);
      store.template.set(template);
      patchState(store, { saved: '', failure: null });
    },
    selectVersion(version: number): void {
      patchState(store, { selectedVersion: version });
    },
    compareWith(version: number): void {
      patchState(store, { compareVersion: version });
    },
    nextPage(): void {
      const next = store.historyPage()?.next_cursor;
      if (!next) return;
      if (store.cursorPath().includes(next)) {
        patchState(store, { cursorCycle: true });
        return;
      }
      patchState(store, { cursor: next, cursorPath: [...store.cursorPath(), next] });
    },
    firstPage(): void {
      patchState(store, { cursor: undefined, cursorPath: [], cursorCycle: false });
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
        store.saving()
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
        store.history.reload();
        store.intent.body = '';
        patchState(store, { saved: result.appended ? 'saved' : 'unchanged' });
        return true;
      } catch (error) {
        if (!isApiRequestError(error)) throw error;
        if (store.scope() === scope)
          patchState(store, {
            failure: error.error,
            conflictOpen: error.error.kind === 'precondition_failed',
          });
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
        patchState(store, {
          failure: null,
          conflictOpen: false,
          saved: '',
          cursor: undefined,
          cursorPath: [],
          cursorCycle: false,
          selectedVersion: 0,
          compareVersion: 0,
        });
      });
    },
  })),
);
