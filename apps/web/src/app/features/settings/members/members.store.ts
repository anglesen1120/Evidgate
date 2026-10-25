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
// Six rules shape every method:
//
// 1. The member list is the data; the role list and the live catalogue are substrate. A failed
//    substrate read degrades — the matrix says its columns are unavailable, the preview says the
//    catalogue is — and never takes the memberships off the screen. A failed member read is the
//    tab's error state, with the server's own `ApiError` and no invented message.
// 2. Only ids and server-returned metadata are kept (task §4 D5). The invited address is personal
//    data (02.1.2 §8): it travels from the dialog to the request body and is never put in a signal
//    of its own, never logged, never a telemetry attribute. Nothing secret-shaped is stored at all,
//    which the `no-secret-field-in-store` rule pins for the whole Settings feature — and the invited
//    token is the case that matters most: `invite` and `resend` answer it as a RETURN value
//    ({@link IssuedInvite}) for the one-time reveal, and no signal, map or log ever holds it.
// 3. Every mutation carries the caller's `Intent` (00 §9.1, ADR-0008 rule 2): invite, resend, cancel
//    and remove take one per button press, and a role change takes one PER CHANGE
//    (`RoleChange.intent`, minted by the edit dialog and keyed by `kind:roleId`), because two
//    different changes under one key are the server's 409 `idempotency_conflict` — the operation is
//    the command type and the hash covers the role. A retry of the same action reuses its own
//    `Idempotency-Key`; the store never mints one.
// 4. The server decides. A refusal is surfaced verbatim — AC2's 409 `last_admin` is shown as the
//    server wrote it, and there is no client-side pre-check that could drift from the guard.
// 5. One scope owns the state. A mutation's reload only runs while the org that mutation addressed
//    is still the org the list shows: a scope switch during the round trip leaves the request
//    landed server-side but makes its reload a write into another org's list. And a mutation
//    always settles its own state, so a rejection that is not the server's answer (a cancellation,
//    a bug) can never leave the dialog submitting forever.
// 6. Roles are the server's, never the dialog's. The edit-roles checkboxes are a draft the dialog
//    owns; a row's role chips and the matrix change only from a membership the server returned, so
//    a change that was refused, or not confirmed yet, is never shown as applied. `editApplied`
//    names the changes this attempt confirmed, in order, so a partial failure is reported as one.

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

/**
 * One role change the edit-roles dialog asks {@link MembersStore.changeRoles} to apply, with the
 * `Intent` the dialog minted for it: the key travels with the change, so a retry of that one
 * request replays the same `Idempotency-Key` (00 §9.1, ADR-0008 rule 2) and a confirmed change is
 * never sent twice under a key the server has already answered.
 */
export interface RoleChange {
  readonly kind: 'grant' | 'revoke';
  readonly roleId: string;
  readonly intent: Intent;
}

/**
 * The part of a {@link RoleChange} the server confirmed: what `editApplied` publishes for the
 * current attempt. The intent is deliberately not part of it — it is transient, and the dialog
 * owns it.
 */
export type AppliedRoleChange = Omit<RoleChange, 'intent'>;

/**
 * What a minted invite answers with (02.1.2 §5): the membership it belongs to and the plaintext
 * link token the server served once. It is a RETURN value only — no signal holds it, no map
 * caches it, and the store never logs it. `inviteToken: null` is the honest answer for a replay
 * (the server never serves a token twice), for an EMPTY or non-string field, and never a failure:
 * the dialogs read `null` as "show the Resend guidance", so an absent token can never become an
 * empty one-time reveal.
 */
export interface IssuedInvite {
  readonly membershipId: string;
  readonly inviteToken: string | null;
}

/** The frozen empty values, so an empty store and a degraded read share one array identity. */
const NO_MEMBERS: readonly MembershipView[] = [];
const NO_ROLES: readonly RoleView[] = [];
const NO_APPLIED: readonly AppliedRoleChange[] = [];

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

/** The server's code for "this membership holds no pending invitation" (resend and cancel). */
const INVITE_NOT_PENDING = 'invite_not_pending';

/**
 * The one-time plaintext of an invite answer as the dialogs must read it: a NON-EMPTY string is
 * the token, and everything else — `null` (an `Idempotency-Key` replay), `''`, or a non-string a
 * broken adapter let through — is ABSENCE. Absence is what makes the dialogs show the "resend to
 * get a new token" notice instead of a one-time reveal of nothing. The value itself stays in the
 * caller's local: this function returns it and stores nothing.
 */
function issuedInviteToken(value: string | null | undefined): string | null {
  return typeof value === 'string' && value.length > 0 ? value : null;
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
  private readonly resendStateSignal = signal<MutationState>(IDLE);
  private readonly cancelStateSignal = signal<MutationState>(IDLE);
  private readonly removeStateSignal = signal<MutationState>(IDLE);
  private readonly editStateSignal = signal<MutationState>(IDLE);
  private readonly editAppliedSignal = signal<readonly AppliedRoleChange[]>(NO_APPLIED);

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
  /**
   * Bumped by every `changeRoles` attempt and by `resetMutations`. A dialog that closed (its
   * attempt is abandoned) or a newer attempt owns the edit state, so an older round trip settles
   * into nothing — the row refresh still runs, because the change landed server-side either way.
   */
  private editAttempt = 0;
  /**
   * The rotate's double-submit lock. It is this private flag, not `resendState().phase`, that
   * decides whether a rotate may start: the state becomes `done` as soon as the server answers
   * (the token is in the caller's hands by then), while the flag stays closed until the row
   * refresh that follows has settled — two rotates would mint two tokens and invalidate the one
   * the user is looking at. `resetMutations()` clears the PUBLISHED state when a dialog closes and
   * deliberately does not touch this flag: only the work settling releases it.
   */
  private resendInFlight = false;

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
  /** The rotate action's state; a 409 `invite_not_pending` lands here verbatim. */
  readonly resendState = this.resendStateSignal.asReadonly();
  /** The cancel action's state; a 409 `invite_not_pending` lands here verbatim. */
  readonly cancelState = this.cancelStateSignal.asReadonly();
  /** The remove dialog's state; AC2's `last_admin` refusal lands here verbatim. */
  readonly removeState = this.removeStateSignal.asReadonly();
  /** The edit-roles dialog's state; a refusal of any one change lands here verbatim. */
  readonly editState = this.editStateSignal.asReadonly();
  /**
   * The changes the server confirmed in the current `changeRoles` attempt, in the order they were
   * sent. Empty before the first attempt and after {@link resetMutations}; on a partial failure it
   * names exactly what landed, so the dialog can say what still has to be done.
   */
  readonly editApplied = this.editAppliedSignal.asReadonly();

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
   * reloads the list from its first page AFTER answering.
   *
   * The body carries `roles` only when the dialog selected at least one: an empty array would be a
   * request for "no roles" that the contract's optional member expresses better by absence. The
   * address is passed straight through to the request and is never stored.
   *
   * The token leaves with the answer, and the reload runs behind it (M2): the plaintext is served
   * exactly once, so a dialog that closes — or a scope that switches — while the list is being read
   * must not cost the caller the only copy that will ever exist. The way back would be a Resend,
   * which invalidates the very token this call minted. Nothing about the reload can fail the
   * mutation: it is a second read of the same list, `load` never rejects, and `load`'s own
   * generation guard drops it when another org (or a newer reload) took the state meanwhile, so it
   * is skipped outright when the scope already moved.
   *
   * @returns the minted invite as soon as the server created the membership, whatever happens to
   *   the list afterwards. Its `inviteToken` is the one-time plaintext the caller reveals, or
   *   `null` when the body carried none (an `Idempotency-Key` replay) or carried an empty/non-string
   *   one — see {@link IssuedInvite}. The call answers `null` itself when it refused, or when the
   *   rejection was not the server's answer at all (a cancellation or a bug, reported as the
   *   generic `unexpected` failure so the dialog is never left submitting).
   */
  async invite(
    email: string,
    roleIds: readonly string[],
    intent: Intent,
  ): Promise<IssuedInvite | null> {
    const orgId = this.orgId;
    if (orgId === null) {
      return null;
    }
    this.inviteStateSignal.set(SUBMITTING);
    let issued: IssuedInvite;
    try {
      const view = await this.data.invite(
        orgId,
        roleIds.length === 0 ? { email } : { email, roles: [...roleIds] },
        intent,
      );
      // Built here and returned, never written to a signal: this local is the only place the
      // plaintext exists inside the store.
      issued = { membershipId: view.id, inviteToken: issuedInviteToken(view.invite_token) };
    } catch (error) {
      this.inviteStateSignal.set({
        phase: 'failed',
        error: isApiRequestError(error) ? error.error : UNRESOLVED_FAILURE,
      });
      return null;
    }
    this.inviteStateSignal.set(SUBMITTED);
    // Behind the answer, never in front of it — and only for the org this call addressed, because
    // `load` would otherwise reset the list another org's own read already put there.
    if (this.orgId === orgId) {
      void this.load(orgId);
    }
    return issued;
  }

  /**
   * Rotates one pending invite (`POST /v1/orgs/{o}/memberships/{m}/resend`), so the previous link
   * stops working, and re-reads that one row AFTER answering — the response carries the membership
   * too, but the row on screen comes from the server's own read, never from the rotate's body.
   *
   * The new `invite_token` is a RETURN value exactly like {@link invite}'s, and it leaves with the
   * answer (M2): the row refresh runs behind it, so closing the reveal while that read is on its
   * way cannot lose the token. A membership that is no longer `pending` is the server's 409
   * `invite_not_pending`, which lands in {@link resendState} verbatim with its own message and
   * code — there is no client-side pre-check — and that code also means the pending row on screen
   * is stale, so the list is re-read once to drop it and its Resend action (L5).
   *
   * The double-submit guard is {@link resendInFlight}, a private flag and NOT `resendState().phase`:
   * the state becomes `done` as soon as the server answers, while the flag stays closed until the
   * row refresh has settled, so a second press within that window answers `null` without sending a
   * request instead of minting a second token.
   *
   * @returns the rotated invite when the server answered; `null` on a refusal, on a rejection that
   *   is not the server's answer (see {@link invite}), or for the double submit.
   */
  async resend(membershipId: string, intent: Intent): Promise<IssuedInvite | null> {
    if (this.resendInFlight) {
      return null;
    }
    const orgId = this.orgId;
    if (orgId === null) {
      return null;
    }
    this.resendInFlight = true;
    this.resendStateSignal.set(SUBMITTING);
    let issued: IssuedInvite;
    try {
      const view = await this.data.resendInvite(orgId, membershipId, intent);
      issued = { membershipId: view.id, inviteToken: issuedInviteToken(view.invite_token) };
    } catch (error) {
      const refusal = isApiRequestError(error) ? error.error : null;
      this.resendStateSignal.set({ phase: 'failed', error: refusal ?? UNRESOLVED_FAILURE });
      this.resendInFlight = false;
      // L5: the server says this membership holds no pending invitation any more, so the pending
      // row the user just pressed is stale. One re-read drops it; the refusal itself stays in
      // `resendState` exactly as it arrived, and the read cannot turn it into a success.
      if (refusal?.code === INVITE_NOT_PENDING && this.orgId === orgId) {
        await this.load(orgId);
      }
      return null;
    }
    this.resendStateSignal.set(SUBMITTED);
    if (this.orgId === orgId) {
      this.releaseResendWhenSettled(this.refreshMember(orgId, membershipId));
    } else {
      // Another org's list is on screen: no refresh read, but the lock still has to open.
      this.resendInFlight = false;
    }
    return issued;
  }

  /**
   * Cancels one pending invite (`POST /v1/orgs/{o}/memberships/{m}/cancel-invite`): the row
   * becomes `revoked` and its stored prefix stops resolving, so the list is reloaded rather than
   * patched. A membership that is not `pending` (already active, already cancelled, removed) is
   * the server's 409 `invite_not_pending`, verbatim in {@link cancelState}; that code also means
   * the pending row on screen is stale, so the list is re-read once to drop it and its Cancel
   * action (L5). Nothing here second-guesses that guard.
   *
   * @returns `true` when the server cancelled the invite and the list on screen is still this
   *   org's; `false` on a refusal, on a rejection that is not the server's answer, or on a scope
   *   switch during the round trip (see {@link invite}).
   */
  async cancel(membershipId: string, intent: Intent): Promise<boolean> {
    const orgId = this.orgId;
    if (orgId === null) {
      return false;
    }
    this.cancelStateSignal.set(SUBMITTING);
    try {
      await this.data.cancelInvite(orgId, membershipId, intent);
    } catch (error) {
      const refusal = isApiRequestError(error) ? error.error : null;
      this.cancelStateSignal.set({ phase: 'failed', error: refusal ?? UNRESOLVED_FAILURE });
      // L5: "no pending invitation" means the row the user pressed Cancel on is stale, so one
      // re-read drops it. The refusal stays verbatim, and the read cannot turn it into a success.
      if (refusal?.code === INVITE_NOT_PENDING && this.orgId === orgId) {
        await this.load(orgId);
      }
      return false;
    }
    this.cancelStateSignal.set(SUBMITTED);
    if (this.orgId !== orgId) {
      return false;
    }
    await this.load(orgId);
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
   * @returns `true` when the server removed the membership and the list on screen is still this
   *   org's; `false` when it refused, when the rejection was not the server's answer at all (see
   *   {@link invite}), or when the scope switched during the round trip.
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
    if (this.orgId !== orgId) {
      return false;
    }
    await this.load(orgId);
    return true;
  }

  /**
   * Applies one member's role changes in the order given — grants first, then revokes, as the
   * dialog builds the list — and stops at the first refusal.
   *
   * Each change arrives with its own `Intent`, so a change the server confirmed is never resent
   * under a key the server has already answered, and one that failed can be retried with the same
   * key. There is no client-side last-admin or self-escalation pre-check (AC2, 02.1.2 §3b): the
   * server's 409 `last_admin`, 409 `unknown_role`, 403 `forbidden` and 404 are the answers, they
   * land in {@link editState} verbatim, and {@link editApplied} keeps the changes that did land —
   * a refused second change leaves the first one applied server-side, so the row on screen has to
   * say so instead of pretending nothing happened.
   *
   * After at least one confirmed change the row is refreshed, never patched optimistically: the
   * server's own membership replaces the row in place (same position, same cursor state), and when
   * that read fails or the row is no longer in the list the whole list is reloaded. The edit state
   * stays `submitting` until that refresh has settled, so the dialog is still busy: publishing the
   * outcome first would open a window in which `diff()` reads the pre-change row while Save is
   * enabled again, and a second save would re-send confirmed changes under their settled
   * `Idempotency-Key`s. A scope switch during the round trips skips that write, exactly like
   * {@link invite} and {@link remove}.
   *
   * Never rejects: the dialog awaits it from a click handler.
   *
   * @returns `true` only when every change was confirmed by the server and the list on screen is
   *   still the org this attempt addressed; `false` otherwise (a refusal, a rejection that is not
   *   the server's answer, a partial apply, or a scope switch).
   */
  async changeRoles(membershipId: string, changes: readonly RoleChange[]): Promise<boolean> {
    const orgId = this.orgId;
    if (orgId === null) {
      return false;
    }
    const attempt = ++this.editAttempt;
    this.editStateSignal.set(SUBMITTING);
    this.editAppliedSignal.set(NO_APPLIED);

    const applied: AppliedRoleChange[] = [];
    let failure: ApiError | null = null;
    for (const change of changes) {
      try {
        if (change.kind === 'grant') {
          await this.data.grantRole(orgId, membershipId, change.roleId, change.intent);
        } else {
          await this.data.revokeRole(orgId, membershipId, change.roleId, change.intent);
        }
      } catch (error) {
        failure = isApiRequestError(error) ? error.error : UNRESOLVED_FAILURE;
        break;
      }
      applied.push({ kind: change.kind, roleId: change.roleId });
    }

    // Deliberately before publishing the outcome: the refresh is part of finishing the attempt, so
    // the dialog stays `submitting` (busy) for its duration.
    if (applied.length > 0) {
      await this.refreshMember(orgId, membershipId);
    }

    // A dialog that closed (and reset the state) or a newer attempt owns the edit state now.
    const current = attempt === this.editAttempt;
    if (current) {
      this.editAppliedSignal.set(applied.length === 0 ? NO_APPLIED : applied);
      this.editStateSignal.set(failure === null ? SUBMITTED : { phase: 'failed', error: failure });
    }
    return current && failure === null && this.orgId === orgId;
  }

  /**
   * The membership the list currently holds, by id — what the edit-roles dialog re-initialises its
   * checkboxes from after a partial apply. `undefined` when the row is not in the loaded pages, so
   * the dialog can fall back to its own draft instead of inventing a role set.
   */
  memberById(membershipId: string): MembershipView | undefined {
    return this.membersSignal().find((member) => member.id === membershipId);
  }

  /**
   * Clears every mutation state, so the next attempt starts from nothing. The edit dialog calls
   * this from its own `open()`, not from `close()`: the draft, the intents and the state it was
   * closed with stay readable until a new edit begins, and a reopening dialog always re-initialises
   * them, so a closed dialog never inherits a busy or failed state that belonged to the previous
   * one. The edit attempt token moves too: a role change still on its way for the dialog that just
   * closed settles into nothing. The rotate LOCK ({@link resendInFlight}) is deliberately not
   * released here: a closing dialog must not reopen the door to a second token from the same
   * request, so only the work settling releases it.
   */
  resetMutations(): void {
    this.editAttempt += 1;
    this.inviteStateSignal.set(IDLE);
    this.resendStateSignal.set(IDLE);
    this.cancelStateSignal.set(IDLE);
    this.removeStateSignal.set(IDLE);
    this.editStateSignal.set(IDLE);
    this.editAppliedSignal.set(NO_APPLIED);
  }

  /**
   * The scope preview of one role (COORDINATOR-DECISION option B): display only, joined from the
   * built-in grant table and the live catalogue. An unknown id answers `unknown-role` — never an
   * empty "grants nothing" panel.
   */
  preview(roleId: string): RolePreview {
    return this.previews().get(roleId) ?? NO_ROLE_PREVIEW;
  }

  /**
   * Re-reads one membership after a confirmed role change or a rotate, and writes the server's row
   * into the list at its existing position, so the chips and the matrix show what the server holds
   * without costing the cursors, the phase or the other rows. The list is reloaded instead when the
   * read fails (a degraded row is worse than a re-read) or when the id is no longer on the pages
   * this store holds (a row that was removed under us). A scope switch skips both.
   *
   * The read is generation-guarded exactly like {@link loadMore}'s page: a `load` that started while
   * this read was on the wire owns the list now, and its rows are newer than this one — writing the
   * read anyway would put a stale row over a fresh list (`resend` fires this refresh detached, so
   * that overlap is reachable without the caller's ordering). Both the replace and the fallback
   * reload are therefore dropped when the generation moved.
   */
  private async refreshMember(orgId: string, membershipId: string): Promise<void> {
    if (this.orgId !== orgId) {
      return;
    }
    const generation = this.generation;
    const read = await settledRead(() => this.data.getMembership(orgId, membershipId));
    // The list on screen may be another org's, or a newer load's, by now.
    if (this.orgId !== orgId || generation !== this.generation) {
      return;
    }
    if (read.ok && this.replaceMember(read.value)) {
      return;
    }
    await this.load(orgId);
  }

  /**
   * Releases {@link resendInFlight} when the rotate's background row refresh settles, either way:
   * the lock exists to keep a second rotate out while this one's work is still open, and a refresh
   * that failed must never leave it closed (a refresh that cannot read the row falls back to a
   * full list read, which also settles here).
   */
  private releaseResendWhenSettled(work: Promise<void>): void {
    const release = (): void => {
      this.resendInFlight = false;
    };
    void work.then(release, release);
  }

  /** Replaces one row by id, keeping server order; `false` when the list does not hold that id. */
  private replaceMember(member: MembershipView): boolean {
    const members = this.membersSignal();
    if (!members.some((candidate) => candidate.id === member.id)) {
      return false;
    }
    this.membersSignal.set(
      members.map((candidate) => (candidate.id === member.id ? member : candidate)),
    );
    return true;
  }
}
