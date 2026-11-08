import { signal } from '@angular/core';
import type { IngestTokenCreatedResponse } from '@evidgate/sdk';

import { SecretsDataService } from '../../../core/data/secrets-data.service';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';

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
 * slot is the only place it may go. It is a return value and never state: no signal, field or log
 * line of the store ever holds it.
 */
export interface RotationResult {
  /** The successor's id, so the row can name what replaced the rotated token. */
  readonly successorId: string;
  /** When the rotated token stops verifying; `null` when the contract sent no overlap window. */
  readonly overlapEndsAt: string | null;
  /** The successor's plaintext, exactly as the server sent it, for the one-time reveal. */
  readonly value: string;
}

const IDLE_ROTATION: RotationState = { phase: 'idle', tokenId: null, error: null };

/**
 * The typed failure a rotation reports when the rejection is not the server's answer — an adapter
 * cancellation or a bug. `unexpected` is the classification table's "no UI state for this",
 * `status: 0` says no response exists, and no text is taken from the thrown value (a thrown value
 * is not server copy, and its message may carry request data the store must not surface).
 *
 * Unlike a members-style copy this one is NOT retryable: the rotate route takes no caller-owned
 * `Idempotency-Key`, and the interceptor's per-request key is fresh on every attempt, so a retry is
 * not a replay — it mints another successor if the first request did land. The list has to be
 * re-read before a second rotation is offered, so the message states that the outcome is unknown
 * rather than naming an action: this kind renders no control of its own on the page.
 */
const UNRESOLVED_FAILURE: ApiError = {
  kind: 'unexpected',
  status: 0,
  code: 'request_unresolved',
  message:
    'The request did not complete, so nothing was confirmed; the token may or may not have rotated.',
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};

/**
 * The typed failure a rotation reports when the answer carries no usable `secret` — an empty
 * string or a value that is not a string at all. The server did rotate (this is a success answer),
 * but the one-time value the reveal exists for is not in it, so there is nothing to show and
 * nothing to retry (a second request would mint a second successor): only a re-read can tell the
 * user what the token is now.
 *
 * It is a STORE-LOCAL failure, not a server envelope: `unexpected` is the classification table's
 * "no UI state for this", the code is spelled like the mapper's own local codes (never a
 * `contracts/openapi.yaml` code), and the message is the store's copy — no text is taken from the
 * answer, and the value it did carry is never quoted, logged or described. The message states the
 * fact and asks for no action: the page offers a control of its own per failure kind, and this kind
 * has none, so copy must not tell the user to press something that is not there.
 */
const UNUSABLE_ANSWER_FAILURE: ApiError = {
  kind: 'unexpected',
  // 200 is the route's only success status (contracts/openapi.yaml), and a body did arrive: the
  // response exists, it is the one-time value in it that does not.
  status: 200,
  code: 'rotation_answer_unusable',
  message:
    'The server rotated the token, but the answer carried no secret to show, so there is nothing to reveal.',
  requestId: null,
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};

/**
 * The two refusals that prove the row on screen is stale rather than merely busy: both are the
 * contract's permanent end states of a token that will never rotate again. This is the one place
 * the track reads a server code, and it does not become a UI state of its own — the refusal still
 * lands in the published state verbatim and renders as the same `failed` attempt; the code only
 * decides whether the list is re-read, which the server's answer has just proven out of date.
 */
const STALE_ROW_CODES: readonly string[] = ['invalid_transition', 'token_revoked'];

/**
 * One rotation at a time, with the state and the lock that make a double press observable. The
 * project the attempt addresses and the background re-read it owes the screen are the owner's; this
 * track owns only the attempt, so the store stays the one place that knows the scope on screen.
 */
export class SecretsRotationTrack {
  private readonly stateSignal = signal<RotationState>(IDLE_ROTATION);
  /**
   * The double-submit lock, published as `rotating`. It is this flag, not the published phase, that
   * decides whether a rotation may start: {@link reset} clears the PUBLISHED attempt state (the
   * dialog closed) while the request is still open, and a published-state guard would then let a
   * second press mint a second successor from a single user action. Only the request settling
   * releases this flag.
   */
  private readonly inFlightSignal = signal(false);
  /**
   * Bumped by every `rotate` attempt and by {@link reset}. A dialog that closed, or a newer
   * attempt, owns the rotate state, so an older round trip settles into nothing — its one-time
   * value is dropped rather than handed to a caller the page no longer shows.
   */
  private attempt = 0;

  /** The rotate action's state; a 409 `invalid_transition`/`token_revoked` lands here verbatim. */
  readonly state = this.stateSignal.asReadonly();
  /**
   * True while a rotate request is out, INCLUDING after {@link reset} cleared the published
   * attempt state: the request is still open, so the guard that ignores a second `rotate()` is
   * still armed, and a caller that only sees the published state would read "idle" while a press is
   * in fact being ignored. It is false the moment the request settles — before the list refresh it
   * started, which is not a rotation of its own.
   */
  readonly rotating = this.inFlightSignal.asReadonly();

  constructor(
    private readonly data: SecretsDataService,
    /** The project on screen, read when an attempt starts and compared when it settles. */
    private readonly currentProjectId: () => string | null,
    /** Starts the store's background re-read of the rotated project's list. */
    private readonly refreshList: (projectId: string) => void,
  ) {}

  /**
   * Rotates one ingest token and starts the owner's background re-read of the project's list.
   *
   * The successor's plaintext is returned to the caller and nowhere else: the caller hands it to
   * the one-time reveal slot, which drops it when the dialog closes. The published state only
   * carries the attempt's phase, the token id and the server's refusal.
   *
   * The one-time value is handed over as soon as the server confirms it — the state becomes `done`
   * and the call resolves without waiting for the list re-read. A slow refresh, or one the server
   * refuses, cannot delay or fail the rotation: `null` here always means "no successor to reveal",
   * never "the list was slow".
   *
   * A second call while one request is still open is IGNORED (`null`) — including after
   * {@link reset} cleared the published state: the route takes no caller-owned `Idempotency-Key`
   * (the interceptor's key is fresh per request), so two requests from one user action would mint
   * two successors. {@link rotating} is true for exactly that window, so the ignored call is
   * observable rather than silent.
   *
   * The refusal is the server's, verbatim: `409 invalid_transition` ("already rotated") and
   * `409 token_revoked` reach the published error with their own message and code, and — because
   * either one proves the row on screen can never rotate again — they re-read the list behind the
   * refusal, so its Rotate button does not survive the answer.
   *
   * An answer that carries no usable `secret` (empty or not a string) is a failed rotation: the
   * successor exists, but nothing can be revealed, so no value reaches the caller and the failure
   * is {@link UNUSABLE_ANSWER_FAILURE} — never a code the server did not send, and never the value
   * itself. The list is re-read behind it, because the row on screen did rotate.
   *
   * Never rejects: the row's button awaits it from a click handler.
   *
   * @returns the one-time result when the server minted a successor and the project on screen is
   *   still the one this attempt rotated; `null` when a request is already open, when the store has
   *   no project yet, when the server refused, when the answer carried no usable secret, when the
   *   rejection was not the server's answer at all (reported as the generic `unexpected` failure so
   *   the button is never left submitting), or when the scope moved during the round trip (that
   *   value belongs to the project the user left, and its dialog is gone).
   */
  async rotate(tokenId: string): Promise<RotationResult | null> {
    if (this.inFlightSignal()) {
      return null;
    }
    const projectId = this.currentProjectId();
    if (projectId === null) {
      return null;
    }

    const attempt = ++this.attempt;
    this.inFlightSignal.set(true);
    this.stateSignal.set({ phase: 'submitting', tokenId, error: null });
    try {
      return await this.rotateAttempted(tokenId, projectId, attempt);
    } finally {
      this.inFlightSignal.set(false);
    }
  }

  /** One rotation, from the request to the refusal; the caller owns the in-flight lock. */
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
      if (attempt !== this.attempt) {
        return null;
      }
      const failure = isApiRequestError(error) ? error.error : UNRESOLVED_FAILURE;
      this.stateSignal.set({ phase: 'failed', tokenId, error: failure });
      // The refusal stays verbatim; the read behind it only removes the Rotate button the server
      // has just proven can never succeed.
      if (STALE_ROW_CODES.includes(failure.code)) {
        this.refreshList(projectId);
      }
      return null;
    }

    if (attempt !== this.attempt) {
      return null;
    }

    // The successor was minted, but the one-time value is the whole point of the answer: without
    // a usable one there is nothing a reveal could show, so the attempt is a failure rather than a
    // "done" that hands an empty string to the dialog. The row on screen is stale all the same —
    // the token DID rotate — so it gets the same re-read a proving 409 gets.
    const value: unknown = created.secret;
    if (typeof value !== 'string' || value.length === 0) {
      this.stateSignal.set({ phase: 'failed', tokenId, error: UNUSABLE_ANSWER_FAILURE });
      this.refreshList(projectId);
      return null;
    }

    this.stateSignal.set({ phase: 'done', tokenId, error: null });

    const result: RotationResult = {
      successorId: created.token.id,
      overlapEndsAt: created.overlap_ends_at ?? created.token.overlap_ends_at ?? null,
      value,
    };

    // The list on screen still shows the rotated row, so it owes the screen a re-read — but not
    // the caller, who already holds the value the server minted. The read owns its generation, so
    // a newer read (a project switch, another rotation) discards it.
    this.refreshList(projectId);
    return result;
  }

  /**
   * Clears the published rotate state, so closing the reveal dialog (or the row menu) starts a fresh
   * attempt, and abandons an attempt still in flight: it settles into nothing and its one-time value
   * is dropped rather than shown in a dialog that is no longer open.
   *
   * It clears the state only — NOT the in-flight lock. A request that is still open keeps holding
   * the lock, so a press that arrives after the dialog closed cannot mint a second successor from
   * the same user action: the published state reads `idle` while {@link rotating} stays true, which
   * is what makes that ignored press observable instead of silent.
   */
  reset(): void {
    this.attempt += 1;
    this.stateSignal.set(IDLE_ROTATION);
  }
}
