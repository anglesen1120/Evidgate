// The invite lifecycle of the Members and roles tab — minting an invite, rotating its token and
// cancelling it — as a collaborator of the store rather than a second store: it owns the rotate's
// double-submit lock and writes state only through the ports the store hands it.

import type { MembersDataService } from '../../../core/data/members-data.service';
import type { Intent } from '../../../core/http/intent';
import { mutationFailure, refusalOf, SUBMITTED, SUBMITTING } from './members.state';
import type { IssuedInvite, MutationState } from './members.types';

/** The server's code for "this membership holds no pending invitation" (resend and cancel). */
const INVITE_NOT_PENDING = 'invite_not_pending';

/**
 * The one-time plaintext of an invite answer as the dialogs must read it: a NON-EMPTY string is
 * the token; `null`, `''` or a non-string a broken adapter let through is ABSENCE, which makes the
 * dialogs offer Resend instead of revealing nothing. The value is returned, never stored.
 */
function issuedInviteToken(value: string | null | undefined): string | null {
  return typeof value === 'string' && value.length > 0 ? value : null;
}

/** What the flow needs from its store: the data service, the scope on screen and its writers. */
export interface MembersInvitePorts {
  readonly data: MembersDataService;
  /** The org whose list is on screen right now; `null` before the first load. */
  readonly currentOrgId: () => string | null;
  readonly publishInvite: (state: MutationState) => void;
  readonly publishResend: (state: MutationState) => void;
  readonly publishCancel: (state: MutationState) => void;
  /** Re-reads the list from its first page; the store's `load`. */
  readonly reload: (orgId: string) => Promise<void>;
  /** Re-reads one row from the server; the store's member refresh. */
  readonly refreshMember: (orgId: string, membershipId: string) => Promise<void>;
}

export class MembersInviteFlow {
  private readonly ports: MembersInvitePorts;

  /**
   * The rotate's double-submit lock. It decides whether a rotate may start, NOT the published
   * `resendState()` phase: the state becomes `done` as soon as the server answers (the token is in
   * the caller's hands), while this stays closed until the row refresh settles — two rotates would
   * mint two tokens and invalidate the one the user is looking at. Closing a dialog resets the
   * published state and deliberately leaves this flag alone; only the work settling releases it.
   */
  private resendInFlight = false;

  constructor(ports: MembersInvitePorts) {
    this.ports = ports;
  }

  /**
   * Invites one address (`POST /v1/orgs/{o}/memberships`) and reloads the list from its first page
   * AFTER answering: the plaintext is served once, so a dialog that closes while the list is being
   * re-read must not lose it, and a Resend would invalidate the very token this call minted. The
   * body carries `roles` only when one was selected, and the address is never stored.
   *
   * @returns the minted invite, or `null` when no org is loaded, the server refused, or the
   *   rejection was not the server's answer (reported as the generic `unexpected` failure).
   */
  async invite(
    email: string,
    roleIds: readonly string[],
    intent: Intent,
  ): Promise<IssuedInvite | null> {
    const orgId = this.ports.currentOrgId();
    if (orgId === null) {
      return null;
    }
    this.ports.publishInvite(SUBMITTING);
    let issued: IssuedInvite;
    try {
      const view = await this.ports.data.invite(
        orgId,
        roleIds.length === 0 ? { email } : { email, roles: [...roleIds] },
        intent,
      );
      // Built here and returned, never written to a signal: this local is the only place the
      // plaintext exists inside the store.
      issued = { membershipId: view.id, inviteToken: issuedInviteToken(view.invite_token) };
    } catch (error) {
      this.ports.publishInvite({ phase: 'failed', error: mutationFailure(error) });
      return null;
    }
    this.ports.publishInvite(SUBMITTED);
    // Behind the answer, never in front of it — and only for the org this call addressed, because
    // `load` would otherwise reset the list another org's own read already put there.
    if (this.ports.currentOrgId() === orgId) {
      void this.ports.reload(orgId);
    }
    return issued;
  }

  /**
   * Rotates one pending invite (`POST …/memberships/{m}/resend`) and re-reads that row AFTER
   * answering: the new `invite_token` is a return value, so closing the reveal while the read is on
   * its way cannot lose it. A 409 `invite_not_pending` lands in the store's `resendState` verbatim,
   * and re-reads the list once, because the pending row on screen is then stale.
   *
   * The double-submit guard is {@link resendInFlight}, not the published phase: a second press in
   * that window answers `null` instead of minting a second token.
   *
   * @returns the rotated invite; `null` on a refusal, a non-server rejection, or the double submit.
   */
  async resend(membershipId: string, intent: Intent): Promise<IssuedInvite | null> {
    if (this.resendInFlight) {
      return null;
    }
    const orgId = this.ports.currentOrgId();
    if (orgId === null) {
      return null;
    }
    this.resendInFlight = true;
    this.ports.publishResend(SUBMITTING);
    let issued: IssuedInvite;
    try {
      const view = await this.ports.data.resendInvite(orgId, membershipId, intent);
      issued = { membershipId: view.id, inviteToken: issuedInviteToken(view.invite_token) };
    } catch (error) {
      const refusal = refusalOf(error);
      this.ports.publishResend({ phase: 'failed', error: mutationFailure(error) });
      this.resendInFlight = false;
      // "No pending invitation" means the row the user just pressed is stale: one re-read drops it,
      // the refusal stays exactly as it arrived, and the read cannot turn it into a success.
      if (refusal?.code === INVITE_NOT_PENDING && this.ports.currentOrgId() === orgId) {
        await this.ports.reload(orgId);
      }
      return null;
    }
    this.ports.publishResend(SUBMITTED);
    if (this.ports.currentOrgId() === orgId) {
      this.releaseResendWhenSettled(this.ports.refreshMember(orgId, membershipId));
    } else {
      // Another org's list is on screen: no refresh read, but the lock still has to open.
      this.resendInFlight = false;
    }
    return issued;
  }

  /**
   * Cancels one pending invite (`POST …/memberships/{m}/cancel-invite`): the row becomes `revoked`,
   * so the list is reloaded rather than patched. A 409 `invite_not_pending` lands in the store's
   * `cancelState` verbatim and re-reads the list once, because that pending row is then stale.
   *
   * @returns `true` when the server cancelled it and this org's list is still on screen; `false` on
   *   a refusal, a non-server rejection, or a scope switch during the round trip.
   */
  async cancel(membershipId: string, intent: Intent): Promise<boolean> {
    const orgId = this.ports.currentOrgId();
    if (orgId === null) {
      return false;
    }
    this.ports.publishCancel(SUBMITTING);
    try {
      await this.ports.data.cancelInvite(orgId, membershipId, intent);
    } catch (error) {
      const refusal = refusalOf(error);
      this.ports.publishCancel({ phase: 'failed', error: mutationFailure(error) });
      // Same stale row as a refused rotate: one re-read drops it, the refusal stays verbatim.
      if (refusal?.code === INVITE_NOT_PENDING && this.ports.currentOrgId() === orgId) {
        await this.ports.reload(orgId);
      }
      return false;
    }
    this.ports.publishCancel(SUBMITTED);
    if (this.ports.currentOrgId() !== orgId) {
      return false;
    }
    await this.ports.reload(orgId);
    return true;
  }

  /**
   * Releases {@link resendInFlight} when the rotate's background row refresh settles, either way: a
   * refresh that failed must never leave the lock closed, and one that cannot read the row falls
   * back to a full list read, which also settles here.
   */
  private releaseResendWhenSettled(work: Promise<void>): void {
    const release = (): void => {
      this.resendInFlight = false;
    };
    void work.then(release, release);
  }
}
