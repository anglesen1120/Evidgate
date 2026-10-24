// 09.1.1-T4.1 (plan 09.1.1 §10 T4; D-S68-Q1): the one seam between the shell and "what am I
// allowed to see", kept explicitly replaceable because V1 has no endpoint that answers it.
//
// No endpoint returns the caller's effective grants (02.3.1's `GET /v1/me` is the story that will
// add them). The only server source of permission strings today is the catalogue,
// `GET /v1/permissions` (02.2.1). `CataloguePermissionsSource` therefore answers with every code
// the catalogue marks `tenant_assignable`: the shell shows the whole catalogue's navigation until
// 02.3.1 replaces this binding with the caller's own grants.
//
// This is a convenience, never a gate: every request is authorised by the API pipeline, and a
// `403` from the server is the only refusal that counts. No role-to-permission mapping exists on
// the client — a code is either in the catalogue the server sent or it is not (00 §2).

import { inject, Injectable } from '@angular/core';

import { IdentityDataService } from '../data/identity-data.service';

/**
 * Reads the permission codes the shell may treat as granted (D-S68-Q1).
 *
 * The abstract class is the injection token: a later story provides
 * `{ provide: EffectivePermissionsSource, useClass: … }` and every consumer follows, with no edit
 * to `SessionStore`. It is `@Injectable()` only so the class is a valid token; nothing constructs
 * it, which is why `load` is abstract.
 */
@Injectable()
export abstract class EffectivePermissionsSource {
  /** Resolves the granted codes; a rejection means the grants could not be read at all. */
  abstract load(): Promise<ReadonlySet<string>>;
}

/**
 * The V1 default: the codes of the server's catalogue that a member principal may hold.
 *
 * `tenant_assignable` is the catalogue's own statement about which codes a member can be granted;
 * `reserved`/`sensitive` describe a code, they do not withhold it (00 §2, AC2/AC5). The result is
 * a plain set of server strings — nothing is derived from a role, and a code missing from the
 * catalogue is never invented.
 */
@Injectable({ providedIn: 'root' })
export class CataloguePermissionsSource implements EffectivePermissionsSource {
  private readonly identity = inject(IdentityDataService);

  async load(): Promise<ReadonlySet<string>> {
    const catalogue = await this.identity.listPermissions();
    const granted = new Set<string>();
    for (const entry of catalogue) {
      if (entry.tenant_assignable) {
        granted.add(entry.code);
      }
    }
    return granted;
  }
}
