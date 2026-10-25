// 09.7.1-T2 (plan 09.7.1 §4 D5, §5 members row, §6 Membership/Invite rows, §8 S3; AC2,
// COORDINATOR-DECISION S73 option B): the Members and roles tab's state, as a plain Angular
// signals service — the workspace does not depend on `@ngrx/signals`, so the store is shaped like
// a `signalStore` (readonly signals out, methods in) without the package, exactly like
// `core/session/session.store.ts`: private `signal`s, public `asReadonly()`/`computed`, and no
// writable state escaping the class.
//
// One instance per page (`providers: [MembersStore]` on `MembersPage`), because the store owns the
// cursor of one org's list and a tab switch must not inherit another page's rows.
//
// Four rules shape every method:
//
// 1. The member list is the data; the role list and the live catalogue are substrate. A failed
//    substrate read degrades — the matrix says its columns are unavailable, the preview says the
//    catalogue is — and never takes the memberships off the screen. A failed member read is the
//    tab's error state, with the server's own `ApiError` and no invented message.
// 2. Only ids and server-returned metadata are kept (task §4 D5). The invited address is personal
//    data (02.1.2 §8): it travels from the dialog to the request body and is never put in a signal
//    of its own, never logged, never a telemetry attribute. Nothing secret-shaped is stored at all,
//    which the `no-secret-field-in-store` rule pins for the whole Settings feature.
// 3. Both mutations carry the caller's `Intent` (00 §9.1, ADR-0008 rule 2): the dialog mints one
//    per button press, so a retry of the same action reuses the `Idempotency-Key` instead of
//    inviting or removing twice. The store never mints a key of its own.
// 4. The server decides. A refusal is surfaced verbatim — AC2's 409 `last_admin` is shown as the
//    server wrote it, and there is no client-side pre-check that could drift from the guard.
// 5. One scope owns the state. A mutation's reload only runs while the org that mutation addressed
//    is still the org the list shows: a scope switch during the round trip leaves the request
//    landed server-side but makes its reload a write into another org's list. And a mutation
//    always settles its own state, so a rejection that is not the server's answer (a cancellation,
//    a bug) can never leave the dialog submitting forever.

import { computed, inject, Injectable, signal } from '@angular/core';
import type { MembershipView, PermissionView, RoleView } from '@evidgate/sdk';

import { MAX_CURSOR_PAGES } from '../../../core/data/identity-data.service';
import { MembersDataService } from '../../../core/data/members-data.service';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import type { Intent } from '../../../core/http/intent';
import { BUILTIN_ROLE_GRANTS, buildRolePreview, type RolePreview } from './role-preview';

/** The tab's read state: `idle` before the first read, which the page renders as loading. */
export type MembersPhase = 'idle' | 'loading' | 'ready' | 'error';

/** One mutation's state: the dialog's busy flag, its retry decision and the server's refusal. */
export interface MutationState {
  /**
   * `failed` freezes the attempt: the dialog keeps the entered data and the same intent, so a retry
   * replays the original `Idempotency-Key`; closing it starts a new attempt with a new intent.
   */
  readonly phase: 'idle' | 'submitting' | 'failed' | 'done';
  /** The server's typed failure, verbatim; `null` in every other phase. */
  readonly error: ApiError | null;
}

/** The members x roles table the page renders, roles being org-wide (02.1.2 §6). */
export interface MembersMatrix {
  /** The columns: every role the server listed, in server order. Empty = catalogue unavailable. */
  readonly columns: readonly RoleView[];
  /** One row per membership, in server order, with the role ids it currently holds. */
  readonly rows: readonly {
    readonly membership: MembershipView;
    readonly granted: ReadonlySet<string>;
  }[];
}

/** The frozen empty values, so an empty store and a degraded read share one array identity. */
const NO_MEMBERS: readonly MembershipView[] = [];
const NO_ROLES: readonly RoleView[] = [];

const IDLE: MutationState = { phase: 'idle', error: null };
const SUBMITTING: MutationState = { phase: 'submitting', error: null };
const SUBMITTED: MutationState = { phase: 'done', error: null };

/** What the preview of an id the store does not know answers, without building a new object. */
const NO_ROLE_PREVIEW: RolePreview = { kind: 'unavailable', reason: 'unknown-role' };

/**
 * The typed failure a mutation reports when the rejection is not the server's answer — an adapter
 * cancellation or a bug. `unexpected` is the classification table's "no UI state for this",
 * `status: 0` says no response exists, and no text is taken from the thrown value (a thrown value
 * is not server copy, and its message may carry request data the store must not surface).
 */
const UNRESOLVED_FAILURE: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: 'request_unresolved',
  message: 'The request did not complete, so nothing was confirmed. Try again.',
  requestId: null,
  // Retrying the same intent is safe: the dialog replays the original `Idempotency-Key`.
  retryable: true,
  details: [],
  retryAfterSeconds: null,
};

/** One independent read of `load`, settled so one failure cannot cancel the other two. */
type SettledRead<T> =
  { readonly ok: true; readonly value: T } | { readonly ok: false; readonly error: unknown };

async function settledRead<T>(read: () => Promise<T>): Promise<SettledRead<T>> {
  try {
    return { ok: true, value: await read() };
  } catch (error) {
    return { ok: false, error };
  }
}

@Injectable()
export class MembersStore {
  private readonly data = inject(MembersDataService);

  private readonly membersSignal = signal<readonly MembershipView[]>(NO_MEMBERS);
  private readonly rolesSignal = signal<readonly RoleView[]>(NO_ROLES);
  private readonly catalogueSignal = signal<readonly PermissionView[] | null>(null);
  private readonly phaseSignal = signal<MembersPhase>('idle');
  private readonly loadErrorSignal = signal<ApiError | null>(null);
  private readonly nextCursorSignal = signal<string | null>(null);
  private readonly inviteStateSignal = signal<MutationState>(IDLE);
  private readonly removeStateSignal = signal<MutationState>(IDLE);

  /**
   * The cursors this store already asked for, so a server that hands one back a second time is a
   * contract violation instead of an endless list of the same rows.
   */
  private readonly followedCursors = new Set<string>();
  /** Pages read for the current org's list, the first page included; bounded by the page cap. */
  private pagesRead = 1;
  /** The org the loaded list belongs to. A mutation reloads from it, so the scope cannot drift. */
  private orgId: string | null = null;
  /** Bumped by every `load`; a read of an older generation never writes state. */
  private generation = 0;

  /** The org's memberships, in server order, as many cursor pages as have been followed. */
  readonly members = this.membersSignal.asReadonly();
  /** The roles the server listed — built-ins plus the org's own rows, in server order. */
  readonly roles = this.rolesSignal.asReadonly();
  /** The live permission catalogue, or `null` when that read failed (the preview says so). */
  readonly catalogue = this.catalogueSignal.asReadonly();
  /** The read state the page's `[data-members-phase]` publishes. */
  readonly phase = this.phaseSignal.asReadonly();
  /** The server's typed failure for the member read; `null` when it never failed. */
  readonly loadError = this.loadErrorSignal.asReadonly();
  /** True while the server named another page, so the page's next control can be offered. */
  readonly hasMore = computed(() => this.nextCursorSignal() !== null);
  /**
   * Members x roles read columns-first: every granted cell comes from the role objects the server
   * put on the membership, so a cell is never inferred from a role *name* or code.
   */
  readonly matrix = computed<MembersMatrix>(() => ({
    columns: this.rolesSignal(),
    rows: this.membersSignal().map((membership) => ({
      membership,
      granted: new Set(membership.roles.map((role) => role.id)),
    })),
  }));
  /** The invite dialog's state. */
  readonly inviteState = this.inviteStateSignal.asReadonly();
  /** The remove dialog's state; AC2's `last_admin` refusal lands here verbatim. */
  readonly removeState = this.removeStateSignal.asReadonly();

  /**
   * Every role's preview for the current role list and catalogue. Computed once per substrate
   * change so the panel's input keeps one identity per state instead of a new object per render.
   */
  private readonly previews = computed<ReadonlyMap<string, RolePreview>>(() => {
    const catalogue = this.catalogueSignal();
    const previews = new Map<string, RolePreview>();
    for (const role of this.rolesSignal()) {
      previews.set(role.id, buildRolePreview(role, catalogue, BUILTIN_ROLE_GRANTS));
    }
    return previews;
  });

  /**
   * Reads the org's first membership page, the whole role list and the permission catalogue from
   * the first page, then becomes `ready`.
   *
   * The three reads are independent, so they run together and settle separately (`settledRead`):
   * a `403` on the catalogue must not hide a member list the caller is allowed to see. A substrate
   * failure of ANY kind degrades — a swallowed contract violation would be a lie in the other
   * direction, and the matrix/preview already say "unavailable" when their input is missing.
   *
   * Never rejects: the page calls `void store.load(...)` from an effect, so a rejection would be an
   * unhandled promise rejection. A cancellation or a bug leaves the typed error `null`, and the tab
   * renders the generic error pattern rather than a message the server never sent.
   */
  async load(orgId: string): Promise<void> {
    const generation = ++this.generation;
    this.orgId = orgId;
    this.phaseSignal.set('loading');
    this.loadErrorSignal.set(null);
    this.followedCursors.clear();
    this.nextCursorSignal.set(null);
    this.pagesRead = 1;

    const [membersRead, rolesRead, catalogueRead] = await Promise.all([
      settledRead(() => this.data.listMemberships(orgId)),
      settledRead(() => this.data.listRoles()),
      settledRead(() => this.data.listPermissions()),
    ]);
    // A newer load (another org, or a reload after a mutation) owns the state now.
    if (generation !== this.generation) {
      return;
    }

    this.membersSignal.set(membersRead.ok ? membersRead.value.items : NO_MEMBERS);
    this.nextCursorSignal.set(membersRead.ok ? membersRead.value.next_cursor : null);
    this.rolesSignal.set(rolesRead.ok ? rolesRead.value : NO_ROLES);
    this.catalogueSignal.set(catalogueRead.ok ? catalogueRead.value : null);

    if (membersRead.ok) {
      this.phaseSignal.set('ready');
      return;
    }
    this.phaseSignal.set('error');
    if (isApiRequestError(membersRead.error)) {
      this.loadErrorSignal.set(membersRead.error.error);
    }
  }

  /**
   * Follows the member list's `next_cursor` once and appends the page, so the screen keeps the rows
   * it already shows while the next one is on its way (00 §9.1 keyset cursor; never "load all").
   *
   * A cursor already followed, or a {@link MAX_CURSOR_PAGES}-th page that still names another one,
   * is a contract violation and throws a plain `Error` — the same two refusals as
   * `IdentityDataService`, so a broken cursor costs visibility of the bug, not a hung tab. A typed
   * server refusal is not a bug: it becomes `loadError` and the rows already read stay on screen
   * (the error state only takes over when there is nothing to show).
   */
  async loadMore(): Promise<void> {
    const orgId = this.orgId;
    const cursor = this.nextCursorSignal();
    if (orgId === null || cursor === null) {
      return;
    }
    if (this.followedCursors.has(cursor)) {
      throw new Error(`the membership list repeated cursor "${cursor}"`);
    }
    if (this.pagesRead >= MAX_CURSOR_PAGES) {
      throw new Error(`the membership list did not finish within ${MAX_CURSOR_PAGES} pages`);
    }

    const generation = this.generation;
    const read = await settledRead(() => this.data.listMemberships(orgId, cursor));
    // A reload (another org, or a mutation's reload) owns the state now: this page belongs to a
    // list nobody renders any more.
    if (generation !== this.generation) {
      return;
    }
    if (!read.ok) {
      // Only a typed refusal is the server's answer; a contract violation is re-thrown.
      if (!isApiRequestError(read.error)) {
        throw read.error;
      }
      this.loadErrorSignal.set(read.error.error);
      if (this.membersSignal().length === 0) {
        this.phaseSignal.set('error');
      }
      return;
    }

    this.followedCursors.add(cursor);
    this.pagesRead += 1;
    const page = read.value;
    this.membersSignal.update((members) => [...members, ...page.items]);
    this.nextCursorSignal.set(page.next_cursor);
  }

  /**
   * Invites one address, optionally with initial roles (`POST /v1/orgs/{o}/memberships`), and
   * reloads the list from its first page on success.
   *
   * The body carries `roles` only when the dialog selected at least one: an empty array would be a
   * request for "no roles" that the contract's optional member expresses better by absence. The
   * address is passed straight through to the request and is never stored.
   *
   * The reload runs only while the org this call addressed still owns the state: a scope switch
   * during the round trip means the list on screen is another org's, and `load(orgId)` would reset
   * `orgId`, the generation and the rows that org's own read already put there. The invitation
   * landed server-side either way, so the mutation state still settles.
   *
   * @returns `true` when the server created the membership; `false` when it refused, or when the
   *   rejection was not the server's answer at all (a cancellation or a bug), which is reported as
   *   the generic `unexpected` failure so the dialog is never left submitting.
   */
  async invite(email: string, roleIds: readonly string[], intent: Intent): Promise<boolean> {
    const orgId = this.orgId;
    if (orgId === null) {
      return false;
    }
    this.inviteStateSignal.set(SUBMITTING);
    try {
      await this.data.invite(
        orgId,
        roleIds.length === 0 ? { email } : { email, roles: [...roleIds] },
        intent,
      );
    } catch (error) {
      this.inviteStateSignal.set({
        phase: 'failed',
        error: isApiRequestError(error) ? error.error : UNRESOLVED_FAILURE,
      });
      return false;
    }
    this.inviteStateSignal.set(SUBMITTED);
    if (this.orgId === orgId) {
      await this.load(orgId);
    }
    return true;
  }

  /**
   * Removes one member (`DELETE /v1/orgs/{o}/memberships/{m}`) and reloads the list on success.
   *
   * There is no client-side last-admin pre-check (AC2): the server's 409 `last_admin` is the
   * answer, it reaches `removeState().error` with its own message and code, and the list on screen
   * is left exactly as it was because only a success reloads it — and only while the same org still
   * owns the state (a scope switch during the round trip skips the reload, see {@link invite}).
   *
   * @returns `true` when the server removed the membership; `false` when it refused, or when the
   *   rejection was not the server's answer at all (a cancellation or a bug), reported as the
   *   generic `unexpected` failure so the dialog is never left submitting.
   */
  async remove(membershipId: string, intent: Intent): Promise<boolean> {
    const orgId = this.orgId;
    if (orgId === null) {
      return false;
    }
    this.removeStateSignal.set(SUBMITTING);
    try {
      await this.data.remove(orgId, membershipId, intent);
    } catch (error) {
      this.removeStateSignal.set({
        phase: 'failed',
        error: isApiRequestError(error) ? error.error : UNRESOLVED_FAILURE,
      });
      return false;
    }
    this.removeStateSignal.set(SUBMITTED);
    if (this.orgId === orgId) {
      await this.load(orgId);
    }
    return true;
  }

  /** Clears both mutation states, so closing a dialog starts a new attempt with a new intent. */
  resetMutations(): void {
    this.inviteStateSignal.set(IDLE);
    this.removeStateSignal.set(IDLE);
  }

  /**
   * The scope preview of one role (COORDINATOR-DECISION option B): display only, joined from the
   * built-in grant table and the live catalogue. An unknown id answers `unknown-role` — never an
   * empty "grants nothing" panel.
   */
  preview(roleId: string): RolePreview {
    return this.previews().get(roleId) ?? NO_ROLE_PREVIEW;
  }
}
