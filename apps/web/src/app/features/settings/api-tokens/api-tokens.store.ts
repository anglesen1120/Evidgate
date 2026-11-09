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
/** The project a create dialog was opened for, kept so a moved selection cannot relabel it. */
export type ApiTokenCreateTarget = { readonly projectId: string; readonly projectName: string };
type CreateOrigin = { readonly scope: Scope; readonly target: ApiTokenCreateTarget };
export type ApiTokensPhase = 'loading' | 'empty' | 'success' | 'error' | 'forbidden';
type WriteAttempt = {
  scope: Scope | null;
  phase: 'idle' | 'submitting' | 'failed';
  error: ApiError | null;
};
const idleAttempt = (): WriteAttempt => ({ scope: null, phase: 'idle', error: null });
// A round trip org A -> B -> A builds a fresh scope object for the same list, so an answer that
// belongs to A is still A's: name the list by its values, never by an object's identity.
const sameScope = (left: Scope | null | undefined, right: Scope | null | undefined): boolean =>
  left?.org === right?.org && left?.project === right?.project;

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
    createOrigin: CreateOrigin | null;
    createAttempt: WriteAttempt;
    revokeAttempt: WriteAttempt;
  }>({
    createOrigin: null,
    createAttempt: idleAttempt(),
    revokeAttempt: idleAttempt(),
  }),
  withProps((store) => {
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
      { equal: sameScope },
    );
    const createTarget = computed(() => store.createOrigin()?.target ?? null);
    // The page the user's Next asked for while its navigation is still in flight. Plain state, not
    // a signal: only a URL emission re-runs the computation below, and that is when it is read.
    const move: { held: string | undefined } = { held: undefined };
    const cursor = linkedSignal({
      source: () => ({
        scope: scope(),
        project: query?.()?.get('project'),
        cursor: query?.()?.get('cursor') ?? undefined,
      }),
      // A URL that still names the cursor it named before is the page's own readback of a page the
      // store has since retired (a create or revoke restarted the list), not a new request. A page
      // the user moved to is not that: its navigation never landed, so the landing keeps the URL.
      computation: (source, previous): string | undefined => {
        if (source.project !== source.scope?.project) return undefined;
        // A re-emission naming the cursor the URL already named is not a navigation: while Next is
        // in flight it must not hand the page back to the entry the user is leaving.
        if (
          move.held !== undefined &&
          previous?.value === move.held &&
          previous.source.cursor === source.cursor &&
          previous.source.project === source.project &&
          sameScope(previous.source.scope, source.scope)
        )
          return move.held;
        move.held = undefined;
        const staleRetirement =
          previous !== undefined &&
          previous.value === undefined &&
          previous.source.cursor === source.cursor &&
          previous.source.project === source.project &&
          sameScope(previous.source.scope, source.scope);
        return staleRetirement ? undefined : source.cursor;
      },
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
    return {
      session,
      data,
      query,
      selectedProject,
      canRead,
      canWrite,
      scope,
      createTarget,
      cursor,
      move,
      pageResource,
    };
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
    // The dialog belongs to the project it was opened on, even if the selection moves under it.
    createOpen: computed(() => store.createOrigin() !== null),
    creating: computed(() => store.createAttempt().phase === 'submitting'),
    revoking: computed(() => store.revokeAttempt().phase === 'submitting'),
    // The failure belongs to the dialog's scope, so a selection that moves under an open dialog
    // cannot hide the server message from the person who submitted it.
    createError: computed(() =>
      sameScope(store.createAttempt().scope, store.createOrigin()?.scope)
        ? store.createAttempt().error
        : null,
    ),
    revokeError: computed(() =>
      sameScope(store.revokeAttempt().scope, store.scope()) ? store.revokeAttempt().error : null,
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
      store.move.held = undefined;
      store.cursor.set(cursor);
    },
    next(): void {
      if (!store.pageResource.isLoading() && store.pageResource.hasValue()) {
        const cursor = store.pageResource.value().next_cursor;
        if (cursor && cursor !== store.cursor()) {
          store.cursor.set(cursor);
          store.move.held = cursor;
        }
      }
    },
    // The navigation a Next started has landed, failed or been overtaken: the URL leads again.
    settleMove(): void {
      store.move.held = undefined;
    },
    reload(): void {
      store.pageResource.reload();
    },
    // A mutation changes the first page, so the next read starts there: an old cursor can name a
    // page the server no longer accepts once a row moves.
    reloadFirst(): void {
      store.move.held = undefined;
      if (store.cursor() === undefined) store.pageResource.reload();
      else store.cursor.set(undefined);
    },
    // The dialog names the project it was opened for, so a moved selection leaves it truthful
    // instead of closing it with the only copy of a minted secret.
    openCreate(): void {
      const scope = store.scope();
      if (!scope || !store.canWrite()) return;
      const name = store.session.projects().find((row) => row.id === scope.project)?.name;
      patchState(store, {
        createOrigin: {
          scope,
          target: { projectId: scope.project, projectName: name ?? scope.project },
        },
      });
    },
    closeCreate(): void {
      patchState(store, { createOrigin: null });
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
      const scope = store.createOrigin()?.scope ?? store.scope();
      if (!scope || !store.canWrite() || store.creating() || store.revoking()) return null;
      patchState(store, { createAttempt: { scope, phase: 'submitting', error: null } });
      try {
        const answer = await store.data.create(scope.project, body, intent);
        if (sameScope(scope, store.scope())) store.reloadFirst();
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
        if (!sameScope(scope, store.scope()) || !store.canWrite()) return false;
        // The DELETE is bodiless. Re-read metadata rather than inventing a response status.
        store.reloadFirst();
        return true;
      } catch (error) {
        if (!isApiRequestError(error)) throw error;
        if (sameScope(scope, store.scope())) {
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
