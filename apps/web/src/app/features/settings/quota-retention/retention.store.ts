import { computed, inject, linkedSignal, resource } from '@angular/core';
import type { RetentionPolicy } from '@evidgate/sdk';
import {
  patchState,
  signalStore,
  withComputed,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';

import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { RetentionDataService } from '../../../core/data/retention-data.service';
import { SessionStore } from '../../../core/session/session.store';

type RetentionScope = {
  readonly org: string;
  readonly project: string;
  readonly writable: boolean;
};
// An A -> B -> A project round trip builds a fresh scope object for the same list, so a write
// answer is matched by the list it names, never by an object's identity.
const sameScope = (
  left: RetentionScope | null | undefined,
  right: RetentionScope | null | undefined,
): boolean => left?.org === right?.org && left?.project === right?.project;
type WriteState = {
  readonly scope: RetentionScope | null;
  readonly phase: 'idle' | 'saving' | 'saved' | 'error';
  readonly error: ApiError | null;
};

export const RetentionStore = signalStore(
  withState<{ write: WriteState }>({ write: { scope: null, phase: 'idle', error: null } }),
  withProps(() => {
    const session = inject(SessionStore);
    const data = inject(RetentionDataService);
    const selection = computed(() => ({
      org: session.currentOrg()?.id ?? null,
      projects: session.projects(),
      current: session.currentProject()?.id ?? null,
    }));
    const selectedProject = linkedSignal({
      source: selection,
      computation: (source, previous): string | null => {
        const previousId = previous?.source.org === source.org ? previous.value : null;
        const preferred = previousId ?? source.current;
        return (
          source.projects.find((row) => row.id === preferred)?.id ?? source.projects[0]?.id ?? null
        );
      },
    });
    const readable = computed(() => session.has('project:read'));
    const writable = computed(() => session.has('project:write'));
    const scope = computed<RetentionScope | undefined>(
      () => {
        const org = session.currentOrg()?.id;
        const project = selectedProject();
        return org && project && readable() && session.projects().some((row) => row.id === project)
          ? { org, project, writable: writable() }
          : undefined;
      },
      {
        equal: (left, right) =>
          left?.org === right?.org &&
          left?.project === right?.project &&
          left?.writable === right?.writable,
      },
    );
    const policyResource = resource({
      params: scope,
      loader: ({ params, abortSignal }) => data.read(params.project, abortSignal),
    });
    return { session, data, selectedProject, readable, writable, scope, policyResource };
  }),
  withComputed((store) => ({
    policy: computed(() =>
      store.scope() && store.policyResource.hasValue() ? store.policyResource.value() : null,
    ),
    loading: computed(() => store.policyResource.isLoading()),
    saving: computed(
      () => sameScope(store.write().scope, store.scope()) && store.write().phase === 'saving',
    ),
    saved: computed(
      () => sameScope(store.write().scope, store.scope()) && store.write().phase === 'saved',
    ),
    writeFailed: computed(
      () => sameScope(store.write().scope, store.scope()) && store.write().phase === 'error',
    ),
    writeError: computed(() =>
      sameScope(store.write().scope, store.scope()) ? store.write().error : null,
    ),
    readError: computed(() => {
      const error = store.policyResource.error();
      return isApiRequestError(error) ? error.error : null;
    }),
  })),
  withMethods((store) => ({
    selectProject(project: string): void {
      if (store.session.projects().some((row) => row.id === project) && !store.saving()) {
        store.selectedProject.set(project);
      }
    },
    reload(): void {
      if (!store.saving()) store.policyResource.reload();
    },
    async save(body: RetentionPolicy): Promise<boolean> {
      const scope = store.scope();
      if (!scope?.writable || !store.policy() || store.saving()) return false;
      patchState(store, { write: { scope, phase: 'saving', error: null } });
      try {
        const answer = await store.data.save(scope.project, body);
        if (!sameScope(scope, store.scope())) return false;
        store.policyResource.set(answer);
        patchState(store, { write: { scope, phase: 'saved', error: null } });
        return true;
      } catch (error) {
        // A programming error is a defect, not a server refusal: surface it instead of reporting a
        // write failure the retry copy would invite the user to repeat.
        if (!isApiRequestError(error)) throw error;
        if (sameScope(scope, store.scope())) {
          patchState(store, {
            write: {
              scope,
              phase: 'error',
              error: error.error,
            },
          });
        }
        return false;
      } finally {
        // Every path out of this attempt must stop it claiming to still be saving, including a
        // rethrown error and a write whose scope moved on while it was in flight.
        if (sameScope(store.write().scope, scope) && store.write().phase === 'saving')
          patchState(store, { write: { scope, phase: 'idle', error: null } });
      }
    },
  })),
);
