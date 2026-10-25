// 09.7.1-T3 (plan 09.7.1 §2 AC3, §4 D5, §5 "secret references" row, §6 ingest-token rotation
// states, §8 S3, §10 T3.1/T3.2; COORDINATOR DECISIONS S82 P1–P5): the Settings > Secrets tab's
// state, as a plain Angular signals service — private `signal`s, public `asReadonly()`/`computed`,
// no writable state escaping the class — shaped exactly like `members/members.store.ts`, because
// the workspace does not depend on `@ngrx/signals`.
//
// One instance per page (`providers: [SecretsStore]` on the page), because the store owns the
// cursor of one project's list and a project/project-scope switch must not inherit another list.
//
// Six rules shape every method:
//
// 1. Metadata only (AC3, S3). `IngestTokenView` is metadata by contract — `name`, `scopes`,
//    `prefix`, `status`, the timestamps — and the state holds nothing else. The rotation's
//    one-time plaintext is a RETURN VALUE of `rotate()`, destined for the component-scoped reveal
//    slot and never written to a signal, a log or a telemetry attribute. The state-shape spec
//    (`secrets.store.spec.ts`) enumerates the store's own field names and serialises every public
//    signal to prove it.
// 2. `status` IS the rotation state (S82 P3). The wire has no `rotation_state` or
//    `last_rotated_at`, so the store derives nothing from them and invents no substitute; the
//    reminder chip is the pure `rotationReminder` over `status` + `expires_at`.
// 3. The project is the scope (S82 P1). The page names a project; a generation counter means an
//    older project's read never writes, and the rows are cleared the moment the scope changes so
//    another project's tokens are never on screen under this project's heading.
// 4. The user reads pages, not a dump (00 §9.1, W7). The list follows `next_cursor` one page per
//    `loadMore()`, bounded by {@link MAX_CURSOR_PAGES} and refusing a cursor it already followed —
//    both contract violations throw a plain `Error`, never a partial list passed off as complete.
// 5. One rotation at a time (S82 P5). The route takes no `Idempotency-Key` (a one-time secret is
//    never replayed), so the guard cannot be a key: a second `rotate` while one is submitting is
//    ignored, which is what stops a double click from minting two successors. A `409`
//    (`invalid_transition`, `token_revoked`) is surfaced verbatim — the server decides, and no
//    client-side pre-check could drift from its guard.
// 6. A rejection that is not the server's answer always settles its own state, so the rotate
//    button can never be left submitting by a cancellation or a bug.

import { computed, inject, Injectable, signal } from '@angular/core';
import type { IngestTokenCreatedResponse, IngestTokenView } from '@evidgate/sdk';

import { MAX_CURSOR_PAGES } from '../../../core/data/identity-data.service';
import { SecretsDataService } from '../../../core/data/secrets-data.service';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';

/** The tab's read state: `idle` before the first read, which the page renders as loading. */
export type SecretsPhase = 'idle' | 'loading' | 'ready' | 'error';

/**
 * The rotate action's state: `submitting` while the request is out, `done` once the server
 * answered with a successor, `failed` with the server's refusal verbatim.
 */
export interface RotationState {
  readonly phase: 'idle' | 'submitting' | 'failed' | 'done';
  /** The token this attempt rotated, so a row can render its own busy state; `null` when idle. */
  readonly tokenId: string | null;
  /** The server's typed failure, verbatim; `null` in every other phase. */
  readonly error: ApiError | null;
}

/**
 * What a successful rotation hands its CALLER — the one-time value included, because the reveal
 * slot is the only place it may go (S82 P3/P6, AC3). It is a return value and never state: no
 * signal, field or log line of the store ever holds it.
 */
export interface RotationResult {
  /** The successor's id, so the row can name what replaced the rotated token. */
  readonly successorId: string;
  /** When the rotated token stops verifying; `null` when the contract sent no overlap window. */
  readonly overlapEndsAt: string | null;
  /** The successor's plaintext, exactly as the server sent it, for the one-time reveal. */
  readonly value: string;
}

/** The frozen empty values, so an empty store and a cleared scope share one array identity. */
const NO_ROWS: readonly IngestTokenView[] = [];

const IDLE_ROTATION: RotationState = { phase: 'idle', tokenId: null, error: null };

/**
 * The typed failure a rotation reports when the rejection is not the server's answer — an adapter
 * cancellation or a bug. `unexpected` is the classification table's "no UI state for this",
 * `status: 0` says no response exists, and no text is taken from the thrown value (a thrown value
 * is not server copy, and its message may carry request data the store must not surface).
 *
 * Unlike the members store's copy this one is NOT retryable: the rotate route takes no caller-owned
 * `Idempotency-Key`, and the interceptor's per-request key is fresh on every attempt, so a retry is
 * not a replay — it mints another successor if the first request did land. The list has to be
 * re-read before a second rotation is offered.
 */
const UNRESOLVED_FAILURE: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: 'request_unresolved',
  message: 'The request did not complete, so nothing was confirmed. Reload the list to check.',
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};

/** One read, settled so a rejection is inspected instead of thrown. */
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
export class SecretsStore {
  private readonly data = inject(SecretsDataService);

  private readonly rowsSignal = signal<readonly IngestTokenView[]>(NO_ROWS);
  private readonly phaseSignal = signal<SecretsPhase>('idle');
  private readonly loadErrorSignal = signal<ApiError | null>(null);
  private readonly nextCursorSignal = signal<string | null>(null);
  private readonly projectIdSignal = signal<string | null>(null);
  private readonly rotateStateSignal = signal<RotationState>(IDLE_ROTATION);

  /**
   * The cursors this store already asked for, so a server that hands one back a second time is a
   * contract violation instead of an endless list of the same rows.
   */
  private readonly followedCursors = new Set<string>();
  /** Pages read for the current project's list, the first page included; bounded by the page cap. */
  private pagesRead = 1;
  /** Bumped by every read; a read of an older generation never writes state. */
  private generation = 0;
  /**
   * Bumped by every `rotate` attempt and by `resetRotation`. A dialog that closed, or a newer
   * attempt, owns the rotate state, so an older round trip settles into nothing — its one-time
   * value is dropped rather than handed to a caller the page no longer shows.
   */
  private rotateAttempt = 0;
  /**
   * The double-submit lock. It is the private flag, not `rotateState().phase`, that decides whether
   * a rotation may start: `resetRotation()` clears the PUBLISHED state (the dialog closed) while the
   * request is still open, and a published-state guard would then let a second press mint a second
   * successor from a single user action. Only the request settling releases this flag.
   */
  private rotateInFlight = false;

  /** The project's ingest tokens, in server order, as many cursor pages as have been followed. */
  readonly rows = this.rowsSignal.asReadonly();
  /** The read state the page renders: loading, the list, or the tab's error state. */
  readonly phase = this.phaseSignal.asReadonly();
  /** The server's typed failure for the token read; `null` when it never failed. */
  readonly loadError = this.loadErrorSignal.asReadonly();
  /** True while the server named another page, so the page's next control can be offered. */
  readonly hasMore = computed(() => this.nextCursorSignal() !== null);
  /** The project the loaded list belongs to; `null` before the first load. */
  readonly projectId = this.projectIdSignal.asReadonly();
  /** The rotate action's state; a 409 `invalid_transition`/`token_revoked` lands here verbatim. */
  readonly rotateState = this.rotateStateSignal.asReadonly();

  /**
   * Reads the named project's first token page and becomes `ready`.
   *
   * Switching the project clears the rows before the read starts: the previous project's tokens
   * must never be rendered under the new project's heading, not even for the duration of the
   * round trip. A reload of the SAME project keeps them, so a rotation's refresh does not blank the
   * list the user is looking at.
   *
   * The rotate state is also cleared, because a rotation belongs to the view that started it.
   *
   * Never rejects: the page calls `void store.load(...)` from an effect, so a rejection would be an
   * unhandled promise rejection. A cancellation or a bug leaves the typed error `null`, and the tab
   * renders the generic error pattern rather than a message the server never sent.
   */
  async load(projectId: string): Promise<void> {
    if (this.projectIdSignal() !== projectId) {
      this.rowsSignal.set(NO_ROWS);
      this.nextCursorSignal.set(null);
    }
    this.resetRotation();
    await this.read(projectId);
  }

  /**
   * Follows `next_cursor` once and appends the page, so the screen keeps the rows it already shows
   * while the next one is on its way (00 §9.1 keyset cursor; never "load all").
   *
   * A cursor already followed, or a {@link MAX_CURSOR_PAGES}-th page that still names another one,
   * is a contract violation and throws a plain `Error` — the same two refusals as
   * `IdentityDataService`, so a broken cursor costs visibility of the bug, not a hung tab. A typed
   * server refusal is not a bug: it becomes `loadError` and the rows already read stay on screen
   * (the error state only takes over when there is nothing to show).
   */
  async loadMore(): Promise<void> {
    const projectId = this.projectIdSignal();
    const cursor = this.nextCursorSignal();
    if (projectId === null || cursor === null) {
      return;
    }
    if (this.followedCursors.has(cursor)) {
      throw new Error(`the ingest-token list repeated cursor "${cursor}"`);
    }
    if (this.pagesRead >= MAX_CURSOR_PAGES) {
      throw new Error(`the ingest-token list did not finish within ${MAX_CURSOR_PAGES} pages`);
    }

    const generation = this.generation;
    const read = await settledRead(() => this.data.listIngestTokens(projectId, cursor));
    // A newer read (another project, or a rotation's refresh) owns the state now: this page
    // belongs to a list nobody renders any more.
    if (generation !== this.generation) {
      return;
    }
    if (!read.ok) {
      // Only a typed refusal is the server's answer; a contract violation is re-thrown.
      if (!isApiRequestError(read.error)) {
        throw read.error;
      }
      this.loadErrorSignal.set(read.error.error);
      if (this.rowsSignal().length === 0) {
        this.phaseSignal.set('error');
      }
      return;
    }

    this.followedCursors.add(cursor);
    this.pagesRead += 1;
    const page = read.value;
    this.rowsSignal.update((rows) => [...rows, ...page.items]);
    this.nextCursorSignal.set(page.next_cursor);
  }

  /**
   * Rotates one ingest token (`POST /v1/ingest-tokens/{t}/rotate`, AC3's rotate action;
   * `ingest_token:write`, S82 P5) and reloads the project's list from its first page.
   *
   * The successor's plaintext is returned to the caller and nowhere else: the caller hands it to
   * the one-time reveal slot, which drops it when the dialog closes. `rotateState` only carries the
   * attempt's phase, the token id and the server's refusal.
   *
   * A second call while one request is still open is IGNORED (`null`) — including after
   * {@link resetRotation} cleared the published state: the route takes no caller-owned
   * `Idempotency-Key` (the interceptor's key is fresh per request), so two requests from one user
   * action would mint two successors.
   *
   * The refusal is the server's, verbatim: `409 invalid_transition` ("already rotated") and
   * `409 token_revoked` reach `rotateState().error` with their own message and code, and the list
   * on screen is left exactly as it was — only a success re-reads it.
   *
   * Never rejects: the row's button awaits it from a click handler.
   *
   * @returns the one-time result when the server minted a successor and the project on screen is
   *   still the one this attempt rotated; `null` when a request is already open, when the store has
   *   no project yet, when the server refused, when the rejection was not the server's answer at all
   *   (reported as the generic `unexpected` failure so the button is never left submitting), or when
   *   the scope moved during the round trip (that value belongs to the project the user left, and
   *   its dialog is gone).
   */
  async rotate(tokenId: string): Promise<RotationResult | null> {
    if (this.rotateInFlight) {
      return null;
    }
    const projectId = this.projectIdSignal();
    if (projectId === null) {
      return null;
    }

    const attempt = ++this.rotateAttempt;
    this.rotateInFlight = true;
    this.rotateStateSignal.set({ phase: 'submitting', tokenId, error: null });
    try {
      return await this.rotateAttempted(tokenId, projectId, attempt);
    } finally {
      this.rotateInFlight = false;
    }
  }

  /** One rotation, from the request to the refresh; the caller owns the in-flight lock. */
  private async rotateAttempted(
    tokenId: string,
    projectId: string,
    attempt: number,
  ): Promise<RotationResult | null> {
    let created: IngestTokenCreatedResponse;
    try {
      created = await this.data.rotateIngestToken(tokenId);
    } catch (error) {
      // An abandoned attempt (the dialog closed, or the scope switched) settles into nothing: the
      // state it would write belongs to a view nobody is looking at any more.
      if (attempt !== this.rotateAttempt) {
        return null;
      }
      this.rotateStateSignal.set({
        phase: 'failed',
        tokenId,
        error: isApiRequestError(error) ? error.error : UNRESOLVED_FAILURE,
      });
      return null;
    }

    if (attempt !== this.rotateAttempt) {
      return null;
    }
    this.rotateStateSignal.set({ phase: 'done', tokenId, error: null });

    const result: RotationResult = {
      successorId: created.token.id,
      overlapEndsAt: created.overlap_ends_at ?? created.token.overlap_ends_at ?? null,
      value: created.secret,
    };

    // The reload runs only while the project this attempt addressed still owns the state: a scope
    // switch during the round trip means the list on screen belongs to another project, and
    // reading this one would write into it. The rotation landed server-side either way.
    if (this.projectIdSignal() === projectId) {
      await this.read(projectId);
    }
    return result;
  }

  /**
   * Clears the published rotate state, so closing the reveal dialog (or the row menu) starts a fresh
   * attempt. An attempt still in flight is abandoned: it settles into nothing and its one-time value
   * is dropped rather than shown in a dialog that is no longer open.
   *
   * It clears the state only — NOT the in-flight lock. A request that is still open keeps holding
   * the lock, so a press that arrives after the dialog closed cannot mint a second successor from
   * the same user action.
   */
  resetRotation(): void {
    this.rotateAttempt += 1;
    this.rotateStateSignal.set(IDLE_ROTATION);
  }

  /**
   * The one read every entry point shares (first load, project switch, rotation refresh). It owns
   * the generation, so whichever read was started last is the one that writes.
   */
  private async read(projectId: string): Promise<void> {
    const generation = ++this.generation;
    this.projectIdSignal.set(projectId);
    this.phaseSignal.set('loading');
    this.loadErrorSignal.set(null);
    this.followedCursors.clear();
    this.nextCursorSignal.set(null);
    this.pagesRead = 1;

    const first = await settledRead(() => this.data.listIngestTokens(projectId));
    // A newer read owns the state now.
    if (generation !== this.generation) {
      return;
    }
    if (!first.ok) {
      if (isApiRequestError(first.error)) {
        this.loadErrorSignal.set(first.error.error);
      }
      // A refresh that failed keeps the rows already on screen — they are this project's; a first
      // read of a scope that has none is the tab's error state.
      if (this.rowsSignal().length === 0) {
        this.rowsSignal.set(NO_ROWS);
        this.phaseSignal.set('error');
      } else {
        this.phaseSignal.set('ready');
      }
      return;
    }

    this.rowsSignal.set(first.value.items);
    this.nextCursorSignal.set(first.value.next_cursor);
    this.phaseSignal.set('ready');
  }
}
