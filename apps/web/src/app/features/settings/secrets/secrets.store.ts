// Token metadata and CI bindings have independent pages and failure states.
// One-time rotation values remain return values, never stored with list metadata.

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
  private readonly pageSignal = signal(1);
  private readonly pageSizeSignal = signal(20);
  private readonly pagingSignal = signal(false);
  private cursors: (string | undefined)[] = [undefined];
  private failedPage: { cursor: string | undefined; page: number; size: number } | null = null;

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

  /** Only the current server page is retained. */
  readonly rows = this.rowsSignal.asReadonly();
  /** The read state the page renders: loading, the list, or the tab's error state. */
  readonly phase = this.phaseSignal.asReadonly();
  /** The server's typed failure for the token read; `null` when it never failed. */
  readonly loadError = this.loadErrorSignal.asReadonly();
  /** True while the server named another page, so the page's next control can be offered. */
  readonly hasMore = computed(() => this.nextCursorSignal() !== null);
  /** The project the loaded list belongs to; `null` before the first load. */
  readonly projectId = this.projectIdSignal.asReadonly();
  readonly page = this.pageSignal.asReadonly();
  readonly pageSize = this.pageSizeSignal.asReadonly();
  readonly paging = this.pagingSignal.asReadonly();
  /** The rotate action's state; see {@link SecretsRotationTrack.state} for what each phase means. */
  readonly rotateState = this.rotation.state;
  /** True while a rotate request is out; see {@link SecretsRotationTrack.rotating}. */
  readonly rotating = this.rotation.rotating;

  /** Independent CI-binding metadata page. */
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
  readonly ciBindingsPage = this.bindings.page;
  readonly ciBindingsPageSize = this.bindings.pageSize;

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
      this.cursors = [undefined];
      this.pageSignal.set(1);
      this.failedPage = null;
      const bindingProjectId = this.bindings.projectId();
      if (bindingProjectId !== null && bindingProjectId !== projectId) {
        this.bindings.reset();
      }
    }
    this.resetRotation();
    await this.read(projectId);
  }

  /** Follow one opaque cursor without retaining the previous page's rows. */
  async loadMore(): Promise<void> {
    const cursor = this.nextCursorSignal();
    if (!cursor || this.pagingSignal()) return;
    if (this.cursors.includes(cursor)) {
      throw new Error(`the ingest-token list repeated cursor "${cursor}"`);
    }
    if (this.pageSignal() >= MAX_CURSOR_PAGES) {
      throw new Error(`the ingest-token list did not finish within ${MAX_CURSOR_PAGES} pages`);
    }
    await this.move(cursor, this.pageSignal() + 1);
  }

  async previous(): Promise<void> {
    if (this.pageSignal() <= 1 || this.pagingSignal()) return;
    const page = this.pageSignal() - 1;
    await this.move(this.cursors[page - 1], page);
  }

  async setPageSize(size: number): Promise<void> {
    if (![20, 50, 100].includes(size) || size === this.pageSizeSignal() || this.pagingSignal())
      return;
    await this.move(undefined, 1, size);
  }

  async retryPage(): Promise<void> {
    const failed = this.failedPage;
    if (failed && !this.pagingSignal()) await this.move(failed.cursor, failed.page, failed.size);
  }

  private async move(
    cursor: string | undefined,
    page: number,
    size = this.pageSizeSignal(),
  ): Promise<void> {
    const project = this.projectIdSignal();
    if (!project) return;
    const generation = ++this.generation;
    this.pagingSignal.set(true);
    this.loadErrorSignal.set(null);
    const result = await settledRead(() => this.data.listIngestTokens(project, cursor, size));
    if (generation !== this.generation) return;
    this.pagingSignal.set(false);
    this.failedPage = { cursor, page, size };
    if (!result.ok) {
      this.phaseSignal.set(this.rowsSignal().length ? 'ready' : 'error');
      if (!isApiRequestError(result.error)) throw result.error;
      this.loadErrorSignal.set(result.error.error);
      return;
    }
    this.failedPage = null;
    this.pageSizeSignal.set(size);
    this.cursors = [...this.cursors.slice(0, page - 1), cursor];
    this.pageSignal.set(page);
    this.rowsSignal.set(result.value.items);
    this.nextCursorSignal.set(result.value.next_cursor || null);
    this.phaseSignal.set('ready');
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

  /** Follows the independent CI-binding cursor. */
  async loadMoreCiBindings(): Promise<void> {
    await this.bindings.loadMore();
  }

  async previousCiBindings(): Promise<void> {
    await this.bindings.previous();
  }

  async setCiBindingsPageSize(size: number): Promise<void> {
    await this.bindings.setPageSize(size);
  }

  async retryCiBindingsPage(): Promise<void> {
    await this.bindings.retryPage();
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
    const size = this.pageSizeSignal();
    this.projectIdSignal.set(projectId);
    this.phaseSignal.set('loading');
    this.loadErrorSignal.set(null);
    this.pagingSignal.set(false);
    const first = await settledRead(() => this.data.listIngestTokens(projectId, undefined, size));
    // A newer read owns the state now.
    if (generation !== this.generation) {
      return;
    }
    if (!first.ok) {
      this.failedPage = { cursor: undefined, page: 1, size };
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

    this.failedPage = null;
    this.cursors = [undefined];
    this.pageSignal.set(1);
    this.rowsSignal.set(first.value.items);
    this.nextCursorSignal.set(first.value.next_cursor);
    this.phaseSignal.set('ready');
  }
}
