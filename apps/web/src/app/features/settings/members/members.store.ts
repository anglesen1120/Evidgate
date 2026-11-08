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
//
// The invite lifecycle (invite, rotate, cancel) lives in `members.invite-flow.ts` and the frozen
// state values with their failure mapping in `members.state.ts`; this class owns the list, the
// scope and the mutation states, and delegates the token work.

import { computed, inject, Injectable, signal } from '@angular/core';
import type { MembershipView, PermissionView, RoleView } from '@evidgate/sdk';

import { MAX_CURSOR_PAGES } from '../../../core/data/identity-data.service';
import { MembersDataService } from '../../../core/data/members-data.service';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import type { Intent } from '../../../core/http/intent';
import { MembersInviteFlow } from './members.invite-flow';
import {
  IDLE,
  NO_APPLIED,
  NO_MEMBERS,
  NO_ROLE_PREVIEW,
  NO_ROLES,
  mutationFailure,
  settledRead,
  SUBMITTED,
  SUBMITTING,
} from './members.state';
import type {
  AppliedRoleChange,
  IssuedInvite,
  MembersMatrix,
  MembersPhase,
  MutationState,
  RoleChange,
} from './members.types';
import { BUILTIN_ROLE_GRANTS, buildRolePreview, type RolePreview } from './role-preview';

// Re-exported because the page, its dialogs and their specs read these off the store.
export type {
  AppliedRoleChange,
  IssuedInvite,
  MembersMatrix,
  MembersPhase,
  MutationState,
  RoleChange,
} from './members.types';

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
   * The invite lifecycle, which owns the rotate's double-submit lock and the one-time token rule;
   * it publishes the three invite states and reads the scope through these ports, so this class
   * stays the only owner of the signals and of `orgId`.
   */
  private readonly invites = new MembersInviteFlow({
    data: this.data,
    currentOrgId: () => this.orgId,
    publishInvite: (state) => this.inviteStateSignal.set(state),
    publishResend: (state) => this.resendStateSignal.set(state),
    publishCancel: (state) => this.cancelStateSignal.set(state),
    reload: (orgId) => this.load(orgId),
    refreshMember: (orgId, membershipId) => this.refreshMember(orgId, membershipId),
  });

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
   * Bumped by every `remove` attempt and by `resetMutations`: a removal whose dialog was reset, or
   * one a newer removal superseded, settles into nothing while its list reload still runs.
   */
  private removeAttempt = 0;

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

  /** `MembersInviteFlow.invite`: the token leaves with the answer, the list reloads behind it. */
  invite(email: string, roleIds: readonly string[], intent: Intent): Promise<IssuedInvite | null> {
    return this.invites.invite(email, roleIds, intent);
  }

  /** `MembersInviteFlow.resend`: answers the new token once, and one rotate at a time. */
  resend(membershipId: string, intent: Intent): Promise<IssuedInvite | null> {
    return this.invites.resend(membershipId, intent);
  }

  /** `MembersInviteFlow.cancel`: reloads the list on success, and drops a stale pending row. */
  cancel(membershipId: string, intent: Intent): Promise<boolean> {
    return this.invites.cancel(membershipId, intent);
  }

  /**
   * Removes one member (`DELETE /v1/orgs/{o}/memberships/{m}`) and reloads the list on success.
   *
   * There is no client-side last-admin pre-check (AC2): the server's 409 `last_admin` is the
   * answer, it reaches `removeState().error` with its own message and code, and the list on screen
   * is left exactly as it was because only a success reloads it — and only while the same org still
   * owns the state (a scope switch during the round trip skips the reload, see {@link invite}).
   *
   * The state is published only while this attempt still owns it: a dialog that opened and reset
   * the states, or a newer removal, keeps its own submitting state and its own answer.
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
    const attempt = ++this.removeAttempt;
    this.removeStateSignal.set(SUBMITTING);
    try {
      await this.data.remove(orgId, membershipId, intent);
    } catch (error) {
      if (attempt === this.removeAttempt) {
        this.removeStateSignal.set({ phase: 'failed', error: mutationFailure(error) });
      }
      return false;
    }
    if (attempt === this.removeAttempt) {
      this.removeStateSignal.set(SUBMITTED);
    }
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
        failure = mutationFailure(error);
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
   * one. Every attempt token moves too — the edit and the removal — so a change or a removal still
   * on its way for the dialog that just closed settles into nothing. The rotate lock lives in the
   * invite flow and is deliberately not released here: a closing dialog must not reopen the door to
   * a second token from the same request, so only the work settling releases it.
   */
  resetMutations(): void {
    this.editAttempt += 1;
    this.removeAttempt += 1;
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
