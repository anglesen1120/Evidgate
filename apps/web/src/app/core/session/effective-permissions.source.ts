// The one seam between the shell and "what am I allowed to see", kept replaceable so a spec (or a
// later story) can bind another source without touching `SessionStore`.
//
// `ApiPermissionsSource` answers with the caller's own effective grants for the tenant the session
// published (`GET /v1/me/permissions`). A failed read rejects: the store renders "unavailable" with
// its retry, so an unreadable grant set is never mistaken for "everything granted" or for a
// silently empty workspace.
//
// This is a convenience, never a gate: every request is authorised by the API pipeline, and a
// `403` from the server is the only refusal that counts. No role-to-permission mapping exists on
// the client — the server's permission strings are used as they arrive.

import { forwardRef, inject, Injectable } from '@angular/core';

import { IdentityDataService } from '../data/identity-data.service';

import { TenantStore } from './tenant.store';

/**
 * Reads the permission codes the shell may treat as granted.
 *
 * The abstract class is the injection token; its root default is {@link ApiPermissionsSource}, and a
 * spec provides `{ provide: EffectivePermissionsSource, useValue: … }` to replace it. It is
 * `@Injectable` only so the class is a valid token; `load` is abstract.
 */
@Injectable({ providedIn: 'root', useExisting: forwardRef(() => ApiPermissionsSource) })
export abstract class EffectivePermissionsSource {
  /** Resolves the granted codes; a rejection means the grants could not be read at all. */
  abstract load(): Promise<ReadonlySet<string>>;
}

/**
 * The caller's grants in the current tenant. The tenant is read when the call starts, so an org
 * switch that happens while the answer is in flight cannot re-label it: the store drops a read a
 * newer scope resolution overtook, and the request itself names the org it was made for.
 *
 * No tenant (the caller has no organization yet) grants nothing and asks nothing: there is no
 * membership whose grants could be read.
 */
@Injectable({ providedIn: 'root' })
export class ApiPermissionsSource implements EffectivePermissionsSource {
  private readonly identity = inject(IdentityDataService);
  private readonly tenant = inject(TenantStore);

  async load(): Promise<ReadonlySet<string>> {
    const orgId = this.tenant.orgId();
    if (orgId === null) {
      return new Set();
    }
    const grants = await this.identity.getMyPermissions(orgId);
    return new Set(grants.permissions);
  }
}
