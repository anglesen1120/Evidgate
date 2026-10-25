// 09.7.1-T2 (plan 09.7.1 §5 members row, §6 Membership/Invite rows; 02.1.2 §4/§5): the org
// membership, the role list and the live permission catalogue the Members and roles tab reads, and
// the two mutations it performs — invite by address and remove.
//
// It lives in `core/data` with the rest of the data services because `apps/web/lint` owns the HTTP
// boundary there: a page, store or component never imports an `@evidgate/sdk` operation (the
// `no-http-outside-core` rule), it calls one of these instead. The pattern is
// `runs-data.service.ts`: the generated SDK operation is called with the app's one client,
// `ApiGateway.call` decides success versus typed failure, and no wire type is declared here —
// every signature is an SDK type.
//
// The `Intent` is the caller's: the dialog mints one with `createIntent()` when the user presses
// the button and passes the same value to every retry, so the SDK `headers` carry the
// caller-owned `Idempotency-Key` the server replays on (00 §9.1, ADR-0008 rule 2). Both mutations
// require that header, so the signature takes an `Intent` rather than inventing a key per call.
//
// The role grant/revoke operations (`grantMembershipRole`, `revokeMembershipRole`) are NOT exposed:
// the plan's rows need the matrix (read), the invite (with optional initial roles) and the remove.
// Adding them here would put an unused capability on the UI's reach with no screen behind it.

import { inject, Injectable } from '@angular/core';
import {
  createMembership,
  listMemberships,
  listRoles,
  removeMember,
  type InviteMemberRequest,
  type MembershipListResponse,
  type MembershipView,
  type PermissionView,
  type RoleView,
} from '@evidgate/sdk';

import { ApiGateway } from '../http/api-gateway';
import { intentHeaders, type Intent } from '../http/intent';
import { IdentityDataService, MAX_CURSOR_PAGES } from './identity-data.service';

@Injectable({ providedIn: 'root' })
export class MembersDataService {
  private readonly gateway = inject(ApiGateway);
  private readonly identity = inject(IdentityDataService);

  /**
   * One page of the org's memberships (`GET /v1/orgs/{o}/memberships`, 00 §9.1 cursor page).
   * The page is returned as served, so the caller owns the cursor loop — the Members store keeps
   * the rows it already renders while the next page is on its way, and a broken cursor costs the
   * page it was following rather than the list on screen.
   */
  listMemberships(orgId: string, cursor?: string): Promise<MembershipListResponse> {
    return this.gateway.call((client) =>
      listMemberships({
        client,
        path: { o: orgId },
        query: cursor === undefined ? {} : { cursor },
      }),
    );
  }

  /**
   * Every role the caller can see — the 00 §2 built-ins plus the org's own rows
   * (`GET /v1/roles`, 02.2.1) — following `next_cursor` to the last page. The matrix needs the
   * whole list to label its columns, so a first page would be a silently short table.
   *
   * The loop is bounded by {@link MAX_CURSOR_PAGES} and refuses a cursor it already followed: both
   * are contract violations rather than a second page, and a thrown `Error` (not an
   * `ApiRequestError`) is what tells the store "the read did not complete" instead of handing it a
   * partial column set.
   */
  listRoles(): Promise<readonly RoleView[]> {
    return this.readAllPages<RoleView>('the role list', async (cursor) => {
      const page = await this.gateway.call((client) =>
        listRoles({ client, query: cursor === undefined ? {} : { cursor } }),
      );
      return [page.items, page.next_cursor];
    });
  }

  /**
   * The live permission catalogue (`GET /v1/permissions`, 02.2.1), already read page by page by
   * `IdentityDataService`. The role scope preview joins the built-in grant codes against it for the
   * description and the `sensitive` flag, so it must be the server's catalogue and never a copy.
   */
  listPermissions(): Promise<readonly PermissionView[]> {
    return this.identity.listPermissions();
  }

  /**
   * Invite a member (`POST /v1/orgs/{o}/memberships`). The membership starts `pending`; the server
   * answers a second invite while one is pending with 409 `invite_exists`, which reaches the caller
   * as the typed `conflict` failure with that code.
   */
  invite(orgId: string, body: InviteMemberRequest, intent: Intent): Promise<MembershipView> {
    return this.gateway.call((client) =>
      createMembership({
        client,
        path: { o: orgId },
        body,
        headers: intentHeaders(intent),
      }),
    );
  }

  /**
   * Remove a member (`DELETE /v1/orgs/{o}/memberships/{m}`): the row is kept as audit history and
   * its status becomes `removed`. Removing the org's last active `tenant_admin` is the server's 409
   * `last_admin` (AC2) — it arrives as the typed failure carrying the server's message and code,
   * never as a value this layer could paper over.
   */
  async remove(orgId: string, membershipId: string, intent: Intent): Promise<void> {
    await this.gateway.call((client) =>
      removeMember({
        client,
        path: { o: orgId, m: membershipId },
        headers: intentHeaders(intent),
      }),
    );
  }

  /**
   * Follows a 00 §9.1 keyset cursor to the last page. `fetchPage` answers one page as
   * `[items, nextCursor]`, and a `null` cursor ends the read. Same loop and same bounds as
   * `IdentityDataService`: the helper is private there, and a second copy of ten lines with the
   * same two refusals is cheaper to read than a shared base class for two services.
   */
  private async readAllPages<TItem>(
    label: string,
    fetchPage: (cursor: string | undefined) => Promise<readonly [readonly TItem[], string | null]>,
  ): Promise<readonly TItem[]> {
    const items: TItem[] = [];
    const requested = new Set<string>();
    let cursor: string | undefined;
    for (let page = 0; page < MAX_CURSOR_PAGES; page += 1) {
      const [pageItems, nextCursor] = await fetchPage(cursor);
      items.push(...pageItems);
      if (nextCursor === null) {
        return items;
      }
      if (requested.has(nextCursor)) {
        throw new Error(`${label} repeated cursor "${nextCursor}"`);
      }
      requested.add(nextCursor);
      cursor = nextCursor;
    }
    throw new Error(`${label} did not finish within ${MAX_CURSOR_PAGES} pages`);
  }
}
