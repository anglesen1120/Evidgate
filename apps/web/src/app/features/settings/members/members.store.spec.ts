// 09.7.1-T2 (plan 09.7.1 §2 AC2, §4 D5, §5 members row, §6 Membership/Invite rows, §8 S3,
// §10 T2.1/T2.2; COORDINATOR-DECISION S73 option B): the Members and roles store.
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
  PermissionListResponse,
  PermissionView,
  RoleListResponse,
  RoleView,
} from '@evidgate/sdk';

import { MembersDataService } from '../../../core/data/members-data.service';
import { ApiRequestError, type ApiErrorKind } from '../../../core/http/api-error';
import { createIntent, type Intent } from '../../../core/http/intent';
import { MembersStore } from './members.store';

import membershipsList from '../../../../../../../fixtures/web-api/memberships-list/input.json';
import membershipsEmpty from '../../../../../../../fixtures/web-api/memberships-list-empty/input.json';
import membershipCreateOk from '../../../../../../../fixtures/web-api/membership-create-ok/input.json';
import membershipCreateInviteExists from '../../../../../../../fixtures/web-api/membership-create-invite-exists/input.json';
import membershipRemoveLastAdmin from '../../../../../../../fixtures/web-api/membership-remove-last-admin/input.json';
import permissionsList from '../../../../../../../fixtures/web-api/permissions-list/input.json';
import rolesList from '../../../../../../../fixtures/web-api/roles-list/input.json';

/** The served page of §5: five memberships, one per lifecycle state the tab renders. */
const PAGE = membershipsList.body as unknown as MembershipListResponse;
/** The same operation's `empty` variant: a fresh org. */
const EMPTY_PAGE = membershipsEmpty.body as unknown as MembershipListResponse;
/** Every role the caller can see, in server order — the matrix's columns. */
const ROLES = (rolesList.body as unknown as RoleListResponse).items;
/** The live catalogue's first page: sixteen rows, two of them `sensitive`. */
const CATALOGUE = (permissionsList.body as unknown as PermissionListResponse).items;
/** The fixture's create response as served: the row plus the one-time plaintext. */
const SERVED_CREATE = membershipCreateOk.body as unknown as InviteIssuedView;
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
const CREATED: MembershipView = createdRow();
/**
 * The plaintext link token a mint serves exactly once (`evgi_<prefix>_<secret>`). It is a test
 * literal only: the store must never keep it, which the last spec of this file proves.
 */
const INVITE_TOKEN = 'evgi_4b7d2e91c6a0_9d3f1a5c8e2b7d4f6a1c3e5b8d0f2a4c';
/** The token a rotate replaces it with. */
const ROTATED_TOKEN = 'evgi_c0a1f3d5e7b9_2f4a6c8e0b1d3f5a7c9e1b3d5f7a9c0b';
/** AC2's refusal: the server's own message and code. */
const LAST_ADMIN = membershipRemoveLastAdmin.body as unknown as ErrorEnvelope;
/** A second invite while one is pending. */
const INVITE_EXISTS = membershipCreateInviteExists.body as unknown as ErrorEnvelope;
/**
 * The 409 of the invite lifecycle (02, AC6): the row is no longer `pending`, so there is nothing
 * to rotate and nothing to cancel — the same code on both routes.
 */
const INVITE_NOT_PENDING: ErrorEnvelope = {
  error: {
    code: 'invite_not_pending',
    message: 'only a pending invitation can be rotated or cancelled',
    request_id: '9e5b1c73-4a28-4d6f-8b0a-2c3d4e5f6a7b',
    retryable: false,
  },
};
/** A 429 on any mutation: the same server copy the dialogs show, countdown included. */
const RATE_LIMITED: ErrorEnvelope = {
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
const SELF_ESCALATION: ErrorEnvelope = {
  error: {
    code: 'forbidden',
    message:
      "the request would grant the caller a permission the caller does not hold (00 §2: a role never widens the caller's own access)",
    request_id: 'c2f0a2b8-0c55-4a4c-9d5f-2f4f6c1f7d31',
    retryable: false,
  },
};

/** One row of the served page by position, so a test never reaches for `!` or `?? ''`. */
function pageMember(index: number): MembershipView {
  const member = PAGE.items[index];
  if (member === undefined) {
    throw new Error(`the memberships fixture has no row ${index}`);
  }
  return member;
}

function roleId(code: string): string {
  const role = ROLES.find((candidate) => candidate.code === code);
  if (role === undefined) {
    throw new Error(`the roles-list fixture has no ${code} role`);
  }
  return role.id;
}

/** The pending row of the served page: the one membership an invite can be rotated or cancelled on. */
const PENDING = pageMember(3);

/** A minted invite: the membership plus the token only the minting response carries. */
const ISSUED: InviteIssuedView = { ...CREATED, invite_token: INVITE_TOKEN };

/** The rotate answer: the same pending row, with the fresh token that replaced the old one. */
const ROTATED: InviteIssuedView = { ...PENDING, invite_token: ROTATED_TOKEN };

/** The typed failure `ApiGateway` builds from one fixture envelope. */
function typedFailure(
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
function settleBackground(): Promise<void> {
  return new Promise<void>((resolve) => {
    setTimeout(resolve, 0);
  });
}

/**
 * The membership reads and mutations the store performs. Cursor `"n"` names `pages[n]`, so a page
 * can hand out a next cursor the test controls exactly; the first page (no cursor) is `pages[0]`.
 */
class FakeMembersDataService {
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

describe('MembersStore (09.7.1-T2)', () => {
  let data: FakeMembersDataService;
  let store: MembersStore;

  beforeEach(() => {
    data = new FakeMembersDataService();
    data.pages = [PAGE];
    data.roles = ROLES;
    data.catalogue = CATALOGUE;
    for (const member of PAGE.items) {
      data.memberViews.set(member.id, member);
    }
    TestBed.configureTestingModule({
      providers: [MembersStore, { provide: MembersDataService, useValue: data }],
    });
    store = TestBed.inject(MembersStore);
  });

  describe('load', () => {
    it('renders the members x roles matrix from the fixture response', async () => {
      expect(store.phase()).toBe('idle');

      await store.load('o-1');

      expect(store.phase()).toBe('ready');
      expect(store.members().map((member) => member.id)).toEqual(
        PAGE.items.map((member) => member.id),
      );
      const matrix = store.matrix();
      // Columns are the server's roles, in server order, unfiltered: the matrix is org-wide.
      expect(matrix.columns.map((role) => role.code)).toEqual(ROLES.map((role) => role.code));
      expect(matrix.rows.map((row) => row.membership.id)).toEqual(
        PAGE.items.map((member) => member.id),
      );
      // Granted cells are the role ids the server put on the membership, nothing derived.
      expect(matrix.rows.map((row) => [...row.granted])).toEqual(
        PAGE.items.map((member) => member.roles.map((role) => role.id)),
      );
      expect(store.hasMore()).toBe(false);
      expect(store.loadError()).toBeNull();
      // The tab reads one org's list, roles and catalogue, once each.
      expect(data.membershipCursors).toEqual([undefined]);
      expect(data.roleCalls).toBe(1);
      expect(data.catalogueCalls).toBe(1);
    });

    it('is ready with no rows for the empty variant, and points the matrix at no columns', async () => {
      data.pages = [EMPTY_PAGE];

      await store.load('o-1');

      expect(store.phase()).toBe('ready');
      expect(store.members()).toEqual([]);
      expect(store.matrix().rows).toEqual([]);
      expect(store.hasMore()).toBe(false);
    });

    it('is the tab error state with the server’s failure when the member read refuses', async () => {
      const failure = typedFailure(LAST_ADMIN, 'conflict', 409);
      data.membersFailure = failure;

      await store.load('o-1');

      expect(store.phase()).toBe('error');
      expect(store.loadError()).toBe(failure.error);
      expect(store.members()).toEqual([]);
      expect(store.hasMore()).toBe(false);
    });

    it('keeps the memberships when the role list fails, and says its columns are unavailable', async () => {
      data.rolesFailure = typedFailure(LAST_ADMIN, 'conflict', 409);

      await store.load('o-1');

      expect(store.phase()).toBe('ready');
      expect(store.members()).toHaveLength(PAGE.items.length);
      expect(store.roles()).toEqual([]);
      expect(store.matrix().columns).toEqual([]);
    });

    it('keeps the memberships when the catalogue fails, and the preview says why', async () => {
      data.catalogueFailure = typedFailure(LAST_ADMIN, 'conflict', 409);

      await store.load('o-1');

      expect(store.phase()).toBe('ready');
      expect(store.members()).toHaveLength(PAGE.items.length);
      expect(store.catalogue()).toBeNull();
      expect(store.preview(roleId('viewer'))).toEqual({
        kind: 'unavailable',
        reason: 'catalogue-unavailable',
      });
    });

    it('lets a newer load win: the older read writes nothing when it settles', async () => {
      data.manual = true;
      const older = store.load('o-1');
      const newer = store.load('o-2');

      data.pending[1]?.resolve(EMPTY_PAGE);
      await newer;
      data.pending[0]?.resolve(PAGE);
      await older;

      expect(store.members()).toEqual([]);
      expect(store.phase()).toBe('ready');
    });
  });

  describe('loadMore', () => {
    it('appends the next page and stops at a null cursor', async () => {
      const [first, second] = PAGE.items;
      data.pages = [
        { items: first === undefined ? [] : [first], next_cursor: '1' },
        { items: second === undefined ? [] : [second], next_cursor: null },
      ];
      await store.load('o-1');
      expect(store.hasMore()).toBe(true);

      await store.loadMore();
      expect(store.members().map((member) => member.id)).toEqual([first?.id, second?.id]);
      expect(store.hasMore()).toBe(false);

      // Nothing left to follow: a second call asks the server nothing.
      await store.loadMore();
      expect(data.membershipCursors).toEqual([undefined, '1']);
    });

    it('refuses a cursor the server hands out twice', async () => {
      data.pages = [
        { items: [], next_cursor: '1' },
        { items: [], next_cursor: '1' },
      ];
      await store.load('o-1');
      await store.loadMore();

      await expect(store.loadMore()).rejects.toThrow(/repeated cursor "1"/);
    });

    it('stops at the page cap when the cursor never ends', async () => {
      data.endless = true;
      await store.load('o-1');

      let failure: Error | null = null;
      for (let attempt = 0; attempt < 40 && failure === null; attempt += 1) {
        failure = await store.loadMore().then(
          () => null,
          (error: Error) => error,
        );
      }

      expect(failure?.message).toContain('within 20 pages');
    });

    it('keeps the rows already read when a later page refuses', async () => {
      const failure = typedFailure(INVITE_EXISTS, 'conflict', 409);
      data.pages = [{ items: PAGE.items, next_cursor: '1' }];
      data.failCursorPagesOnly = true;
      data.membersFailure = failure;
      await store.load('o-1');

      await store.loadMore();

      expect(store.members()).toHaveLength(PAGE.items.length);
      expect(store.phase()).toBe('ready');
      expect(store.loadError()).toBe(failure.error);
      expect(store.hasMore()).toBe(true);
    });

    it('re-throws a rejection that is not the server’s answer, for the page to catch', async () => {
      const failure = new Error('adapter bug: the page never arrived');
      data.pages = [{ items: PAGE.items, next_cursor: '1' }];
      data.failCursorPagesOnly = true;
      data.membersFailure = failure;
      await store.load('o-1');

      // Not a typed refusal: a bug, re-thrown unchanged rather than read as a server decision.
      await expect(store.loadMore()).rejects.toBe(failure);
      expect(store.members()).toHaveLength(PAGE.items.length);
      expect(store.loadError()).toBeNull();
    });
  });

  describe('invite', () => {
    it('sends { email, roles } with the caller’s intent and reloads the first page', async () => {
      data.pages = [{ items: [...PAGE.items, CREATED], next_cursor: null }];
      await store.load('o-1');
      const intent = createIntent();

      const invited = await store.invite('newhire@acme.test', [roleId('viewer')], intent);

      // The token comes back exactly once, on the call: it is a return value, not state.
      expect(invited).toEqual({ membershipId: CREATED.id, inviteToken: INVITE_TOKEN });
      expect(data.invites).toEqual([
        {
          orgId: 'o-1',
          body: { email: 'newhire@acme.test', roles: [roleId('viewer')] },
          intent,
        },
      ]);
      expect(store.inviteState()).toEqual({ phase: 'done', error: null });
      // The address travels in the body only: the store's own state never carries it.
      expect(JSON.stringify(store.inviteState())).not.toContain('newhire@acme.test');
      // Neither does any state carry the token the answer just handed back.
      expect(JSON.stringify(store.inviteState())).not.toContain(INVITE_TOKEN);
      // The reload started from the first page, so the new invite reaches the screen.
      await settleBackground();
      expect(data.membershipCursors).toEqual([undefined, undefined]);
      expect(store.members().map((member) => member.id)).toContain(CREATED.id);
    });

    it('answers the token before its list reload finishes, so a closing dialog keeps it', async () => {
      await store.load('o-1');
      // Every membership read is deferred by hand from here on: the reload stays on the wire.
      data.manual = true;

      const inFlight = store.invite('newhire@acme.test', [], createIntent());
      await settleBackground();

      // M2: the one plaintext the server serves once arrives without waiting for the list.
      await expect(inFlight).resolves.toEqual({
        membershipId: CREATED.id,
        inviteToken: INVITE_TOKEN,
      });
      expect(store.inviteState()).toEqual({ phase: 'done', error: null });
      // The reload this call started has ISSUED its read and is waiting on it (deferred by hand):
      // the token came back anyway.
      expect(data.membershipCursors).toEqual([undefined, undefined]);
      expect(data.pending).toHaveLength(1);

      // Let it land, so the deferred read does not outlive the spec.
      data.pending[0]?.resolve(PAGE);
      await settleBackground();
      expect(data.membershipCursors).toEqual([undefined, undefined]);
      expect(store.members().map((member) => member.id)).toEqual(
        PAGE.items.map((member) => member.id),
      );
    });

    it('answers inviteToken: null when the server replays a body with no token', async () => {
      await store.load('o-1');
      // An `Idempotency-Key` replay answers the stored body, which never held the plaintext.
      data.issued = { ...CREATED, invite_token: null };

      const invited = await store.invite('newhire@acme.test', [], createIntent());

      expect(invited).toEqual({ membershipId: CREATED.id, inviteToken: null });
      expect(store.inviteState()).toEqual({ phase: 'done', error: null });
      await settleBackground();
    });

    it('answers inviteToken: null for an EMPTY served token, so the dialog offers Resend', async () => {
      await store.load('o-1');
      // F2: `''` is absence, not a one-time value — the dialogs must not reveal nothing.
      data.issued = { ...CREATED, invite_token: '' };

      const invited = await store.invite('newhire@acme.test', [], createIntent());

      expect(invited).toEqual({ membershipId: CREATED.id, inviteToken: null });
      await settleBackground();
    });

    it('answers inviteToken: null for a non-string served token', async () => {
      await store.load('o-1');
      // F2: a broken adapter that echoes a number must not reach the reveal as `42`.
      data.issued = { ...CREATED, invite_token: 42 as unknown as string };

      const invited = await store.invite('newhire@acme.test', [], createIntent());

      expect(invited).toEqual({ membershipId: CREATED.id, inviteToken: null });
      await settleBackground();
    });

    it('omits roles when the dialog selected none', async () => {
      await store.load('o-1');

      await store.invite('newhire@acme.test', [], createIntent());

      expect(data.invites[0]?.body).toEqual({ email: 'newhire@acme.test' });
    });

    it('reuses the same Idempotency-Key when the same intent is retried', async () => {
      await store.load('o-1');
      const intent = createIntent();
      const refusal = typedFailure(INVITE_EXISTS, 'conflict', 409);
      data.inviteFailure = refusal;

      const refused = await store.invite('newhire@acme.test', [], intent);
      expect(refused).toBeNull();
      expect(store.inviteState()).toEqual({ phase: 'failed', error: refusal.error });

      data.inviteFailure = null;
      const retried = await store.invite('newhire@acme.test', [], intent);

      expect(retried).toEqual({ membershipId: CREATED.id, inviteToken: INVITE_TOKEN });
      expect(data.invites.map((entry) => entry.intent.idempotencyKey)).toEqual([
        intent.idempotencyKey,
        intent.idempotencyKey,
      ]);
    });

    it('surfaces 409 invite_exists verbatim and leaves the list alone', async () => {
      await store.load('o-1');
      data.inviteFailure = typedFailure(INVITE_EXISTS, 'conflict', 409);

      const invited = await store.invite('newhire@acme.test', [], createIntent());

      expect(invited).toBeNull();
      expect(store.inviteState().phase).toBe('failed');
      expect(store.inviteState().error?.code).toBe(INVITE_EXISTS.error.code);
      expect(store.inviteState().error?.message).toBe(INVITE_EXISTS.error.message);
      // No reload: the refused invite changed nothing on screen.
      expect(data.membershipCursors).toEqual([undefined]);
      expect(store.members()).toHaveLength(PAGE.items.length);
    });
  });

  describe('resend', () => {
    it('rotates the invite, answers the new token and refreshes that one row', async () => {
      await store.load('o-1');
      const intent = createIntent();

      const rotated = await store.resend(PENDING.id, intent);

      // The new plaintext is the answer and nothing else.
      expect(rotated).toEqual({ membershipId: PENDING.id, inviteToken: ROTATED_TOKEN });
      expect(data.resends).toEqual([{ orgId: 'o-1', membershipId: PENDING.id, intent }]);
      expect(store.resendState()).toEqual({ phase: 'done', error: null });
      // The row comes from the server's own read, not from patchwork on the rotate response.
      await settleBackground();
      expect(data.membershipGets).toEqual([{ orgId: 'o-1', membershipId: PENDING.id }]);
      expect(store.members().map((member) => member.id)).toEqual(
        PAGE.items.map((member) => member.id),
      );
      expect(store.members()[3]?.status).toBe('pending');
      // No signal holds the token the call just returned.
      expect(JSON.stringify(store.resendState())).not.toContain(ROTATED_TOKEN);
    });

    it('answers the new token for an EMPTY served token as absence, never as an empty value', async () => {
      await store.load('o-1');
      // F2: the replay notice, not a one-time reveal of `''`.
      data.resendToken = '';

      const rotated = await store.resend(PENDING.id, createIntent());

      expect(rotated).toEqual({ membershipId: PENDING.id, inviteToken: null });
      await settleBackground();
    });

    it('answers absence for a non-string rotated token as well', async () => {
      await store.load('o-1');
      data.resendToken = 7 as unknown as string;

      const rotated = await store.resend(PENDING.id, createIntent());

      expect(rotated).toEqual({ membershipId: PENDING.id, inviteToken: null });
      await settleBackground();
    });

    it('surfaces 409 invite_not_pending verbatim and re-reads the list to drop the stale row', async () => {
      await store.load('o-1');
      const refusal = typedFailure(INVITE_NOT_PENDING, 'conflict', 409);
      data.resendFailure = refusal;
      // What the server's refusal means for the list: the row the user pressed is not pending.
      data.pages = [
        {
          items: PAGE.items.map((member) =>
            member.id === PENDING.id ? { ...member, status: 'active' as const } : member,
          ),
          next_cursor: null,
        },
      ];

      const rotated = await store.resend(PENDING.id, createIntent());

      expect(rotated).toBeNull();
      expect(store.resendState()).toEqual({ phase: 'failed', error: refusal.error });
      expect(store.resendState().error?.code).toBe('invite_not_pending');
      expect(store.resendState().error?.message).toBe(INVITE_NOT_PENDING.error.message);
      // L5: one re-read of the list, so the pending row (and its Resend action) disappears.
      expect(data.membershipCursors).toEqual([undefined, undefined]);
      expect(store.members()[3]?.status).toBe('active');
      // Only the refusal read: no row refresh of a rotate that never happened.
      expect(data.membershipGets).toEqual([]);
    });

    it('surfaces a 429 verbatim, so the rotate can offer the server’s countdown', async () => {
      await store.load('o-1');
      const refusal = typedFailure(RATE_LIMITED, 'rate_limited', 429);
      data.resendFailure = refusal;

      await expect(store.resend(PENDING.id, createIntent())).resolves.toBeNull();

      expect(store.resendState()).toEqual({ phase: 'failed', error: refusal.error });
      expect(store.resendState().error?.kind).toBe('rate_limited');
      expect(store.resendState().error?.retryable).toBe(true);
      // A throttle is not a stale row: the list on screen stands.
      expect(data.membershipGets).toEqual([]);
      expect(data.membershipCursors).toEqual([undefined]);
    });

    it('drops a detached row refresh that a newer load overtook, so no stale row lands', async () => {
      await store.load('o-1');
      // The rotate answers; its row refresh stays on the wire, deferred by hand.
      data.membershipGetManual = true;

      await expect(store.resend(PENDING.id, createIntent())).resolves.toEqual({
        membershipId: PENDING.id,
        inviteToken: ROTATED_TOKEN,
      });
      await settleBackground();
      expect(data.pendingGets).toHaveLength(1);

      // Meanwhile a newer load of the SAME org reads a list where that row has moved on.
      data.pages = [
        {
          items: PAGE.items.map((member) =>
            member.id === PENDING.id ? { ...member, status: 'active' as const } : member,
          ),
          next_cursor: null,
        },
      ];
      await store.load('o-1');
      expect(store.members()[3]?.status).toBe('active');

      // The overtaken read lands with the row as it was BEFORE that load: it writes nothing.
      data.settleGet(0);
      await settleBackground();
      expect(store.members()[3]?.status).toBe('active');
      // And it does not fall back to a reload either: the list was read exactly twice.
      expect(data.membershipCursors).toEqual([undefined, undefined]);
    });

    it('answers null without a second request while one rotate is submitting', async () => {
      await store.load('o-1');
      data.resendManual = true;
      const first = store.resend(PENDING.id, createIntent());
      expect(store.resendState().phase).toBe('submitting');

      // Two rotates would mint two tokens and kill the one the user is looking at.
      await expect(store.resend(PENDING.id, createIntent())).resolves.toBeNull();
      expect(data.resends).toHaveLength(1);

      data.pendingResends[0]?.resolve(ROTATED);
      await expect(first).resolves.toEqual({
        membershipId: PENDING.id,
        inviteToken: ROTATED_TOKEN,
      });
      expect(store.resendState()).toEqual({ phase: 'done', error: null });
      await settleBackground();
    });

    it('publishes done before the row refresh settles, and still refuses a second rotate', async () => {
      await store.load('o-1');
      // The server answered; its own refresh read is the part still on the wire.
      data.membershipGetManual = true;

      const rotated = store.resend(PENDING.id, createIntent());

      // M2: the token does not wait for the refresh, so a dialog that closes now still has it.
      await expect(rotated).resolves.toEqual({
        membershipId: PENDING.id,
        inviteToken: ROTATED_TOKEN,
      });
      expect(store.resendState()).toEqual({ phase: 'done', error: null });
      expect(data.membershipGets).toEqual([{ orgId: 'o-1', membershipId: PENDING.id }]);

      // The guard is the private lock, not the published phase: a second rotate within that window
      // would mint a second token and kill the one this call just handed back.
      await expect(store.resend(PENDING.id, createIntent())).resolves.toBeNull();
      expect(data.resends).toHaveLength(1);

      data.settleGet(0);
      await settleBackground();
      // The lock opens once the refresh lands, so a later, deliberate rotate is a new attempt.
      data.membershipGetManual = false;
      await expect(store.resend(PENDING.id, createIntent())).resolves.toEqual({
        membershipId: PENDING.id,
        inviteToken: ROTATED_TOKEN,
      });
      expect(data.resends).toHaveLength(2);
      await settleBackground();
    });

    it('rejects a non-API rejection as the generic unexpected failure', async () => {
      await store.load('o-1');
      data.resendFailure = new TypeError('adapter bug: boom');

      await expect(store.resend(PENDING.id, createIntent())).resolves.toBeNull();

      expect(store.resendState().phase).toBe('failed');
      expect(store.resendState().error?.kind).toBe('unexpected');
      expect(store.resendState().error?.status).toBe(0);
    });
  });

  describe('cancel', () => {
    it('cancels with the caller’s intent and reloads the list', async () => {
      await store.load('o-1');
      const intent = createIntent();
      // The server's answer to the cancel: the row is `revoked`, which the reload reads.
      data.pages = [
        {
          items: PAGE.items.map((member) =>
            member.id === PENDING.id ? { ...member, status: 'revoked' as const } : member,
          ),
          next_cursor: null,
        },
      ];

      const cancelled = await store.cancel(PENDING.id, intent);

      expect(cancelled).toBe(true);
      expect(data.cancels).toEqual([{ orgId: 'o-1', membershipId: PENDING.id, intent }]);
      expect(store.cancelState()).toEqual({ phase: 'done', error: null });
      expect(data.membershipCursors).toEqual([undefined, undefined]);
      expect(store.members()[3]?.status).toBe('revoked');
    });

    it('surfaces 409 invite_not_pending verbatim and re-reads the list to drop the stale row', async () => {
      await store.load('o-1');
      const refusal = typedFailure(INVITE_NOT_PENDING, 'conflict', 409);
      data.cancelFailure = refusal;
      // The server's answer means the row is no longer pending, which the re-read shows.
      data.pages = [
        {
          items: PAGE.items.map((member) =>
            member.id === PENDING.id ? { ...member, status: 'revoked' as const } : member,
          ),
          next_cursor: null,
        },
      ];

      const cancelled = await store.cancel(PENDING.id, createIntent());

      expect(cancelled).toBe(false);
      expect(store.cancelState()).toEqual({ phase: 'failed', error: refusal.error });
      expect(store.cancelState().error?.status).toBe(409);
      expect(store.cancelState().error?.code).toBe(INVITE_NOT_PENDING.error.code);
      expect(store.cancelState().error?.message).toBe(INVITE_NOT_PENDING.error.message);
      // L5: one re-read of the list, so the stale pending row and its Cancel action go.
      expect(data.membershipCursors).toEqual([undefined, undefined]);
      expect(store.members()[3]?.status).toBe('revoked');
    });

    it('rejects a non-API rejection as the generic unexpected failure', async () => {
      await store.load('o-1');
      data.cancelFailure = new Error('adapter bug: boom');

      await expect(store.cancel(PENDING.id, createIntent())).resolves.toBe(false);

      expect(store.cancelState().phase).toBe('failed');
      expect(store.cancelState().error?.kind).toBe('unexpected');
    });
  });

  describe('remove', () => {
    it('removes with the caller’s intent and reloads on success', async () => {
      data.pages = [{ items: PAGE.items.slice(0, 1), next_cursor: null }];
      await store.load('o-1');
      const target = PAGE.items[1];
      const intent = createIntent();

      const removed = await store.remove(target?.id ?? '', intent);

      expect(removed).toBe(true);
      expect(data.removals).toEqual([{ orgId: 'o-1', membershipId: target?.id, intent }]);
      expect(store.removeState()).toEqual({ phase: 'done', error: null });
      expect(data.membershipCursors).toEqual([undefined, undefined]);
    });

    it('surfaces 409 last_admin verbatim and leaves the list unchanged (AC2)', async () => {
      await store.load('o-1');
      const before = store.members();
      data.removeFailure = typedFailure(LAST_ADMIN, 'conflict', 409);

      const removed = await store.remove(PAGE.items[0]?.id ?? '', createIntent());

      expect(removed).toBe(false);
      expect(store.removeState().phase).toBe('failed');
      expect(store.removeState().error?.status).toBe(409);
      expect(store.removeState().error?.code).toBe('last_admin');
      expect(store.removeState().error?.message).toBe(LAST_ADMIN.error.message);
      // The rows on screen are exactly the ones read before the attempt.
      expect(store.members()).toBe(before);
      expect(data.membershipCursors).toEqual([undefined]);
    });
  });

  describe('changeRoles', () => {
    it('applies grants before revokes in the given order and shows the server’s roles', async () => {
      await store.load('o-1');
      const target = pageMember(1);
      const viewer = roleId('viewer');
      const qa = roleId('qa_engineer');
      const grantIntent = createIntent();
      const revokeIntent = createIntent();

      const changed = await store.changeRoles(target.id, [
        { kind: 'grant', roleId: viewer, intent: grantIntent },
        { kind: 'revoke', roleId: qa, intent: revokeIntent },
      ]);

      expect(changed).toBe(true);
      // One request per role, in the dialog's order, each with its own caller-owned key.
      expect(data.roleRequests).toEqual([`grant:${viewer}`, `revoke:${qa}`]);
      expect(data.grants[0]?.intent).toBe(grantIntent);
      expect(data.revokes[0]?.intent).toBe(revokeIntent);
      expect(store.editState()).toEqual({ phase: 'done', error: null });
      expect(store.editApplied()).toEqual([
        { kind: 'grant', roleId: viewer },
        { kind: 'revoke', roleId: qa },
      ]);
      // The row is the server's row, in its own position, replaced after one refresh read.
      expect(store.memberById(target.id)?.roles.map((role) => role.id)).toEqual([viewer]);
      expect(store.members().map((member) => member.id)).toEqual(PAGE.items.map((it) => it.id));
      expect(data.membershipGets).toEqual([{ orgId: 'o-1', membershipId: target.id }]);
      // Refreshed in place: no full reload, so the other rows and the cursor state stand.
      expect(data.membershipOrgs).toEqual(['o-1']);
    });

    it('forwards each change’s own intent, so two grants never share one key', async () => {
      await store.load('o-1');
      const first = createIntent();
      const second = createIntent();

      await store.changeRoles(pageMember(1).id, [
        { kind: 'grant', roleId: roleId('viewer'), intent: first },
        { kind: 'grant', roleId: roleId('ci_producer'), intent: second },
      ]);

      // The key belongs to the change, not to the attempt: one key on two different grants is the
      // server's 409 `idempotency_conflict` (00 §9.1, the operation is the command type), so the
      // store passes each change's own intent through and never mints or reuses one itself.
      expect(data.grants.map((entry) => entry.intent)).toEqual([first, second]);
      expect(new Set(data.grants.map((entry) => entry.intent.idempotencyKey)).size).toBe(2);
      expect(store.editApplied()).toHaveLength(2);
    });

    it('grant ok then revoke 409 last_admin: stops, keeps the grant and refreshes the row', async () => {
      await store.load('o-1');
      const admin = pageMember(0);
      const viewer = roleId('viewer');
      const tenantAdmin = roleId('tenant_admin');
      data.revokeFailure = typedFailure(LAST_ADMIN, 'conflict', 409);

      const changed = await store.changeRoles(admin.id, [
        { kind: 'grant', roleId: viewer, intent: createIntent() },
        { kind: 'revoke', roleId: tenantAdmin, intent: createIntent() },
        // Never reached: the refusal above ends the attempt, so no third call is sent.
        { kind: 'grant', roleId: roleId('ci_producer'), intent: createIntent() },
      ]);

      expect(changed).toBe(false);
      expect(data.roleRequests).toEqual([`grant:${viewer}`, `revoke:${tenantAdmin}`]);
      expect(data.grants).toHaveLength(1);
      expect(data.revokes).toHaveLength(1);
      // The refusal is the server's own, read from `editState` exactly as it arrived.
      expect(store.editState().phase).toBe('failed');
      expect(store.editState().error?.status).toBe(409);
      expect(store.editState().error?.kind).toBe('conflict');
      expect(store.editState().error?.code).toBe('last_admin');
      expect(store.editState().error?.message).toBe(LAST_ADMIN.error.message);
      // The grant landed, so it is named as applied and the row shows it; the revoke did not.
      expect(store.editApplied()).toEqual([{ kind: 'grant', roleId: viewer }]);
      expect(store.memberById(admin.id)?.roles.map((role) => role.id)).toEqual([
        tenantAdmin,
        viewer,
      ]);
      expect(data.membershipGets).toEqual([{ orgId: 'o-1', membershipId: admin.id }]);
    });

    it('leaves the list exactly as it was when the first change is refused', async () => {
      await store.load('o-1');
      const before = store.members();
      data.revokeFailure = typedFailure(LAST_ADMIN, 'conflict', 409);

      const changed = await store.changeRoles(pageMember(0).id, [
        { kind: 'revoke', roleId: roleId('tenant_admin'), intent: createIntent() },
      ]);

      expect(changed).toBe(false);
      expect(store.editState().error?.code).toBe(LAST_ADMIN.error.code);
      expect(store.editApplied()).toEqual([]);
      // Nothing was confirmed, so there is nothing to refresh and nothing to show as applied.
      expect(store.members()).toBe(before);
      expect(data.membershipGets).toEqual([]);
      expect(data.membershipOrgs).toEqual(['o-1']);
    });

    it('surfaces the 403 of the self-escalation rule verbatim', async () => {
      await store.load('o-1');
      const before = store.members();
      data.grantFailure = typedFailure(SELF_ESCALATION, 'forbidden', 403);

      const changed = await store.changeRoles(pageMember(0).id, [
        { kind: 'grant', roleId: roleId('release_approver'), intent: createIntent() },
      ]);

      expect(changed).toBe(false);
      const state = store.editState();
      expect(state.phase).toBe('failed');
      expect(state.error?.kind).toBe('forbidden');
      expect(state.error?.status).toBe(403);
      expect(state.error?.code).toBe(SELF_ESCALATION.error.code);
      expect(state.error?.message).toBe(SELF_ESCALATION.error.message);
      expect(store.editApplied()).toEqual([]);
      expect(store.members()).toBe(before);
    });

    it('reloads the list when the refresh read fails', async () => {
      await store.load('o-1');
      const target = pageMember(1);
      data.membershipGetFailure = typedFailure(SELF_ESCALATION, 'forbidden', 403);

      const changed = await store.changeRoles(target.id, [
        { kind: 'grant', roleId: roleId('viewer'), intent: createIntent() },
      ]);

      expect(changed).toBe(true);
      expect(data.membershipGets).toEqual([{ orgId: 'o-1', membershipId: target.id }]);
      // The row could not be re-read, so the store re-read the list instead of trusting it.
      expect(data.membershipOrgs).toEqual(['o-1', 'o-1']);
      expect(store.phase()).toBe('ready');
      expect(store.editState()).toEqual({ phase: 'done', error: null });
      expect(store.editApplied()).toEqual([{ kind: 'grant', roleId: roleId('viewer') }]);
    });

    it('reloads the list when the refreshed row is no longer on the pages it holds', async () => {
      await store.load('o-1');
      const stranger: MembershipView = { ...pageMember(1), id: 'm-not-listed' };
      data.memberViews.set(stranger.id, stranger);

      const changed = await store.changeRoles(stranger.id, [
        { kind: 'grant', roleId: roleId('viewer'), intent: createIntent() },
      ]);

      expect(changed).toBe(true);
      expect(data.membershipGets).toEqual([{ orgId: 'o-1', membershipId: stranger.id }]);
      expect(data.membershipOrgs).toEqual(['o-1', 'o-1']);
      expect(store.members().map((member) => member.id)).toEqual(PAGE.items.map((it) => it.id));
    });

    it('sends nothing and settles done when the draft equals the server state', async () => {
      await store.load('o-1');

      await expect(store.changeRoles(pageMember(1).id, [])).resolves.toBe(true);

      expect(data.roleRequests).toEqual([]);
      expect(data.membershipGets).toEqual([]);
      expect(store.editState()).toEqual({ phase: 'done', error: null });
      expect(store.editApplied()).toEqual([]);
    });

    it('answers false when no org list is loaded, because there is no scope to address', async () => {
      await expect(store.changeRoles(pageMember(1).id, [])).resolves.toBe(false);

      expect(data.roleRequests).toEqual([]);
      expect(store.editState()).toEqual({ phase: 'idle', error: null });
    });

    it('keeps the attempt submitting until the refresh read settles, then publishes it once', async () => {
      await store.load('o-1');
      const target = pageMember(1);
      const viewer = roleId('viewer');
      // The server confirmed the grant; its own refresh read is the part still on the wire.
      data.membershipGetManual = true;

      const inFlight = store.changeRoles(target.id, [
        { kind: 'grant', roleId: viewer, intent: createIntent() },
      ]);
      // Let the grant's promise chain advance to the refresh read.
      await new Promise((resolve) => setTimeout(resolve, 0));

      expect(data.roleRequests).toEqual([`grant:${viewer}`]);
      expect(data.pendingGets).toHaveLength(1);
      // Busy() is `phase === 'submitting'`: the dialog's Save and Cancel stay inert, so a second
      // save cannot re-send the confirmed change while `diff()` still reads the old row.
      expect(store.editState().phase).toBe('submitting');
      expect(store.editApplied()).toEqual([]);

      data.settleGet(0);
      await expect(inFlight).resolves.toBe(true);

      expect(store.editState()).toEqual({ phase: 'done', error: null });
      expect(store.editApplied()).toEqual([{ kind: 'grant', roleId: viewer }]);
      expect(store.memberById(target.id)?.roles.map((role) => role.id)).toContain(viewer);
    });

    it('keeps the row refresh but drops its own state when the dialog closed mid-flight', async () => {
      await store.load('o-1');
      const target = pageMember(1);
      const viewer = roleId('viewer');
      data.grantManual = true;
      const inFlight = store.changeRoles(target.id, [
        { kind: 'grant', roleId: viewer, intent: createIntent() },
      ]);
      expect(store.editState().phase).toBe('submitting');

      // The user closes the dialog (or another dialog reopens): the attempt is abandoned.
      store.resetMutations();
      data.settleGrant(0);

      await expect(inFlight).resolves.toBe(false);
      expect(store.editState()).toEqual({ phase: 'idle', error: null });
      expect(store.editApplied()).toEqual([]);
      // The change landed server-side, so the row still comes from the server.
      expect(store.memberById(target.id)?.roles.map((role) => role.id)).toEqual([
        roleId('qa_engineer'),
        viewer,
      ]);
    });
  });

  describe('memberById', () => {
    it('answers the row the list holds, and nothing for an id it does not', async () => {
      await store.load('o-1');

      expect(store.memberById(pageMember(1).id)).toBe(store.members()[1]);
      expect(store.memberById('')).toBeUndefined();
      expect(store.memberById('m-unknown')).toBeUndefined();
    });
  });

  describe('a scope change while a mutation is in flight', () => {
    it('keeps the new org’s rows, still answers the token and points the next calls at it', async () => {
      await store.load('o-1');
      data.inviteManual = true;
      const inFlight = store.invite('newhire@acme.test', [], createIntent());
      expect(store.inviteState().phase).toBe('submitting');

      // The scope switches (the page closes its dialogs and reads the new org) while the invite
      // is still on its way to o-1.
      data.pages = [
        { items: EMPTY_PAGE.items, next_cursor: '1' },
        { items: PAGE.items, next_cursor: null },
      ];
      await store.load('o-2');
      data.pendingInvites[0]?.resolve(ISSUED);

      // The token still leaves with the answer (M2): the server will never serve it again, so
      // dropping it here would lose the invitation for good. Who may SHOW it is the dialog's own
      // stale-generation guard, not this store.
      await expect(inFlight).resolves.toEqual({
        membershipId: CREATED.id,
        inviteToken: INVITE_TOKEN,
      });

      // The invitation landed on o-1; its reload must not write into o-2's list, and there is no
      // background read either.
      expect(data.invites.map((entry) => entry.orgId)).toEqual(['o-1']);
      expect(data.membershipOrgs).toEqual(['o-1', 'o-2']);
      expect(store.members()).toEqual(EMPTY_PAGE.items);
      expect(store.hasMore()).toBe(true);
      expect(store.loadError()).toBeNull();

      // The store still addresses o-2 for the next mutation and the next page.
      data.inviteManual = false;
      await store.invite('second@acme.test', [], createIntent());
      await settleBackground();
      expect(data.invites.map((entry) => entry.orgId)).toEqual(['o-1', 'o-2']);

      await store.loadMore();
      expect(data.membershipOrgs).toEqual(['o-1', 'o-2', 'o-2', 'o-2']);
      expect(store.members().map((member) => member.id)).toEqual(
        PAGE.items.map((member) => member.id),
      );
    });

    it('skips the reload of a removal addressed to the org that is no longer shown', async () => {
      await store.load('o-1');
      data.removeManual = true;
      const inFlight = store.remove(PAGE.items[0]?.id ?? '', createIntent());

      data.pages = [EMPTY_PAGE];
      await store.load('o-2');
      data.pendingRemovals[0]?.resolve();

      await expect(inFlight).resolves.toBe(false);

      expect(data.removals.map((entry) => entry.orgId)).toEqual(['o-1']);
      expect(data.membershipOrgs).toEqual(['o-1', 'o-2']);
      expect(store.members()).toEqual(EMPTY_PAGE.items);
      expect(store.removeState()).toEqual({ phase: 'done', error: null });
    });

    it('skips the rotate refresh but still answers the token when the scope switched', async () => {
      await store.load('o-1');
      data.resendManual = true;
      const inFlight = store.resend(PENDING.id, createIntent());
      expect(store.resendState().phase).toBe('submitting');

      data.pages = [EMPTY_PAGE];
      await store.load('o-2');
      data.pendingResends[0]?.resolve(ROTATED);

      // M2: the one-time token is the caller's, whatever the list now shows.
      await expect(inFlight).resolves.toEqual({
        membershipId: PENDING.id,
        inviteToken: ROTATED_TOKEN,
      });

      // No refresh read into another org's list, and the state still settles.
      expect(data.membershipGets).toEqual([]);
      expect(data.membershipOrgs).toEqual(['o-1', 'o-2']);
      expect(store.members()).toEqual(EMPTY_PAGE.items);
      expect(store.resendState()).toEqual({ phase: 'done', error: null });
    });

    it('skips the reload of a cancel addressed to the org that is no longer shown', async () => {
      await store.load('o-1');
      data.cancelManual = true;
      const inFlight = store.cancel(PENDING.id, createIntent());

      data.pages = [EMPTY_PAGE];
      await store.load('o-2');
      data.pendingCancels[0]?.resolve();

      await expect(inFlight).resolves.toBe(false);

      expect(data.cancels.map((entry) => entry.orgId)).toEqual(['o-1']);
      expect(data.membershipOrgs).toEqual(['o-1', 'o-2']);
      expect(store.members()).toEqual(EMPTY_PAGE.items);
      expect(store.cancelState()).toEqual({ phase: 'done', error: null });
    });

    it('skips the refresh of a role change addressed to the org that is no longer shown', async () => {
      await store.load('o-1');
      const target = pageMember(1);
      const viewer = roleId('viewer');
      data.grantManual = true;
      const inFlight = store.changeRoles(target.id, [
        { kind: 'grant', roleId: viewer, intent: createIntent() },
      ]);
      expect(store.editState().phase).toBe('submitting');

      data.pages = [EMPTY_PAGE];
      await store.load('o-2');
      data.settleGrant(0);

      await expect(inFlight).resolves.toBe(false);

      expect(data.grants.map((entry) => entry.orgId)).toEqual(['o-1']);
      // No refresh read and no reload: the list now belongs to another org.
      expect(data.membershipGets).toEqual([]);
      expect(data.membershipOrgs).toEqual(['o-1', 'o-2']);
      expect(store.members()).toEqual(EMPTY_PAGE.items);
      // The state still settles, with the confirmed change named: the dialog is not left busy.
      expect(store.editState()).toEqual({ phase: 'done', error: null });
      expect(store.editApplied()).toEqual([{ kind: 'grant', roleId: viewer }]);
    });
  });

  describe('a rejection that is not the server’s answer', () => {
    it('settles a refused-by-nobody invite as a generic unexpected failure', async () => {
      await store.load('o-1');
      const before = store.members();
      data.inviteFailure = new Error('adapter bug: boom');

      await expect(store.invite('newhire@acme.test', [], createIntent())).resolves.toBeNull();

      const state = store.inviteState();
      expect(state.phase).toBe('failed');
      expect(state.error?.kind).toBe('unexpected');
      expect(state.error?.status).toBe(0);
      expect(state.error?.code).toBe('request_unresolved');
      // The typed failure is the store's own, never the thrown value's text.
      expect(state.error?.message).not.toContain('boom');
      expect(JSON.stringify(state)).not.toContain('boom');
      // Nothing reloaded and nothing was removed from the screen.
      expect(data.membershipOrgs).toEqual(['o-1']);
      expect(store.members()).toBe(before);
      // The dialog can be reopened: it is no longer busy, and a reset starts a new attempt.
      store.resetMutations();
      expect(store.inviteState()).toEqual({ phase: 'idle', error: null });
    });

    it('settles a refused-by-nobody removal as a generic unexpected failure', async () => {
      await store.load('o-1');
      const before = store.members();
      data.removeFailure = new TypeError('adapter bug: boom');

      await expect(store.remove(PAGE.items[0]?.id ?? '', createIntent())).resolves.toBe(false);

      const state = store.removeState();
      expect(state.phase).toBe('failed');
      expect(state.error?.kind).toBe('unexpected');
      expect(state.error?.status).toBe(0);
      expect(state.error?.code).toBe('request_unresolved');
      expect(JSON.stringify(state)).not.toContain('boom');
      expect(data.membershipOrgs).toEqual(['o-1']);
      expect(store.members()).toBe(before);
      // Reopening the remove dialog goes through the same reset.
      store.resetMutations();
      expect(store.removeState()).toEqual({ phase: 'idle', error: null });
    });

    it('settles a refused-by-nobody role change as a generic unexpected failure', async () => {
      await store.load('o-1');
      const before = store.members();
      data.grantFailure = new TypeError('adapter bug: boom');

      await expect(
        store.changeRoles(pageMember(1).id, [
          { kind: 'grant', roleId: roleId('viewer'), intent: createIntent() },
        ]),
      ).resolves.toBe(false);

      const state = store.editState();
      expect(state.phase).toBe('failed');
      expect(state.error?.kind).toBe('unexpected');
      expect(state.error?.status).toBe(0);
      expect(state.error?.code).toBe('request_unresolved');
      // The typed failure is the store's own, never the thrown value's text.
      expect(JSON.stringify(state)).not.toContain('boom');
      // Nothing was confirmed, so nothing is reported as applied and nothing was refreshed.
      expect(store.editApplied()).toEqual([]);
      expect(data.membershipGets).toEqual([]);
      expect(data.membershipOrgs).toEqual(['o-1']);
      expect(store.members()).toBe(before);
      // Reopening the dialog starts clean instead of inheriting the failed attempt.
      store.resetMutations();
      expect(store.editState()).toEqual({ phase: 'idle', error: null });
    });
  });

  it('resetMutations() returns every dialog to idle', async () => {
    await store.load('o-1');
    data.removeFailure = typedFailure(LAST_ADMIN, 'conflict', 409);
    await store.remove(PAGE.items[0]?.id ?? '', createIntent());
    data.revokeFailure = typedFailure(LAST_ADMIN, 'conflict', 409);
    await store.changeRoles(pageMember(0).id, [
      { kind: 'revoke', roleId: roleId('tenant_admin'), intent: createIntent() },
    ]);
    data.resendFailure = typedFailure(INVITE_NOT_PENDING, 'conflict', 409);
    await store.resend(PENDING.id, createIntent());
    data.cancelFailure = typedFailure(INVITE_NOT_PENDING, 'conflict', 409);
    await store.cancel(PENDING.id, createIntent());
    expect(store.editState().phase).toBe('failed');
    expect(store.resendState().phase).toBe('failed');
    expect(store.cancelState().phase).toBe('failed');

    store.resetMutations();

    expect(store.inviteState()).toEqual({ phase: 'idle', error: null });
    expect(store.resendState()).toEqual({ phase: 'idle', error: null });
    expect(store.cancelState()).toEqual({ phase: 'idle', error: null });
    expect(store.removeState()).toEqual({ phase: 'idle', error: null });
    expect(store.editState()).toEqual({ phase: 'idle', error: null });
    expect(store.editApplied()).toEqual([]);
  });

  describe('preview', () => {
    it('previews a built-in role from the live catalogue, flags sensitive grants', async () => {
      await store.load('o-1');

      const preview = store.preview(roleId('qa_engineer'));

      expect(preview.kind).toBe('ready');
      if (preview.kind !== 'ready') {
        return;
      }
      const artifact = preview.groups.find((group) => group.resource === 'artifact');
      expect(artifact?.permissions.map((entry) => entry.code)).toEqual([
        'artifact:read',
        'artifact:read_sensitive',
        'artifact:write',
      ]);
      expect(artifact?.permissions.map((entry) => entry.sensitive)).toEqual([false, true, false]);
      // qa_engineer grants 22 codes; the served page holds nine of them, so thirteen are counted
      // as omitted rather than rendered without a description.
      expect(preview.omitted).toBe(13);
    });

    it('answers unknown-role for an id the store does not hold', async () => {
      await store.load('o-1');

      expect(store.preview('')).toEqual({ kind: 'unavailable', reason: 'unknown-role' });
      expect(store.preview('role-does-not-exist')).toEqual({
        kind: 'unavailable',
        reason: 'unknown-role',
      });
    });

    it('shows the org’s own role as unavailable rather than as an empty grant list', async () => {
      const custom: RoleView = {
        id: 'role-auditor',
        org_id: 'o-1',
        code: 'auditor',
        name: 'Auditor',
        is_builtin: false,
        created_at: '2026-10-02T09:00:00Z',
        version: 1,
      };
      data.roles = [...ROLES, custom];
      await store.load('o-1');

      expect(store.preview(custom.id)).toEqual({ kind: 'unavailable', reason: 'custom-role' });
    });
  });

  it('holds no secret-shaped field in any of its signals (D5, S3)', async () => {
    await store.load('o-1');
    const issued = await store.invite('newhire@acme.test', [roleId('viewer')], createIntent());
    const rotated = await store.resend(PENDING.id, createIntent());
    await store.cancel(PENDING.id, createIntent());
    data.removeFailure = typedFailure(LAST_ADMIN, 'conflict', 409);
    await store.remove(PAGE.items[0]?.id ?? '', createIntent());
    data.revokeFailure = typedFailure(LAST_ADMIN, 'conflict', 409);
    await store.changeRoles(pageMember(0).id, [
      { kind: 'grant', roleId: roleId('viewer'), intent: createIntent() },
      { kind: 'revoke', roleId: roleId('tenant_admin'), intent: createIntent() },
    ]);

    // The two plaintexts the server served were handed to the caller, exactly once each.
    expect(issued).toEqual({ membershipId: CREATED.id, inviteToken: INVITE_TOKEN });
    expect(rotated).toEqual({ membershipId: PENDING.id, inviteToken: ROTATED_TOKEN });

    const keys = new Set<string>();
    const collect = (value: unknown): void => {
      if (Array.isArray(value)) {
        for (const item of value) collect(item);
        return;
      }
      if (value instanceof Set) {
        for (const item of value) collect(item);
        return;
      }
      if (value instanceof Map) {
        for (const item of value.values()) collect(item);
        return;
      }
      if (value !== null && typeof value === 'object') {
        for (const [key, child] of Object.entries(value)) {
          keys.add(key);
          collect(child);
        }
      }
    };
    const signals = [
      store.members(),
      store.roles(),
      store.catalogue(),
      store.phase(),
      store.loadError(),
      store.hasMore(),
      store.matrix(),
      store.inviteState(),
      store.resendState(),
      store.cancelState(),
      store.removeState(),
      store.editState(),
      store.editApplied(),
    ];
    collect(signals);

    // The state was serialised for real: a member, a role and a refusal are in it.
    expect(keys.has('invited_email')).toBe(true);
    expect(keys.has('code')).toBe(true);
    expect([...keys].filter((key) => /token|secret|password|plaintext/i.test(key))).toEqual([]);
    // And the plaintexts themselves are nowhere in it — the token is a return value only.
    const serialized = JSON.stringify(signals);
    expect(serialized).not.toContain(INVITE_TOKEN);
    expect(serialized).not.toContain(ROTATED_TOKEN);
    expect(serialized).not.toContain('evgi_');
    // No field of the store itself is named like a value.
    expect(
      Object.getOwnPropertyNames(store).filter((key) =>
        /token|secret|password|plaintext/i.test(key),
      ),
    ).toEqual([]);
  });
});
