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
// - both mutations carry the caller's intent, so a retry reuses the `Idempotency-Key`; a refusal is
//   stored verbatim (AC2's 409 `last_admin`) and leaves the list exactly as it was;
// - no signal holds a secret-shaped field (task §8 S3, the `no-secret-field-in-store` rule's
//   runtime twin), and the invited address is never stored.

import { TestBed } from '@angular/core/testing';
import type {
  ErrorEnvelope,
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
/** A pending invite, as `POST /v1/orgs/{o}/memberships` answers one. */
const CREATED = membershipCreateOk.body as unknown as MembershipView;
/** AC2's refusal: the server's own message and code. */
const LAST_ADMIN = membershipRemoveLastAdmin.body as unknown as ErrorEnvelope;
/** A second invite while one is pending. */
const INVITE_EXISTS = membershipCreateInviteExists.body as unknown as ErrorEnvelope;

function roleId(code: string): string {
  const role = ROLES.find((candidate) => candidate.code === code);
  if (role === undefined) {
    throw new Error(`the roles-list fixture has no ${code} role`);
  }
  return role.id;
}

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
  removeFailure: Error | null = null;
  /** When true, every page names a fresh cursor, so the store's page cap is the only stop. */
  endless = false;
  /** When true, a membership read answers only when the test settles `pending`. */
  manual = false;
  readonly pending: Array<{ orgId: string; resolve: (page: MembershipListResponse) => void }> = [];
  readonly invites: Array<{ orgId: string; body: InviteMemberRequest; intent: Intent }> = [];
  readonly removals: Array<{ orgId: string; membershipId: string; intent: Intent }> = [];
  readonly membershipCursors: Array<string | undefined> = [];
  membershipCalls = 0;
  roleCalls = 0;
  catalogueCalls = 0;

  listMemberships(orgId: string, cursor?: string): Promise<MembershipListResponse> {
    this.membershipCalls += 1;
    this.membershipCursors.push(cursor);
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

  invite(orgId: string, body: InviteMemberRequest, intent: Intent): Promise<MembershipView> {
    this.invites.push({ orgId, body, intent });
    return this.inviteFailure === null
      ? Promise.resolve(CREATED)
      : Promise.reject(this.inviteFailure);
  }

  remove(orgId: string, membershipId: string, intent: Intent): Promise<void> {
    this.removals.push({ orgId, membershipId, intent });
    return this.removeFailure === null ? Promise.resolve() : Promise.reject(this.removeFailure);
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
  });

  describe('invite', () => {
    it('sends { email, roles } with the caller’s intent and reloads the first page', async () => {
      data.pages = [{ items: [...PAGE.items, CREATED], next_cursor: null }];
      await store.load('o-1');
      const intent = createIntent();

      const invited = await store.invite('newhire@acme.test', [roleId('viewer')], intent);

      expect(invited).toBe(true);
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
      // The reload started from the first page, so the new invite is on screen.
      expect(data.membershipCursors).toEqual([undefined, undefined]);
      expect(store.members().map((member) => member.id)).toContain(CREATED.id);
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
      expect(refused).toBe(false);
      expect(store.inviteState()).toEqual({ phase: 'failed', error: refusal.error });

      data.inviteFailure = null;
      const retried = await store.invite('newhire@acme.test', [], intent);

      expect(retried).toBe(true);
      expect(data.invites.map((entry) => entry.intent.idempotencyKey)).toEqual([
        intent.idempotencyKey,
        intent.idempotencyKey,
      ]);
    });

    it('surfaces 409 invite_exists verbatim and leaves the list alone', async () => {
      await store.load('o-1');
      data.inviteFailure = typedFailure(INVITE_EXISTS, 'conflict', 409);

      const invited = await store.invite('newhire@acme.test', [], createIntent());

      expect(invited).toBe(false);
      expect(store.inviteState().phase).toBe('failed');
      expect(store.inviteState().error?.code).toBe(INVITE_EXISTS.error.code);
      expect(store.inviteState().error?.message).toBe(INVITE_EXISTS.error.message);
      // No reload: the refused invite changed nothing on screen.
      expect(data.membershipCursors).toEqual([undefined]);
      expect(store.members()).toHaveLength(PAGE.items.length);
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

  it('resetMutations() returns both dialogs to idle', async () => {
    await store.load('o-1');
    data.removeFailure = typedFailure(LAST_ADMIN, 'conflict', 409);
    await store.remove(PAGE.items[0]?.id ?? '', createIntent());

    store.resetMutations();

    expect(store.inviteState()).toEqual({ phase: 'idle', error: null });
    expect(store.removeState()).toEqual({ phase: 'idle', error: null });
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
    await store.invite('newhire@acme.test', [roleId('viewer')], createIntent());
    data.removeFailure = typedFailure(LAST_ADMIN, 'conflict', 409);
    await store.remove(PAGE.items[0]?.id ?? '', createIntent());

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
    collect([
      store.members(),
      store.roles(),
      store.catalogue(),
      store.phase(),
      store.loadError(),
      store.hasMore(),
      store.matrix(),
      store.inviteState(),
      store.removeState(),
    ]);

    // The state was serialised for real: a member, a role and a refusal are in it.
    expect(keys.has('invited_email')).toBe(true);
    expect(keys.has('code')).toBe(true);
    expect([...keys].filter((key) => /token|secret|password|plaintext/i.test(key))).toEqual([]);
  });
});
