import { computed, inject, linkedSignal, resource } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import type { ApiTokenCreatedResponse, ApiTokenView, CreateApiTokenRequest } from '@evidgate/sdk';
import {
  patchState,
  signalStore,
  withComputed,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';
import { ApiTokensService } from '../../../core/data/api-tokens-data.service';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import type { Intent } from '../../../core/http/intent';
import { SessionStore } from '../../../core/session/session.store';

type Scope = { readonly org: string; readonly project: string };
export type ApiTokensPhase = 'loading' | 'empty' | 'success' | 'error' | 'forbidden';
type WriteAttempt = {
  scope: Scope | null;
  phase: 'idle' | 'submitting' | 'failed';
  error: ApiError | null;
};
const idleAttempt = (): WriteAttempt => ({ scope: null, phase: 'idle', error: null });

// Explicit projection keeps a malformed list response's extra credential fields out of signals.
function metadata(row: ApiTokenView): ApiTokenView {
  return {
    id: row.id,
    name: row.name,
    prefix: row.prefix,
    permissions: [...row.permissions],
    status: row.status,
    expires_at: row.expires_at,
    last_used_at: row.last_used_at,
    created_by: row.created_by,
  };
}

export const ApiTokensStore = signalStore(
  withState<{
    createScope: Scope | null;
    createAttempt: WriteAttempt;
    revokeAttempt: WriteAttempt;
  }>({
    createScope: null,
    createAttempt: idleAttempt(),
    revokeAttempt: idleAttempt(),
  }),
  withProps(() => {
    const session = inject(SessionStore);
    const data = inject(ApiTokensService);
    const route = inject(ActivatedRoute, { optional: true });
    const query = route ? toSignal(route.queryParamMap) : undefined;
    const selection = computed(() => ({
      org: session.currentOrg()?.id,
      projects: session.projects(),
      current: session.currentProject()?.id,
      requested: query?.()?.get('project') ?? null,
    }));
    const selectedProject = linkedSignal({
      source: selection,
      computation: (source, previous): string | null => {
        const preferred =
          source.requested && source.requested !== previous?.source.requested
            ? source.requested
            : previous && previous.source.org === source.org
              ? previous.value
              : (source.requested ?? source.current);
        return (
          source.projects.find((row) => row.id === preferred)?.id ?? source.projects[0]?.id ?? null
        );
      },
    });
    const canRead = computed(() => session.has('api_token:read'));
    const canWrite = computed(() => session.has('api_token:write'));
    const scope = computed<Scope | undefined>(
      () => {
        const org = session.currentOrg()?.id;
        const project = selectedProject();
        return org && project && canRead() && session.projects().some((row) => row.id === project)
          ? { org, project }
          : undefined;
      },
      { equal: (a, b) => a?.org === b?.org && a?.project === b?.project },
    );
    const cursor = linkedSignal({
      source: () => ({
        scope: scope(),
        project: query?.()?.get('project'),
        cursor: query?.()?.get('cursor') ?? undefined,
      }),
      computation: (source): string | undefined =>
        source.project === source.scope?.project ? source.cursor : undefined,
    });
    const pageResource = resource({
      params: () => {
        const active = scope();
        return active ? { ...active, cursor: cursor() } : undefined;
      },
      loader: async ({ params, abortSignal }) => {
        const page = await data.list(params.project, params.cursor, abortSignal);
        return { items: page.items.map(metadata), next_cursor: page.next_cursor };
      },
    });
    return { session, data, selectedProject, canRead, canWrite, scope, cursor, pageResource };
  }),
  withComputed((store) => ({
    rows: computed(() =>
      store.scope() && store.pageResource.hasValue() ? store.pageResource.value().items : [],
    ),
    loadError: computed(() => {
      if (!store.scope()) return null;
      const error = store.pageResource.error();
      return isApiRequestError(error) ? error.error : null;
    }),
    hasNext: computed(() =>
      store.scope() && store.pageResource.hasValue()
        ? !!store.pageResource.value().next_cursor
        : false,
    ),
    // The response belongs to the open dialog, even if project selection or grants change.
    createOpen: computed(() => store.createScope() !== null),
    creating: computed(() => store.createAttempt().phase === 'submitting'),
    revoking: computed(() => store.revokeAttempt().phase === 'submitting'),
    // The failure belongs to the dialog's scope, so a selection that moves under an open dialog
    // cannot hide the server message from the person who submitted it.
    createError: computed(() =>
      store.createAttempt().scope === store.createScope() ? store.createAttempt().error : null,
    ),
    revokeError: computed(() =>
      store.revokeAttempt().scope === store.scope() ? store.revokeAttempt().error : null,
    ),
    phase: computed<ApiTokensPhase>(() => {
      if (!store.canRead()) return 'forbidden';
      if (!store.scope()) return 'empty';
      if (store.pageResource.isLoading()) return 'loading';
      const error = store.pageResource.error();
      if (error)
        return isApiRequestError(error) && error.error.kind === 'forbidden' ? 'forbidden' : 'error';
      return store.pageResource.hasValue() && store.pageResource.value().items.length > 0
        ? 'success'
        : 'empty';
    }),
  })),
  withMethods((store) => ({
    selectProject(project: string): void {
      if (store.session.projects().some((row) => row.id === project))
        store.selectedProject.set(project);
    },
    setCursor(cursor: string | undefined): void {
      store.cursor.set(cursor);
    },
    next(): void {
      if (!store.pageResource.isLoading() && store.pageResource.hasValue()) {
        const cursor = store.pageResource.value().next_cursor;
        if (cursor && cursor !== store.cursor()) store.cursor.set(cursor);
      }
    },
    reload(): void {
      store.pageResource.reload();
    },
    // A mutation changes the first page, so the next read starts there: an old cursor can name a
    // page the server no longer accepts once a row moves.
    reloadFirst(): void {
      if (store.cursor() === undefined) store.pageResource.reload();
      else store.cursor.set(undefined);
    },
    openCreate(): void {
      const scope = store.scope();
      if (scope && store.canWrite()) patchState(store, { createScope: scope });
    },
    closeCreate(): void {
      patchState(store, { createScope: null });
    },
  })),
  withMethods((store) => ({
    resetCreateError(): void {
      if (!store.creating()) patchState(store, { createAttempt: idleAttempt() });
    },
    async create(
      body: CreateApiTokenRequest,
      intent: Intent,
    ): Promise<ApiTokenCreatedResponse | null> {
      // The dialog opened for one scope and owns the only copy of the secret it receives, so the
      // token is minted where the dialog was opened, not wherever the selection moved since.
      const scope = store.createScope() ?? store.scope();
      if (!scope || !store.canWrite() || store.creating() || store.revoking()) return null;
      patchState(store, { createAttempt: { scope, phase: 'submitting', error: null } });
      try {
        const answer = await store.data.create(scope.project, body, intent);
        if (scope === store.scope()) store.reloadFirst();
        return answer;
      } catch (error) {
        if (!isApiRequestError(error)) throw error;
        // `createError` reads this attempt only while the dialog that opened for `scope` is up, so
        // the refusal reaches the person who submitted it even if the selection moved meanwhile.
        patchState(store, { createAttempt: { scope, phase: 'failed', error: error.error } });
        return null;
      } finally {
        if (store.createAttempt().phase === 'submitting')
          patchState(store, { createAttempt: idleAttempt() });
      }
    },
  })),
  withMethods((store) => ({
    resetRevokeError(): void {
      if (!store.revoking()) patchState(store, { revokeAttempt: idleAttempt() });
    },
    async revoke(id: string, intent: Intent): Promise<boolean> {
      const scope = store.scope();
      if (!scope || !store.canWrite() || store.creating() || store.revoking()) return false;
      patchState(store, { revokeAttempt: { scope, phase: 'submitting', error: null } });
      try {
        await store.data.revoke(id, intent);
        if (scope !== store.scope() || !store.canWrite()) return false;
        // The DELETE is bodiless. Re-read metadata rather than inventing a response status.
        store.reloadFirst();
        return true;
      } catch (error) {
        if (!isApiRequestError(error)) throw error;
        if (scope === store.scope()) {
          patchState(store, { revokeAttempt: { scope, phase: 'failed', error: error.error } });
          if (error.error.kind === 'not_found' || error.error.kind === 'conflict')
            store.reloadFirst();
        }
        return false;
      } finally {
        if (store.revokeAttempt().phase === 'submitting')
          patchState(store, { revokeAttempt: idleAttempt() });
      }
    },
  })),
);
