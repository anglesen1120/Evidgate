// 09.7.1-T2 (plan 09.7.1 §5 members row, §6 Membership/Invite rows; 02.1.2 §4/§5): the org
// membership, the role list and the live permission catalogue the Members and roles tab reads, and
// the mutations it performs — invite by address, the invite's own lifecycle (rotate the token,
// cancel the invite), remove, and the per-role grant/revoke pair the edit roles dialog applies
// (09.7.1-T2's edit-roles row, `F6`).
//
// An invited token (`invite_token`) is the one plaintext secret these operations carry: `invite`
// and `resendInvite` answer it ONCE, as the request's return value, and this layer never stores,
// caches or logs it — the caller hands it to the UI's one-time reveal and drops it.
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
// `grantRole` and `revokeRole` take ONE role each, because the contract does: a role change is one
// request with one key, so the dialog mints a key per role (a retried grant replays that key, and
// two roles can never share one). Ordering and stopping at the first refusal belong to the store,
// which owns the dialog's attempt.

import { inject, Injectable } from '@angular/core';
import {
  cancelMembershipInvite,
  createMembership,
  getMembership,
  grantMembershipRole,
  listMemberships,
  listRoles,
  removeMember,
  resendMembershipInvite,
  revokeMembershipRole,
  type InviteIssuedView,
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
   * Invite a member (`POST /v1/orgs/{o}/memberships`, `02.1.2-T2 §5`). The membership starts
   * `pending` and the `201` body also carries `invite_token`: the plaintext link token, served
   * once by the request that minted it. The server answers a second invite while one is pending
   * with 409 `invite_exists`, which reaches the caller as the typed `conflict` failure with that
   * code.
   *
   * The token is a return value and nothing else — this layer does not keep it, cache it or log
   * it, and the response's `Cache-Control: no-store` is the server's half of that rule.
   */
  invite(orgId: string, body: InviteMemberRequest, intent: Intent): Promise<InviteIssuedView> {
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
   * Rotate a pending invite's token (`POST /v1/orgs/{o}/memberships/{m}/resend`, `02.1.2 §5`):
   * `202` with the membership and the NEW `invite_token`, which the previous token stops
   * answering for. A membership that is not `pending` is 409 `invite_not_pending` and an id of
   * another org is 404 — both arrive as the typed failure carrying the server's message and code.
   * An `Idempotency-Key` replay answers the stored body with `invite_token` null, so a retried
   * rotate never shows a token twice.
   */
  resendInvite(orgId: string, membershipId: string, intent: Intent): Promise<InviteIssuedView> {
    return this.gateway.call((client) =>
      resendMembershipInvite({
        client,
        path: { o: orgId, m: membershipId },
        headers: intentHeaders(intent),
      }),
    );
  }

  /**
   * Cancel a pending invite (`POST /v1/orgs/{o}/memberships/{m}/cancel-invite`, `02.1.2 §6`):
   * `204`, the row becomes `revoked` and its stored prefix stops resolving. A membership that is
   * not `pending` is 409 `invite_not_pending`; both that and an id of another org (404) arrive as
   * the typed failure, never as a value this layer could paper over.
   */
  async cancelInvite(orgId: string, membershipId: string, intent: Intent): Promise<void> {
    await this.gateway.call((client) =>
      cancelMembershipInvite({
        client,
        path: { o: orgId, m: membershipId },
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
   * One membership (`GET /v1/orgs/{o}/memberships/{m}`), with the role set the server currently
   * holds — the row the edit-roles dialog replaces in the list after a grant or a revoke, so the
   * matrix shows the server's answer instead of the dialog's draft.
   *
   * It answers an `ETag`, which the SDK surfaces as a header and no caller needs here: neither
   * role route takes `If-Match` (the server's guard is the role set, not the row version).
   */
  getMembership(orgId: string, membershipId: string): Promise<MembershipView> {
    return this.gateway.call((client) =>
      getMembership({ client, path: { o: orgId, m: membershipId } }),
    );
  }

  /**
   * Grant one role (`POST /v1/orgs/{o}/memberships/{m}/roles`, body `{ role_id }`): `200` with the
   * membership's new role set. The write is idempotent by `unique (membership_id, role_id)`, so a
   * grant of a role the membership already holds answers the current state rather than creating
   * anything. A role the tenant cannot see is 409 `unknown_role`, and a grant that would widen the
   * caller's own permissions is 403 `forbidden` (02.1.2 §3b self-escalation) — both arrive as the
   * typed failure carrying the server's message and code.
   */
  grantRole(
    orgId: string,
    membershipId: string,
    roleId: string,
    intent: Intent,
  ): Promise<MembershipView> {
    return this.gateway.call((client) =>
      grantMembershipRole({
        client,
        path: { o: orgId, m: membershipId },
        body: { role_id: roleId },
        headers: intentHeaders(intent),
      }),
    );
  }

  /**
   * Revoke one role (`DELETE /v1/orgs/{o}/memberships/{m}/roles/{r}`): `204`, nothing to read.
   * Revoking `tenant_admin` from the org's last active admin is 409 `last_admin` (the same guard as
   * removal, AC2), and a pair that was never granted is 404 — both typed, never a value.
   */
  async revokeRole(
    orgId: string,
    membershipId: string,
    roleId: string,
    intent: Intent,
  ): Promise<void> {
    await this.gateway.call((client) =>
      revokeMembershipRole({
        client,
        path: { o: orgId, m: membershipId, r: roleId },
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
