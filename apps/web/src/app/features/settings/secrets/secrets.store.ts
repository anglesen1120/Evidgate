// The Settings > Secrets tab's state, as a plain Angular signals service — private `signal`s,
// public `asReadonly()`/`computed`, no writable state escaping the class — shaped like
// `members/members.store.ts`, because the workspace does not depend on `@ngrx/signals`.
//
// One instance per page (`providers: [SecretsStore]` on the page), because the store owns the
// cursor of one project's list and a project/project-scope switch must not inherit another list.
//
// The token list, its scope and the public surface live here; the two independent flows are
// composed from siblings: `secrets-rotation.ts` owns one rotation at a time, and
// `secrets-ci-bindings.ts` owns the read-only CI-binding track.
//
// These rules shape every method:
//
// 1. Metadata only. `IngestTokenView` is metadata by contract — `name`, `scopes`, `prefix`,
//    `status`, the timestamps — and the state holds nothing else. The rotation's one-time
//    plaintext is a RETURN VALUE of `rotate()`, destined for the component-scoped reveal slot and
//    never written to a signal, a log or a telemetry attribute. The state-shape spec enumerates
//    the store's own field names and serialises every public signal to prove it.
// 2. `status` IS the rotation state. The wire has no `rotation_state` or `last_rotated_at`, so the
//    store derives nothing from them and invents no substitute; the reminder chip is the pure
//    `rotationReminder` over `status` + `expires_at`.
// 3. The project is the scope. A generation counter means an older project's read never writes,
//    and the rows are cleared the moment the scope changes so another project's tokens are never
//    on screen under this project's heading.
// 4. The user reads pages, not a dump. The list follows `next_cursor` one page per `loadMore()`,
//    bounded by {@link MAX_CURSOR_PAGES} and refusing a cursor it already followed — both contract
//    violations throw a plain `Error`, never a partial list passed off as complete.
// 5. One rotation at a time. The route takes no caller-owned `Idempotency-Key` (a one-time secret
//    is never replayed), so the guard cannot be a key: a second `rotate` while one is submitting
//    is ignored, which stops a double click from minting two successors. A `409`
//    (`invalid_transition`, `token_revoked`) is surfaced verbatim — the server decides, and no
//    client-side pre-check could drift from its guard. The same lock is published as `rotating`,
//    so the guard stays observable after `resetRotation()` cleared the published attempt.
// 6. A rejection that is not the server's answer always settles its own state, so the rotate
//    button can never be left submitting by a cancellation or a bug.
// 7. The caller gets the rotation, the screen gets the refresh: the one-time value resolves as
//    soon as the server confirms it, and the re-read started behind it can neither delay nor fail
//    the call. A refusal that proves the row stale re-reads too, so a row that can never rotate
//    again does not keep its Rotate button.
// 8. CI-binding metadata is a SECOND track: independent routes, cursors and failure states, so a
//    refused binding read never turns the token list into an error and vice versa. It is
//    READ-ONLY — the contract has no `GET /ci-bindings/{b}` and the only writes are `PATCH`/
//    `DELETE`, so the store offers no create, update or disable method — and LIST-ONLY, one page
//    per read, bounded by the same page cap. Rows are appended by `id`, so two answers never
//    render one binding twice. Nothing is read on construction: the page starts the first read
//    behind its own `project:read` gate, and a server 403 still renders `forbidden`.

import { computed, inject, Injectable, signal } from '@angular/core';
import type { IngestTokenView } from '@evidgate/sdk';

import { MAX_CURSOR_PAGES } from '../../../core/data/identity-data.service';
import { SecretsDataService } from '../../../core/data/secrets-data.service';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { SecretsCiBindingsTrack } from './secrets-ci-bindings';
import { settledRead } from './secrets-read';
import { SecretsRotationTrack, type RotationResult, type RotationState } from './secrets-rotation';

export type { CiBindingsPhase } from './secrets-ci-bindings';
export type { RotationResult, RotationState };

/** The tab's read state: `idle` before the first read, which the page renders as loading. */
export type SecretsPhase = 'idle' | 'loading' | 'ready' | 'error';

/** The frozen empty values, so an empty store and a cleared scope share one array identity. */
const NO_ROWS: readonly IngestTokenView[] = [];

@Injectable()
export class SecretsStore {
  private readonly data = inject(SecretsDataService);

  private readonly rowsSignal = signal<readonly IngestTokenView[]>(NO_ROWS);
  private readonly phaseSignal = signal<SecretsPhase>('idle');
  private readonly loadErrorSignal = signal<ApiError | null>(null);
  private readonly nextCursorSignal = signal<string | null>(null);
  private readonly projectIdSignal = signal<string | null>(null);

  /**
   * The cursors this store already asked for, so a server that hands one back a second time is a
   * contract violation instead of an endless list of the same rows.
   */
  private readonly followedCursors = new Set<string>();
  /** Pages read for the current project's list, the first page included; bounded by the page cap. */
  private pagesRead = 1;
  /** Bumped by every read; a read of an older generation never writes state. */
  private generation = 0;

  /** One rotation at a time, over the project this store currently has on screen. */
  private readonly rotation = new SecretsRotationTrack(
    this.data,
    () => this.projectIdSignal(),
    (projectId) => this.refreshList(projectId),
  );
  /** The independent, read-only CI-binding track (rule 8). */
  private readonly bindings = new SecretsCiBindingsTrack(this.data);

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
  /** The rotate action's state; see {@link SecretsRotationTrack.state} for what each phase means. */
  readonly rotateState = this.rotation.state;
  /** True while a rotate request is out; see {@link SecretsRotationTrack.rotating}. */
  readonly rotating = this.rotation.rotating;

  /**
   * The project's CI bindings, in server order, as many cursor pages as have been followed.
   * Metadata only: `CiBindingView` never carries the challenge or its hash.
   */
  readonly ciBindings = this.bindings.rows;
  /** The binding list's read state: see {@link CiBindingsPhase} for each phase. */
  readonly ciBindingsPhase = this.bindings.phase;
  /**
   * The server's typed failure for the binding read; `null` when it never failed, and when the
   * rejection was not the server's answer at all (then the phase is `unavailable`).
   */
  readonly ciBindingsError = this.bindings.error;
  /** The project the loaded bindings belong to; `null` before the first read and after a reset. */
  readonly ciBindingsProjectId = this.bindings.projectId;
  /** True while the server named another binding page, so the page's next control can be offered. */
  readonly hasMoreCiBindings = this.bindings.hasMore;

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
   * A scope switch drops the CI-binding track as well, but ONLY when that track belongs to another
   * project: the project is the scope of the whole tab (rule 3), so p-1's bindings must not survive
   * under p-2's heading even if the page has not re-read them yet. The guard matters in the other
   * order — a page that reads the bindings of `p-2` FIRST and then calls `load('p-2')` keeps the
   * rows it has just loaded, because they already belong to the project being loaded.
   *
   * Never rejects: the page calls `void store.load(...)` from an effect, so a rejection would be an
   * unhandled promise rejection. A cancellation or a bug leaves the typed error `null`, and the tab
   * renders the generic error pattern rather than a message the server never sent.
   */
  async load(projectId: string): Promise<void> {
    if (this.projectIdSignal() !== projectId) {
      this.rowsSignal.set(NO_ROWS);
      this.nextCursorSignal.set(null);
      const bindingProjectId = this.bindings.projectId();
      if (bindingProjectId !== null && bindingProjectId !== projectId) {
        this.bindings.reset();
      }
    }
    this.resetRotation();
    await this.read(projectId);
  }

  /**
   * Follows `next_cursor` once and appends the page, so the screen keeps the rows it already shows
   * while the next one is on its way (never "load all").
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
   * Rotates one ingest token and starts a background re-read of the project's list. The one-time
   * value is the call's return value and never state; the whole contract — the double-press guard,
   * which refusals re-read the list, and why this never rejects — is on
   * {@link SecretsRotationTrack.rotate}.
   *
   * @returns the one-time result, or `null` when there is nothing to reveal (see the track method).
   */
  async rotate(tokenId: string): Promise<RotationResult | null> {
    return this.rotation.rotate(tokenId);
  }

  /**
   * Clears the published rotate state, so closing the reveal dialog (or the row menu) starts a fresh
   * attempt, and abandons an attempt still in flight. It clears the state only — NOT the in-flight
   * lock, so a press after the dialog closed cannot mint a second successor; the reasoning is on
   * {@link SecretsRotationTrack.reset}.
   */
  resetRotation(): void {
    this.rotation.reset();
  }

  /**
   * Re-reads the named project's first page WITHOUT the caller waiting for it, and without any way
   * for it to fail the caller: the read owns its generation (a newer read discards it) and settles
   * its own error state, so a slow or refused refresh can never turn a confirmed rotation into a
   * failed one, nor delay the value the user is waiting for.
   *
   * Skipped when the project on screen is no longer the one the action addressed: the list under
   * another heading is not this project's to rewrite.
   */
  private refreshList(projectId: string): void {
    if (this.projectIdSignal() !== projectId) {
      return;
    }
    // `read` is async and settles its own state; this catch only keeps a rejection from becoming
    // an unhandled one. Nothing here is the caller's failure.
    void this.read(projectId).catch(() => undefined);
  }

  /** Reads the named project's first CI-binding page; see {@link SecretsCiBindingsTrack.load}. */
  async loadCiBindings(projectId: string): Promise<void> {
    await this.bindings.load(projectId);
  }

  /** Follows the CI-binding cursor once and appends; see {@link SecretsCiBindingsTrack.loadMore}. */
  async loadMoreCiBindings(): Promise<void> {
    await this.bindings.loadMore();
  }

  /** Drops the whole CI-binding track; see {@link SecretsCiBindingsTrack.reset}. */
  resetCiBindings(): void {
    this.bindings.reset();
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
