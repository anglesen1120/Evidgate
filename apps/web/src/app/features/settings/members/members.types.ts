// The Members and roles tab's public types: what the store publishes, what the edit-roles dialog
// hands back and what an invite answers with. One declaration per shape, shared by the store, its
// invite flow and the dialogs, and re-exported from `members.store.ts` for their readers.

import type { MembershipView, RoleView } from '@evidgate/sdk';

import type { ApiError } from '../../../core/http/api-error';
import type { Intent } from '../../../core/http/intent';

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

/** The members x roles table the page renders, roles being org-wide. */
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
 * One role change the edit-roles dialog asks `changeRoles` to apply, with the `Intent` minted for
 * it: the key travels with the change, so a retry of that one request replays the same
 * `Idempotency-Key` (ADR-0008 rule 2) and a confirmed change is never sent twice.
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
 * What a minted invite answers with: the membership it belongs to and the one-time plaintext the
 * server served. A RETURN value only — no signal, map or log holds it — and `inviteToken: null` is
 * absence (a replay, an empty or non-string field), which the dialogs read as "offer Resend".
 */
export interface IssuedInvite {
  readonly membershipId: string;
  readonly inviteToken: string | null;
}
