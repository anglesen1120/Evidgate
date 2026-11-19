import { computed, inject, linkedSignal, resource } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { map } from 'rxjs';
import {
  patchState,
  signalStore,
  withComputed,
  withHooks,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import {
  AuthoringEditorDataService,
  AuthoringCaseConflict,
  type AuthoringCaseHead,
} from '../../../core/data/authoring-editor-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { createRetryAfterWait } from '../../../core/http/retry';
import { newUuid } from '../../../core/http/random-id';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import {
  bodyOf,
  draftOf,
  EDITOR_READ,
  EDITOR_WRITE,
  type CaseDraft,
  type CaseTextField,
  type CasePriority,
  type StepTextField,
} from './editor.model';

export interface EditorScope {
  readonly org: string;
  readonly project: string;
  readonly caseId: string;
}
interface HeadSource {
  readonly scope: EditorScope | undefined;
  readonly value: AuthoringCaseHead | undefined;
}

export const EditorStore = signalStore(
  withState({
    saving: false,
    restoring: false,
    saveError: null as ApiError | null,
    mutationScope: undefined as EditorScope | undefined,
    conflictRequested: false,
    conflictSnapshot: null as AuthoringCaseHead | null,
    saved: null as null | 'saved' | 'unchanged',
  }),
  withProps(() => ({
    data: inject(AuthoringEditorDataService),
    session: inject(SessionStore),
    viewport: inject(NarrowMutationPolicy),
    route: inject(ActivatedRoute),
    retryWait: createRetryAfterWait(),
  })),
  withProps((store) => {
    const caseId = toSignal(store.route.paramMap.pipe(map((params) => params.get('caseId'))));
    const publishedScope = computed<EditorScope | undefined>(
      () => {
        const org = store.session.currentOrg()?.id;
        const project = store.session.currentProject()?.id;
        const id = caseId();
        return org && project && id && store.session.has(EDITOR_READ)
          ? { org, project, caseId: id }
          : undefined;
      },
      {
        equal: (a, b) => a?.org === b?.org && a?.project === b?.project && a?.caseId === b?.caseId,
      },
    );
    // Destination scope guards publish the next org/project before this route's canDeactivate
    // runs. Keeping the loaded case until the URL names another case keeps the draft, and its
    // dirty state, intact while the visitor decides whether to leave.
    const scope = linkedSignal<EditorScope | undefined, EditorScope | undefined>({
      source: () => publishedScope(),
      computation: (source, previous) =>
        source && previous?.value && previous.value.caseId === source.caseId
          ? previous.value
          : source,
    });
    const head = resource({
      params: scope,
      loader: ({ params, abortSignal }) =>
        store.data.readCase(params.project, params.caseId, abortSignal),
    });
    const base = linkedSignal<HeadSource, AuthoringCaseHead | undefined>({
      source: () => ({ scope: scope(), value: head.hasValue() ? head.value() : undefined }),
      computation: (source, previous) =>
        source.value ?? (source.scope === previous?.source.scope ? previous?.value : undefined),
    });
    const baseDraft = computed(() => {
      const current = base();
      return current ? draftOf(current.view) : undefined;
    });
    const draft = linkedSignal<CaseDraft | undefined>(() => baseDraft());
    const conflictOpen = computed(
      () => store.mutationScope() === scope() && store.conflictRequested(),
    );
    const conflictRead = resource({
      params: () => (conflictOpen() && !store.conflictSnapshot() ? scope() : undefined),
      loader: ({ params, abortSignal }) =>
        store.data.readCase(params.project, params.caseId, abortSignal),
    });
    return {
      scope,
      head,
      base,
      baseDraft,
      draft,
      conflictOpen,
      conflictRead,
      intent: { serialized: '', key: '' },
      currentConflict: computed(() =>
        conflictOpen()
          ? (store.conflictSnapshot() ??
            (conflictRead.hasValue() ? conflictRead.value() : undefined))
          : undefined,
      ),
    };
  }),
  withComputed((store) => ({
    canWrite: computed(() => store.session.has(EDITOR_WRITE)),
    failure: computed(() => (store.mutationScope() === store.scope() ? store.saveError() : null)),
    result: computed(() => (store.mutationScope() === store.scope() ? store.saved() : null)),
    dirty: computed(() => {
      const draft = store.draft();
      const base = store.baseDraft();
      return !!draft && !!base && JSON.stringify(bodyOf(draft)) !== JSON.stringify(bodyOf(base));
    }),
    // A case write or a head refresh must finish before the draft accepts input, because the
    // arriving head replaces baseDraft and would otherwise discard edits made during the read.
    locked: computed(() => store.saving() || store.restoring() || store.head.isLoading()),
  })),
  withMethods((store) => {
    function edit(next: CaseDraft): void {
      if (
        !store.canWrite() ||
        !store.viewport.allowsMutation() ||
        store.locked() ||
        store.base()?.view.state === 'retired'
      )
        return;
      store.draft.set(next);
      patchState(store, {
        saved: null,
        saveError: store.retryWait.ready() ? null : store.saveError(),
      });
    }
    function steps(next: CaseDraft['steps']): void {
      const draft = store.draft();
      if (draft) edit({ ...draft, steps: next.map((step, index) => ({ ...step, no: index + 1 })) });
    }
    return {
      editText(key: CaseTextField, value: string): void {
        const draft = store.draft();
        if (draft) edit({ ...draft, [key]: value });
      },
      editPriority(priority: CasePriority): void {
        const draft = store.draft();
        if (draft) edit({ ...draft, priority });
      },
      editStep(id: string, key: StepTextField, value: string): void {
        const draft = store.draft();
        if (draft)
          edit({
            ...draft,
            steps: draft.steps.map((step) => (step.id === id ? { ...step, [key]: value } : step)),
          });
      },
      addStep(): void {
        const draft = store.draft();
        if (draft && draft.steps.length < 100)
          steps([
            ...draft.steps,
            { id: newUuid(), no: draft.steps.length + 1, action: '', expected: '', data: '' },
          ]);
      },
      removeStep(id: string): void {
        const draft = store.draft();
        if (draft && draft.steps.length > 1) steps(draft.steps.filter((step) => step.id !== id));
      },
      moveStep(id: string, offset: -1 | 1): void {
        const draft = store.draft();
        if (!draft) return;
        const next = [...draft.steps];
        const index = next.findIndex((step) => step.id === id);
        const target = index + offset;
        const step = next[index];
        if (!step || target < 0 || target >= next.length) return;
        next.splice(index, 1);
        next.splice(target, 0, step);
        steps(next);
      },
      closeConflict(): void {
        patchState(store, { conflictRequested: false });
      },
      reloadConflict(): void {
        const current = store.currentConflict();
        if (store.saving()) return;
        if (!current) {
          store.conflictRead.reload();
          return;
        }
        store.head.set(current);
        patchState(store, {
          conflictRequested: false,
          conflictSnapshot: null,
          saveError: null,
          saved: null,
        });
      },
      // Timeline keeps a restore alive across tab switches; ownership lives here so Save cannot
      // write the same case while that restore is still in flight.
      beginRestore(): boolean {
        if (store.saving() || store.restoring() || store.head.isLoading()) return false;
        patchState(store, { restoring: true });
        return true;
      },
      endRestore(): void {
        if (store.restoring()) patchState(store, { restoring: false });
      },
      refreshAfterRestore(): void {
        if (store.dirty() || store.saving()) return;
        store.head.reload();
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
          store.locked() ||
          !store.retryWait.ready() ||
          store.conflictOpen() ||
          base.view.state === 'retired'
        )
          return false;
        const body = bodyOf(draft);
        const serialized = JSON.stringify({ scope, etag: base.etag, body });
        if (store.intent.serialized !== serialized) {
          store.intent.serialized = serialized;
          store.intent.key = newUuid();
        }
        patchState(store, {
          saving: true,
          mutationScope: scope,
          saveError: null,
          saved: null,
          conflictSnapshot: null,
        });
        try {
          const result = await store.data.updateCase(
            scope.project,
            scope.caseId,
            body,
            base.etag,
            store.intent.key,
          );
          if (store.scope() !== scope) return false;
          store.head.set(result);
          store.intent.serialized = '';
          patchState(store, {
            saved: result.view.version === base.view.version ? 'unchanged' : 'saved',
          });
          return true;
        } catch (error) {
          if (!isApiRequestError(error)) throw error;
          if (store.scope() === scope) {
            patchState(store, {
              saveError: error.error,
              conflictRequested: error.error.kind === 'precondition_failed',
              conflictSnapshot: error instanceof AuthoringCaseConflict ? error.current : null,
            });
            if (error.error.kind === 'unavailable' || error.error.kind === 'rate_limited')
              store.retryWait.start(error.error.retryAfterSeconds);
          }
          return false;
        } finally {
          patchState(store, { saving: false });
        }
      },
    };
  }),
  withHooks((store) => ({
    onDestroy() {
      store.retryWait.clear();
    },
  })),
);
