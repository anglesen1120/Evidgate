// The healing kill switch: the three stored scopes, the mode in force, and the one write path.
//
// The read is a `resource` keyed on the active org and project, so a switch aborts the read it
// replaces. A write carries one intent per attempt: the route remembers an `Idempotency-Key` for
// 24 h, so a fresh key for a retried action could apply it twice.
import { computed, inject, resource } from '@angular/core';
import type { HealingSwitchResolutionView, HealingSwitchSetV1 } from '@evidgate/sdk';
import {
  patchState,
  signalStore,
  withComputed,
  withMethods,
  withProps,
  withState,
} from '@ngrx/signals';

import { HealingSwitchDataService } from '../../core/data/healing-switch-data.service';
import { isApiRequestError, type ApiError } from '../../core/http/api-error';
import { createIntent, type Intent } from '../../core/http/intent';
import { SessionStore } from '../../core/session/session.store';

/** The scopes a switch row can name; `system` is read-only for a tenant. */
export type SwitchScope = 'system' | 'tenant' | 'project';

/** The modes this route accepts; `unavailable` is an observation, never a value to set. */
export type SettableMode = 'enabled' | 'shadow' | 'disabled';

/** The scopes this page can write; a system row is written only by the operator ingress. */
type WriteTarget = 'tenant' | 'project';

type ReadScope = { readonly org: string; readonly project: string };

type WriteState = {
  readonly scope: ReadScope | null;
  readonly target: WriteTarget | null;
  readonly phase: 'idle' | 'saving' | 'saved' | 'error';
  readonly error: ApiError | null;
  /** The attempt that wrote this state, so an older answer cannot claim it. */
  readonly attempt: number;
};

/** Widest scope first: the outermost row with the mode in force is the one that decides. */
const SCOPE_ORDER: readonly SwitchScope[] = ['system', 'tenant', 'project'];

// A -> B -> A project round trip builds a fresh scope object for the same pair, so a read or write
// answer is matched by the org and project it names, never by an object's identity.
const sameScope = (
  left: ReadScope | null | undefined,
  right: ReadScope | null | undefined,
): boolean => left?.org === right?.org && left?.project === right?.project;

/** The scope whose row carries the mode in force; `null` when no row does. */
function decider(resolution: HealingSwitchResolutionView | null): SwitchScope | null {
  if (resolution === null || resolution.resolved === 'unavailable') return null;
  const { system, tenant, project } = resolution;
  const rows: Record<SwitchScope, { mode: string } | null> = { system, tenant, project };
  return SCOPE_ORDER.find((scope) => rows[scope]?.mode === resolution.resolved) ?? null;
}

/**
 * The ancestor whose `disabled` row forbids setting `target`. `unavailable` is the platform's own
 * observation, so it names `system`: no tenant or project scope can lift it.
 */
function blocker(
  resolution: HealingSwitchResolutionView | null,
  target: WriteTarget,
): SwitchScope | null {
  if (resolution === null) return null;
  if (resolution.resolved === 'unavailable') return 'system';
  if (resolution.system?.mode === 'disabled') return 'system';
  return target === 'project' && resolution.tenant?.mode === 'disabled' ? 'tenant' : null;
}

/** The route refuses a tenant body that names a project, and a project body that omits one. */
function bodyFor(
  scope: ReadScope,
  target: WriteTarget,
  mode: SettableMode,
  reason: string | null,
): HealingSwitchSetV1 {
  const why = reason === null ? {} : { reason };
  return target === 'project'
    ? { scope: 'project', project_id: scope.project, mode, ...why }
    : { scope: 'tenant', mode, ...why };
}

export const HealingSwitchStore = signalStore(
  withState<{ write: WriteState }>({
    write: { scope: null, target: null, phase: 'idle', error: null, attempt: 0 },
  }),
  withProps(() => {
    const session = inject(SessionStore);
    const data = inject(HealingSwitchDataService);
    const scope = computed<ReadScope | undefined>(
      () => {
        const org = session.currentOrg()?.id;
        const project = session.currentProject()?.id;
        return org !== undefined && project !== undefined && session.has('healing:triage')
          ? { org, project }
          : undefined;
      },
      { equal: (left, right) => sameScope(left, right) },
    );
    const switchResource = resource({
      params: scope,
      loader: ({ params, abortSignal }) => data.read(params.project, abortSignal),
    });
    return { session, data, scope, switchResource };
  }),
  // Not state: nothing renders these. `latest` names, per scope and target, the newest attempt, so
  // an answer for a superseded attempt is dropped; `intents` keeps a transport-failed key.
  withProps(() => ({
    attempts: { next: 0, latest: new Map<string, number>() },
    intents: new Map<string, { readonly signature: string; readonly intent: Intent }>(),
  })),
  withComputed((store) => ({
    resolution: computed(() =>
      store.scope() !== undefined && store.switchResource.hasValue()
        ? store.switchResource.value()
        : null,
    ),
    loading: computed(() => store.scope() !== undefined && store.switchResource.isLoading()),
    readError: computed(() => {
      const error = store.switchResource.error();
      return isApiRequestError(error) ? error.error : null;
    }),
    canConfigure: computed(() => store.session.has('healing:configure')),
  })),
  withComputed((store) => {
    const current = computed(() => sameScope(store.write().scope, store.scope()));
    const failed = computed(() =>
      current() && store.write().phase === 'error' ? store.write().error : null,
    );
    return {
      saving: computed(() =>
        current() && store.write().phase === 'saving' ? store.write().target : null,
      ),
      saved: computed(() =>
        current() && store.write().phase === 'saved' ? store.write().target : null,
      ),
      // A 404 is the one answer that never says whether the id exists: render "not found", with no
      // hint about another tenant's project.
      notFound: computed(
        () => store.readError()?.kind === 'not_found' || failed()?.kind === 'not_found',
      ),
      decidingScope: computed(() => decider(store.resolution())),
    };
  }),
  withMethods((store) => ({
    reload(): void {
      if (store.scope() !== undefined) store.switchResource.reload();
    },
    /** The scope that forbids setting `target` right now, or `null` when it is settable. */
    blockedBy(target: WriteTarget): SwitchScope | null {
      return blocker(store.resolution(), target);
    },
    writeError(target: WriteTarget): ApiError | null {
      const write = store.write();
      return write.target === target &&
        write.phase === 'error' &&
        sameScope(write.scope, store.scope())
        ? write.error
        : null;
    },
    async save(target: WriteTarget, mode: SettableMode, reason: string | null): Promise<boolean> {
      const scope = store.scope();
      const resolution = store.resolution();
      if (scope === undefined || resolution === null || !store.canConfigure()) return false;
      if (store.saving() !== null || blocker(resolution, target) !== null) return false;

      const attempt = ++store.attempts.next;
      const key = `${scope.org}\u0000${scope.project}\u0000${target}`;
      store.attempts.latest.set(key, attempt);
      const owned = (): boolean =>
        store.attempts.latest.get(key) === attempt && sameScope(scope, store.scope());

      // The same user action retries with its original key after a transport failure, because the
      // server may have applied the request nobody saw answered; any other outcome starts fresh.
      const signature = `${mode}\u0000${reason ?? ''}`;
      const pending = store.intents.get(key);
      const intent =
        pending !== undefined && pending.signature === signature ? pending.intent : createIntent();
      store.intents.set(key, { signature, intent });

      patchState(store, { write: { scope, target, phase: 'saving', error: null, attempt } });
      try {
        await store.data.set(bodyFor(scope, target, mode, reason), intent);
        store.intents.delete(key);
        if (!owned()) return false;
        patchState(store, { write: { scope, target, phase: 'saved', error: null, attempt } });
        // The answer's row version only matters once the resolution is re-read: the decider and the
        // ancestor reads decide the controls, so a stale resolution would offer a wrong write.
        store.switchResource.reload();
        return true;
      } catch (error) {
        // A programming error is a defect, not a server refusal: surface it instead of reporting a
        // write failure whose retry copy invites the user to repeat the request.
        if (!isApiRequestError(error)) throw error;
        const failure = error.error;
        if (failure.kind !== 'network' && failure.kind !== 'timeout') store.intents.delete(key);
        if (!owned()) return false;
        patchState(store, { write: { scope, target, phase: 'error', error: failure, attempt } });
        if (failure.kind === 'precondition_failed') store.switchResource.reload();
        return false;
      } finally {
        // Every path out of this attempt stops it claiming to be saving, including a rethrown error
        // and a write whose scope moved on while it was in flight.
        const write = store.write();
        if (write.attempt === attempt && write.phase === 'saving') {
          patchState(store, { write: { scope, target, phase: 'idle', error: null, attempt } });
        }
      }
    },
  })),
);
