// The harness the members store specs share — the served fixture corpus and the fake data service
// that answers from it — so each spec mounts the same substrate and asserts on the store alone.
//
// The answers come from the real fixture corpus (`fixtures/web-api/memberships-list`,
// `memberships-list-empty`, `roles-list`, `permissions-list`, `membership-create-ok`,
// `membership-create-invite-exists`, `membership-remove-last-admin`), so the members, the role
// columns and the refusal envelopes are the shapes the dev backend serves and the contract pins.
// `MembersDataService` is replaced by a fake, because this suite is about what the store does with
// an answer — the request side is `core/data/members-data.service.spec.ts`.
//
// Every case is a decision the tab depends on:
//
// - the matrix is members x roles read from the server's own role objects, columns in server order;
// - a failed role or catalogue read degrades the substrate and never hides the memberships, while a
//   failed member read is the tab's error state with the server's message;
// - the list follows `next_cursor` once per call, appends, and refuses a cursor it already followed;
// - every mutation carries the caller's intent, so a retry reuses the `Idempotency-Key`; a refusal is
//   stored verbatim (AC2's 409 `last_admin`) and leaves the list exactly as it was;
// - a role change is applied one request at a time in the order the dialog built (grants first) and
//   stops at the first refusal, while `editApplied` keeps what the server did confirm — a partial
//   apply is reported as one, and the row always comes from a refreshed membership, never from the
//   dialog's draft;
// - no signal holds a secret-shaped field (task §8 S3, the `no-secret-field-in-store` rule's
//   runtime twin), and the invited address is never stored.

import { TestBed } from '@angular/core/testing';
import type {
  ErrorEnvelope,
  InviteIssuedView,
  InviteMemberRequest,
  MembershipListResponse,
  MembershipView,
  PermissionView,
  RoleView,
} from '@evidgate/sdk';

import { MembersDataService } from '../../../core/data/members-data.service';
import { ApiRequestError, type ApiErrorKind } from '../../../core/http/api-error';
import type { Intent } from '../../../core/http/intent';
import { MembersStore } from './members.store';

import membershipsList from '../../../../../../../fixtures/web-api/memberships-list/input.json';
import membershipsEmpty from '../../../../../../../fixtures/web-api/memberships-list-empty/input.json';
import membershipCreateOk from '../../../../../../../fixtures/web-api/membership-create-ok/input.json';
import membershipCreateInviteExists from '../../../../../../../fixtures/web-api/membership-create-invite-exists/input.json';
import membershipRemoveLastAdmin from '../../../../../../../fixtures/web-api/membership-remove-last-admin/input.json';
import permissionsList from '../../../../../../../fixtures/web-api/permissions-list/input.json';
import rolesList from '../../../../../../../fixtures/web-api/roles-list/input.json';

/**
 * A corpus literal's membership state as the SDK union: an imported JSON string widens to `string`,
 * so the state is checked against the wire's own values here rather than asserted into the type.
 */
function membershipState(value: string): MembershipView['status'] {
  switch (value) {
    case 'pending':
    case 'active':
    case 'removed':
    case 'revoked':
    case 'expired':
      return value;
    default:
      throw new Error(`fixtures/web-api serves an unknown membership status "${value}"`);
  }
}

/** The served page of §5: five memberships, one per lifecycle state the tab renders. */
export const PAGE: MembershipListResponse = {
  items: membershipsList.body.items.map((row) => ({
    ...row,
    status: membershipState(row.status),
  })),
  next_cursor: membershipsList.body.next_cursor,
};
/** The same operation's `empty` variant: a fresh org. */
export const EMPTY_PAGE: MembershipListResponse = membershipsEmpty.body;
/** Every role the caller can see, in server order — the matrix's columns. */
export const ROLES: readonly RoleView[] = rolesList.body.items;
/** The live catalogue's first page: sixteen rows, two of them `sensitive`. */
const CATALOGUE: readonly PermissionView[] = permissionsList.body.items;
/** The fixture's create response as served: the row plus the one-time plaintext. */
const SERVED_CREATE: InviteIssuedView = {
  ...membershipCreateOk.body,
  status: membershipState(membershipCreateOk.body.status),
};
/**
 * A pending invite, as `POST /v1/orgs/{o}/memberships` answers one — WITHOUT the served
 * `invite_token`: the fixture carries it, a row that can reach a store signal must not. The
 * plaintext lives only in {@link ISSUED}, which no test puts on a page.
 */
function createdRow(): MembershipView {
  const row: InviteIssuedView = { ...SERVED_CREATE };
  delete row.invite_token;
  return row;
}
export const CREATED: MembershipView = createdRow();
/**
 * The plaintext link token a mint serves exactly once (`evgi_<prefix>_<secret>`). It is a test
 * literal only: the store must never keep it, which `members.store.state.spec.ts` proves.
 */
export const INVITE_TOKEN = 'evgi_4b7d2e91c6a0_9d3f1a5c8e2b7d4f6a1c3e5b8d0f2a4c';
/** The token a rotate replaces it with. */
export const ROTATED_TOKEN = 'evgi_c0a1f3d5e7b9_2f4a6c8e0b1d3f5a7c9e1b3d5f7a9c0b';
/** AC2's refusal: the server's own message and code. */
export const LAST_ADMIN: ErrorEnvelope = membershipRemoveLastAdmin.body;
/** A second invite while one is pending. */
export const INVITE_EXISTS: ErrorEnvelope = membershipCreateInviteExists.body;
/**
 * The 409 of the invite lifecycle (02, AC6): the row is no longer `pending`, so there is nothing
 * to rotate and nothing to cancel — the same code on both routes.
 */
export const INVITE_NOT_PENDING: ErrorEnvelope = {
  error: {
    code: 'invite_not_pending',
    message: 'only a pending invitation can be rotated or cancelled',
    request_id: '9e5b1c73-4a28-4d6f-8b0a-2c3d4e5f6a7b',
    retryable: false,
  },
};
/** A 429 on any mutation: the same server copy the dialogs show, countdown included. */
export const RATE_LIMITED: ErrorEnvelope = {
  error: {
    code: 'rate_limited',
    message: 'too many requests; retry after the interval the response names',
    request_id: 'b7d1f4a6-2c8e-4f9a-8b3d-5e7f9a1c3b5d',
    retryable: true,
  },
};
/**
 * The server's 403 of the self-escalation rule (02.1.2 §3b, `SelfEscalation.Refused`): the 00 §9.2
 * `forbidden` code the 403 already documents, with the rule's own message. It is what a
 * `tenant_admin` gets when it grants itself a role holding a permission it lacks.
 */
export const SELF_ESCALATION: ErrorEnvelope = {
  error: {
    code: 'forbidden',
    message:
      "the request would grant the caller a permission the caller does not hold (00 §2: a role never widens the caller's own access)",
    request_id: 'c2f0a2b8-0c55-4a4c-9d5f-2f4f6c1f7d31',
    retryable: false,
  },
};

/** One row of the served page by position, so a test never reaches for `!` or `?? ''`. */
export function pageMember(index: number): MembershipView {
  const member = PAGE.items[index];
  if (member === undefined) {
    throw new Error(`the memberships fixture has no row ${index}`);
  }
  return member;
}

export function roleId(code: string): string {
  const role = ROLES.find((candidate) => candidate.code === code);
  if (role === undefined) {
    throw new Error(`the roles-list fixture has no ${code} role`);
  }
  return role.id;
}

/** The pending row of the served page: the one membership an invite can be rotated or cancelled on. */
export const PENDING = pageMember(3);

/** A minted invite: the membership plus the token only the minting response carries. */
export const ISSUED: InviteIssuedView = { ...CREATED, invite_token: INVITE_TOKEN };

/** The rotate answer: the same pending row, with the fresh token that replaced the old one. */
export const ROTATED: InviteIssuedView = { ...PENDING, invite_token: ROTATED_TOKEN };

/** The typed failure `ApiGateway` builds from one fixture envelope. */
export function typedFailure(
  envelope: ErrorEnvelope,
  kind: ApiErrorKind,
  status: number,
): ApiRequestError {
  return new ApiRequestError({
    kind,
    status,
    code: envelope.error.code,
    message: envelope.error.message,
    requestId: envelope.error.request_id,
    retryable: envelope.error.retryable,
    details: envelope.error.details ?? [],
    retryAfterSeconds: null,
  });
}

/**
 * Gives the store's background work its turn. `invite` answers before its list reload and `resend`
 * before its row refresh (M2), so a spec that asserts on the LIST or on the refreshed row has to
 * let those promises settle; the fake reads resolve immediately, so one macrotask drains them.
 */
export function settleBackground(): Promise<void> {
  return new Promise<void>((resolve) => {
    setTimeout(resolve, 0);
  });
}

/**
 * The membership reads and mutations the store performs. Cursor `"n"` names `pages[n]`, so a page
 * can hand out a next cursor the test controls exactly; the first page (no cursor) is `pages[0]`.
 */
export class FakeMembersDataService {
  pages: readonly (MembershipListResponse | undefined)[] = [];
  roles: readonly RoleView[] = [];
  catalogue: readonly PermissionView[] = [];
  membersFailure: Error | null = null;
  /** When true, `membersFailure` fires only on a cursor page, so the first page still loads. */
  failCursorPagesOnly = false;
  rolesFailure: Error | null = null;
  catalogueFailure: Error | null = null;
  inviteFailure: Error | null = null;
  /** The minted invite `invite` answers with; a test swaps it to answer a replay body. */
  issued: InviteIssuedView = ISSUED;
  /** When true, a rotate answers only when the test settles `pendingResends`. */
  resendManual = false;
  readonly pendingResends: Array<{
    membershipId: string;
    resolve: (view: InviteIssuedView) => void;
  }> = [];
  resendFailure: Error | null = null;
  /** The `invite_token` a rotate answers with; `null` or `''` mirror a body without one. */
  resendToken: string | null = ROTATED_TOKEN;
  /** When true, a cancel answers only when the test settles `pendingCancels`. */
  cancelManual = false;
  readonly pendingCancels: Array<{ membershipId: string; resolve: () => void }> = [];
  cancelFailure: Error | null = null;
  readonly resends: Array<{ orgId: string; membershipId: string; intent: Intent }> = [];
  readonly cancels: Array<{ orgId: string; membershipId: string; intent: Intent }> = [];
  removeFailure: Error | null = null;
  /** When true, every page names a fresh cursor, so the store's page cap is the only stop. */
  endless = false;
  /** When true, a membership read answers only when the test settles `pending`. */
  manual = false;
  readonly pending: Array<{ orgId: string; resolve: (page: MembershipListResponse) => void }> = [];
  /** When true, an invite answers only when the test settles `pendingInvites`. */
  inviteManual = false;
  readonly pendingInvites: Array<{
    orgId: string;
    resolve: (view: InviteIssuedView) => void;
  }> = [];
  /** When true, a removal answers only when the test settles `pendingRemovals`. */
  removeManual = false;
  readonly pendingRemovals: Array<{ orgId: string; resolve: () => void }> = [];
  /**
   * The "server's" memberships by id: `getMembership` serves them and the role operations write
   * to them, so a test's refresh reads the row the server would actually hold.
   */
  readonly memberViews = new Map<string, MembershipView>();
  /** Every refresh read, so a scope switch that skipped one is visible. */
  readonly membershipGets: Array<{ orgId: string; membershipId: string }> = [];
  membershipGetFailure: Error | null = null;
  /** When true, a refresh read answers only when the test settles `pendingGets`. */
  membershipGetManual = false;
  readonly pendingGets: Array<{
    membershipId: string;
    resolve: (view: MembershipView) => void;
  }> = [];
  /** Each role request in the order it reached the fake, so "grants first, then stop" is provable. */
  readonly roleRequests: string[] = [];
  readonly grants: Array<{
    orgId: string;
    membershipId: string;
    roleId: string;
    intent: Intent;
  }> = [];
  readonly revokes: Array<{
    orgId: string;
    membershipId: string;
    roleId: string;
    intent: Intent;
  }> = [];
  grantFailure: Error | null = null;
  revokeFailure: Error | null = null;
  /** When true, a grant answers only when the test settles it with `settleGrant`. */
  grantManual = false;
  readonly pendingGrants: Array<{
    membershipId: string;
    roleId: string;
    resolve: (view: MembershipView) => void;
  }> = [];
  readonly invites: Array<{ orgId: string; body: InviteMemberRequest; intent: Intent }> = [];
  readonly removals: Array<{ orgId: string; membershipId: string; intent: Intent }> = [];
  readonly membershipCursors: Array<string | undefined> = [];
  /** The org of each membership read, so a reload of a stale scope is visible. */
  readonly membershipOrgs: string[] = [];
  membershipCalls = 0;
  roleCalls = 0;
  catalogueCalls = 0;

  listMemberships(orgId: string, cursor?: string): Promise<MembershipListResponse> {
    this.membershipCalls += 1;
    this.membershipCursors.push(cursor);
    this.membershipOrgs.push(orgId);
    if (this.manual) {
      return new Promise<MembershipListResponse>((resolve) => {
        this.pending.push({ orgId, resolve });
      });
    }
    if (this.membersFailure !== null && (!this.failCursorPagesOnly || cursor !== undefined)) {
      return Promise.reject(this.membersFailure);
    }
    if (this.endless) {
      return Promise.resolve({ items: [], next_cursor: `c-${this.membershipCalls}` });
    }
    return Promise.resolve(
      this.pages[cursor === undefined ? 0 : Number(cursor)] ?? { items: [], next_cursor: null },
    );
  }

  listRoles(): Promise<readonly RoleView[]> {
    this.roleCalls += 1;
    return this.rolesFailure === null
      ? Promise.resolve(this.roles)
      : Promise.reject(this.rolesFailure);
  }

  listPermissions(): Promise<readonly PermissionView[]> {
    this.catalogueCalls += 1;
    return this.catalogueFailure === null
      ? Promise.resolve(this.catalogue)
      : Promise.reject(this.catalogueFailure);
  }

  invite(orgId: string, body: InviteMemberRequest, intent: Intent): Promise<InviteIssuedView> {
    this.invites.push({ orgId, body, intent });
    if (this.inviteManual) {
      return new Promise<InviteIssuedView>((resolve) => {
        this.pendingInvites.push({ orgId, resolve });
      });
    }
    return this.inviteFailure === null
      ? Promise.resolve(this.issued)
      : Promise.reject(this.inviteFailure);
  }

  resendInvite(orgId: string, membershipId: string, intent: Intent): Promise<InviteIssuedView> {
    this.resends.push({ orgId, membershipId, intent });
    if (this.resendManual) {
      return new Promise<InviteIssuedView>((resolve) => {
        this.pendingResends.push({ membershipId, resolve });
      });
    }
    if (this.resendFailure !== null) {
      return Promise.reject(this.resendFailure);
    }
    // The rotate writes the row the refresh read serves; the token is only in the answer.
    const row = this.memberViews.get(membershipId) ?? PENDING;
    this.memberViews.set(membershipId, row);
    return Promise.resolve({ ...row, invite_token: this.resendToken });
  }

  cancelInvite(orgId: string, membershipId: string, intent: Intent): Promise<void> {
    this.cancels.push({ orgId, membershipId, intent });
    if (this.cancelManual) {
      return new Promise<void>((resolve) => {
        this.pendingCancels.push({ membershipId, resolve });
      });
    }
    return this.cancelFailure === null ? Promise.resolve() : Promise.reject(this.cancelFailure);
  }

  remove(orgId: string, membershipId: string, intent: Intent): Promise<void> {
    this.removals.push({ orgId, membershipId, intent });
    if (this.removeManual) {
      return new Promise<void>((resolve) => {
        this.pendingRemovals.push({ orgId, resolve });
      });
    }
    return this.removeFailure === null ? Promise.resolve() : Promise.reject(this.removeFailure);
  }

  getMembership(orgId: string, membershipId: string): Promise<MembershipView> {
    this.membershipGets.push({ orgId, membershipId });
    if (this.membershipGetFailure !== null) {
      return Promise.reject(this.membershipGetFailure);
    }
    if (this.membershipGetManual) {
      return new Promise<MembershipView>((resolve) => {
        this.pendingGets.push({ membershipId, resolve });
      });
    }
    const view = this.memberViews.get(membershipId);
    return view === undefined
      ? Promise.reject(new Error(`the fake holds no membership ${membershipId}`))
      : Promise.resolve(view);
  }

  /** Settles the n-th deferred refresh read with the row the "server" holds right now. */
  settleGet(index: number): void {
    const pending = this.pendingGets[index];
    if (pending === undefined) {
      throw new Error(`the fake holds no pending refresh read ${index}`);
    }
    const view = this.memberViews.get(pending.membershipId);
    if (view === undefined) {
      throw new Error(`the fake holds no membership ${pending.membershipId}`);
    }
    pending.resolve(view);
  }

  grantRole(
    orgId: string,
    membershipId: string,
    roleId: string,
    intent: Intent,
  ): Promise<MembershipView> {
    this.roleRequests.push(`grant:${roleId}`);
    this.grants.push({ orgId, membershipId, roleId, intent });
    if (this.grantManual) {
      return new Promise<MembershipView>((resolve) => {
        this.pendingGrants.push({ membershipId, roleId, resolve });
      });
    }
    return this.grantFailure === null
      ? Promise.resolve(this.applyRole(membershipId, roleId, true))
      : Promise.reject(this.grantFailure);
  }

  revokeRole(orgId: string, membershipId: string, roleId: string, intent: Intent): Promise<void> {
    this.roleRequests.push(`revoke:${roleId}`);
    this.revokes.push({ orgId, membershipId, roleId, intent });
    if (this.revokeFailure !== null) {
      return Promise.reject(this.revokeFailure);
    }
    this.applyRole(membershipId, roleId, false);
    return Promise.resolve();
  }

  /** Settles the n-th manual grant the way the server does: the row gains that role. */
  settleGrant(index: number): void {
    const pending = this.pendingGrants[index];
    if (pending === undefined) {
      throw new Error(`the fake holds no pending grant ${index}`);
    }
    pending.resolve(this.applyRole(pending.membershipId, pending.roleId, true));
  }

  /** The server's own role-set write, so a refreshed row already holds the change it confirmed. */
  private applyRole(membershipId: string, roleId: string, grant: boolean): MembershipView {
    const current = this.memberViews.get(membershipId);
    const role = this.roles.find((candidate) => candidate.id === roleId);
    if (current === undefined || role === undefined) {
      throw new Error(`the fake cannot change ${roleId} on ${membershipId}`);
    }
    const kept = current.roles.filter((held) => held.id !== roleId);
    const updated: MembershipView = { ...current, roles: grant ? [...kept, role] : kept };
    this.memberViews.set(membershipId, updated);
    return updated;
  }
}

/**
 * The store under test, with the fake serving the served page, its roles and the live catalogue.
 * Each spec mounts a fresh store so one case's refusals never leak into the next.
 */
export function mountMembersStore(): { data: FakeMembersDataService; store: MembersStore } {
  const data = new FakeMembersDataService();
  data.pages = [PAGE];
  data.roles = ROLES;
  data.catalogue = CATALOGUE;
  for (const member of PAGE.items) {
    data.memberViews.set(member.id, member);
  }
  TestBed.configureTestingModule({
    providers: [MembersStore, { provide: MembersDataService, useValue: data }],
  });
  return { data, store: TestBed.inject(MembersStore) };
}
