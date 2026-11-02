// The tenant the next request is scoped to: what `tenantOrgInterceptor` sends as `X-Evidgate-Org`.
// It is a slice of its own because the interceptor must inject it while `SessionStore` already
// depends on `HttpClient`; sharing that store would close a provider cycle. The session store owns
// every write — it publishes the first listed org after sign-in and follows the resolved route.

import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';

type TenantState = {
  /** The organization last confirmed by the server, or `null` when there is no tenant context. */
  readonly orgId: string | null;
};

const INITIAL_STATE: TenantState = { orgId: null };

const TenantStoreDefinition = signalStore(
  { providedIn: 'root' },
  withState<TenantState>(INITIAL_STATE),
  withMethods((store) => ({
    /** Publishes the organization the session (or the resolved route) confirmed. */
    setOrg(orgId: string): void {
      patchState(store, { orgId });
    },
    /** Forgets the tenant: no org list, no resolved scope, no session. */
    clear(): void {
      patchState(store, { orgId: null });
    },
  })),
);

/** The tenant store token and class; `providedIn: 'root'`, so the app has one tenant slice. */
export const TenantStore = TenantStoreDefinition;

/** The injected instance: its state signal plus the two writers above. */
export type TenantStore = InstanceType<typeof TenantStoreDefinition>;
