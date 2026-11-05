// The Settings > Policy tab's state: private `signal`s with readonly/computed views, one instance
// per page because the store owns one revision list's cursor. A revision id is the only key: the
// project comes from the revision itself, and a revision this session cannot see is `not_found`,
// the state a `404` renders, so the tab cannot prove another project's revision exists.

import { computed, inject, Injectable, signal } from '@angular/core';
import type {
  PolicyActivationStateDto,
  PolicyRevisionDto,
  PolicyRevisionSummaryDto,
} from '@evidgate/sdk';

import { MAX_CURSOR_PAGES } from '../../../core/data/identity-data.service';
import { PolicyDataService, type PolicyRevisionPage } from '../../../core/data/policy-data.service';
import { ApiRequestError, isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { settledRead } from '../../../core/http/settled-read';
import { SessionStore } from '../../../core/session/session.store';
import { POLICY_BODY_MAX_BYTES, policyBodyBytes } from './policy-body-size';
import { PolicyProjectReads } from './policy-reads';
import { PolicyBodyCache } from './policy-store-bodies';
import {
  MALFORMED_REVISION_FAILURE,
  NO_SCOPE_FAILURE,
  NO_WRITE_FAILURE,
  classifyPublishFailure,
  classifyReadFailure,
  parsePolicyObject,
} from './policy-store-failures';

/** The tab's read state: `idle` before a revision is opened, `not_found` for every refusal. */
export type PolicyPhase = 'idle' | 'loading' | 'ready' | 'error' | 'not_found';

/**
 * What one publish attempt hands its caller. `created`/`unchanged` carry the stored revision, the
 * server's own answer to a `201`/`200`; `conflict` is a `412` with the head the server named;
 * `refused` is every other refusal (a `422`, a `413`, a rate limit, a transport failure) verbatim,
 * never paraphrased; and `too_large`/`invalid_json` are the two local refusals, where nothing at
 * all was sent.
 */
export type PublishResult =
  | { readonly kind: 'created' | 'unchanged'; readonly revision: PolicyRevisionDto }
  | {
      readonly kind: 'conflict';
      readonly error: ApiError;
      readonly currentRevisionId: string | null;
    }
  | { readonly kind: 'refused'; readonly error: ApiError }
  | { readonly kind: 'too_large'; readonly bytes: number }
  | { readonly kind: 'invalid_json' }
  | { readonly kind: 'busy' };

/** The frozen empty rows, so an empty store and a cleared scope share one array identity. */
const NO_REVISIONS: readonly PolicyRevisionSummaryDto[] = [];

/**
 * The shape of a revision id: five hex groups, `8-4-4-4-12`, any version. Shape only — the server
 * decides existence and access — which is what makes a stray URL parameter cost no request and
 * reveal nothing.
 */
const REVISION_ID_SHAPE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

@Injectable()
export class PolicyStore {
  private readonly data = inject(PolicyDataService);
  private readonly session = inject(SessionStore);
  /**
   * The project's activation and head revision, read beside the list. Exposed as is: the summary
   * renders their phases, which are independent of the list's own phase.
   */
  readonly reads = inject(PolicyProjectReads);

  private readonly revisionsSignal = signal<readonly PolicyRevisionSummaryDto[]>(NO_REVISIONS);
  private readonly phaseSignal = signal<PolicyPhase>('idle');
  private readonly loadErrorSignal = signal<ApiError | null>(null);
  private readonly nextCursorSignal = signal<string | null>(null);
  private readonly projectIdSignal = signal<string | null>(null);
  private readonly headRevisionIdSignal = signal<string | null>(null);
  private readonly activationSignal = signal<PolicyActivationStateDto | null>(null);
  /**
   * The double-submit lock. It is this flag, not the absence of an answer, that decides whether a
   * publish may start: the request is out and a second press would append a second revision (or
   * answer `busy` into a dialog that already closed).
   */
  private readonly publishingSignal = signal(false);

  /**
   * The revision id the list route is asked about. The route's path parameter is a revision id —
   * any of the project's — so every page of one list names the same anchor, and a publish (which
   * appends a NEW id) never invalidates it.
   */
  private anchorRevisionId: string | null = null;
  /**
   * The last revision id this store was ASKED to open, kept even when that open failed before it
   * could read the list: it is what {@link reload} retries from, so a failed open does not strand
   * the page's own retry.
   */
  private lastRequestedRevisionId: string | null = null;
  /** The cursors this store already asked for, so a repeated one is an error, not a loop. */
  private readonly followedCursors = new Set<string>();
  /** Pages read for the current anchor, the first page included; bounded by the page cap. */
  private pagesRead = 1;
  /** Bumped by every read; a read of an older generation never writes state. */
  private generation = 0;
  /** The bodies the diff and the publish seed share, and the reads still out for them. */
  private readonly bodies = new PolicyBodyCache(
    (revisionId) => this.data.getRevision(revisionId),
    (generation) => generation === this.generation,
  );

  /** The project's revisions, ascending as served, as many cursor pages as were followed. */
  readonly revisions = this.revisionsSignal.asReadonly();
  /** The read state the page renders. */
  readonly phase = this.phaseSignal.asReadonly();
  /** The server's typed failure for the revision or list read; `null` when it never failed. */
  readonly loadError = this.loadErrorSignal.asReadonly();
  /** The project the loaded list belongs to, from the opened revision; `null` before that. */
  readonly projectId = this.projectIdSignal.asReadonly();
  /** True while the server named another page, so the page's next control can be offered. */
  readonly hasMore = computed(() => this.nextCursorSignal() !== null);
  /**
   * The head revision id the last list answer named (its `ETag`, unquoted) — the value a publish
   * quotes in `If-Match`, and `null` until a list answer says what the head is.
   */
  readonly headRevisionId = this.headRevisionIdSignal.asReadonly();
  /** The listed revision whose `state` is `active`; `null` when the list names none. */
  readonly activeRevision = computed(
    () => this.revisionsSignal().find((revision) => revision.state === 'active') ?? null,
  );
  /**
   * The project's policy activation, as the seam {@link applyActivation} was handed: the served
   * object once {@link PolicyProjectReads} read it, `null` while that read is out or when the
   * project has no activation (or the caller may not read it).
   */
  readonly activation = this.activationSignal.asReadonly();
  /** `policy_activation.mode`, VERBATIM — never derived from a revision's `state`. */
  readonly mode = computed(() => this.activationSignal()?.mode ?? null);
  /** True while a publish request is out; a second {@link publish} answers `busy`. */
  readonly publishing = this.publishingSignal.asReadonly();

  /**
   * Opens the named revision, refuses it unless its project is one this session is in, then reads
   * that project's first revision page. An id that is not UUID-shaped requests nothing, and every
   * refusal is `not_found`, so the tab cannot tell the three refusals apart. Never rejects.
   */
  async open(revisionId: string): Promise<void> {
    const id = revisionId.trim();
    if (!REVISION_ID_SHAPE.test(id)) {
      this.reset();
      return;
    }

    // A reload of a store with no anchor retries from here.
    this.lastRequestedRevisionId = id;
    const generation = ++this.generation;
    this.clearState();
    this.phaseSignal.set('loading');

    const revision = await settledRead(() => this.data.getRevision(id));
    // A newer open (or a reset) owns the state now: this answer describes a revision nobody renders.
    if (generation !== this.generation) {
      return;
    }
    if (!revision.ok) {
      this.settleFailure(revision.error);
      return;
    }

    const projectId = revision.value.project_id;
    if (!this.session.projects().some((project) => project.id === projectId)) {
      // The revision exists and is readable, but it belongs to a project this session is not in.
      // The tab answers exactly what a 404 answers, so it cannot be used to probe for revisions.
      this.phaseSignal.set('not_found');
      return;
    }

    this.projectIdSignal.set(projectId);
    this.startReads(projectId);
    await this.readList(id, generation, true);
  }

  /**
   * Follows `next_cursor` once and appends the page, so the rows already shown stay while the next
   * one is on its way. A repeated cursor or a page past {@link MAX_CURSOR_PAGES} is a contract
   * violation and throws; a typed refusal becomes `loadError` and keeps the rows.
   */
  async loadMore(): Promise<void> {
    const anchor = this.anchorRevisionId;
    const projectId = this.projectIdSignal();
    const cursor = this.nextCursorSignal();
    if (anchor === null || projectId === null || cursor === null) {
      return;
    }
    if (this.followedCursors.has(cursor)) {
      throw new Error(`the policy revision list repeated cursor "${cursor}"`);
    }
    if (this.pagesRead >= MAX_CURSOR_PAGES) {
      throw new Error(`the policy list did not finish within ${MAX_CURSOR_PAGES} pages`);
    }

    const generation = this.generation;
    const read = await settledRead(() => this.data.listRevisions(anchor, cursor));
    // A newer read (another open, a reload, a publish's refresh) owns the state now.
    if (generation !== this.generation) {
      return;
    }
    if (!read.ok) {
      // Only a typed refusal is the server's answer; a contract violation is re-thrown.
      if (!isApiRequestError(read.error)) {
        throw read.error;
      }
      this.loadErrorSignal.set(read.error.error);
      if (this.revisionsSignal().length === 0) {
        this.phaseSignal.set('error');
      }
      return;
    }

    this.followedCursors.add(cursor);
    this.pagesRead += 1;
    const page = read.value;
    this.revisionsSignal.update((rows) => [...rows, ...page.items]);
    this.nextCursorSignal.set(page.nextCursor);
    // Every page of one list names the same head; taking it from the newest answer keeps the
    // `If-Match` source honest if a publish committed elsewhere in between.
    this.headRevisionIdSignal.set(page.headRevisionId);
    // A page that arrived is the answer an earlier refusal withheld: its alert must not outlive it.
    this.loadErrorSignal.set(null);
  }

  /**
   * Re-reads the list from its first page — the reload a `412` conflict dialog offers and the page's
   * own retry — and restarts the project reads with it. With no anchor it retries the open that
   * never reached the list. Never rejects.
   */
  async reload(): Promise<void> {
    const anchor = this.anchorRevisionId;
    if (anchor === null) {
      if (this.lastRequestedRevisionId !== null) {
        await this.open(this.lastRequestedRevisionId);
      }
      return;
    }
    const projectId = this.projectIdSignal();
    if (projectId !== null) {
      this.startReads(projectId);
    }
    const generation = ++this.generation;
    await this.readList(anchor, generation, true);
  }

  /**
   * One revision with its body, cached for the diff and the publish seed. The id must be
   * revision-shaped before any request: a `412` envelope's head id is server text and reaches this
   * one path without the page's own check. REJECTS with the server's own failure.
   */
  async loadBody(revisionId: string): Promise<PolicyRevisionDto> {
    const id = revisionId.trim();
    if (!REVISION_ID_SHAPE.test(id)) {
      throw new ApiRequestError(MALFORMED_REVISION_FAILURE);
    }
    return this.bodies.load(id, this.projectIdSignal(), this.generation);
  }

  /**
   * Publishes the textarea's text. Three local refusals send nothing: no `policy:write`, text that
   * is not a JSON object, and a COMPACT wire body over 65 536 UTF-8 bytes, so indentation the
   * editor shows cannot cost a body the server would accept. A second call in flight is `busy`.
   *
   * `If-Match` is the head the last list answer named. A `412` answers `conflict` and is never
   * retried; any other refusal is `refused` with the server's own error. A stored answer returns
   * before the list is re-read behind it, and a refusal of that re-read cannot fail this call.
   */
  async publish(bodyText: string): Promise<PublishResult> {
    if (this.publishingSignal()) {
      return { kind: 'busy' };
    }
    if (!this.session.has('policy:write')) {
      return { kind: 'refused', error: NO_WRITE_FAILURE };
    }
    const projectId = this.projectIdSignal();
    if (projectId === null) {
      return { kind: 'refused', error: NO_SCOPE_FAILURE };
    }

    const body = parsePolicyObject(bodyText);
    if (body === null) {
      return { kind: 'invalid_json' };
    }
    // The size is the compact wire body's, never the indented text: the cap counts UTF-8 bytes, so
    // a character costs every byte it occupies. `null` is no measurable body, which sends nothing.
    const bytes = policyBodyBytes(bodyText);
    if (bytes === null) {
      return { kind: 'invalid_json' };
    }
    if (bytes > POLICY_BODY_MAX_BYTES) {
      return { kind: 'too_large', bytes };
    }

    // The head is read BEFORE the request: the `If-Match` this attempt quotes is the one the user
    // was shown, and a background refresh landing mid-flight must not rewrite it.
    const ifMatch = this.headRevisionIdSignal();
    this.publishingSignal.set(true);
    try {
      const published = await this.data.publish(projectId, body, ifMatch);
      const answer: PublishResult = {
        kind: published.created ? 'created' : 'unchanged',
        revision: published.revision,
      };
      // The list on screen still shows the old head, so it owes the screen a re-read — but not the
      // caller, who already holds the stored revision. A created revision also supersedes the
      // summary's head read, which still names the previous revision; an unchanged answer wrote
      // nothing, so it leaves that read alone.
      this.refreshBehind(projectId);
      if (published.created) {
        this.reads.refreshHead(projectId);
      }
      return answer;
    } catch (error: unknown) {
      return classifyPublishFailure(error);
    } finally {
      this.publishingSignal.set(false);
    }
  }

  /**
   * The activation seam: the page renders exactly what the server said, and nothing is inferred
   * from a revision's `state`. {@link PolicyProjectReads} calls it with the served object, and with
   * `null` for an absent, forbidden or failed read; nothing else ever writes it.
   */
  applyActivation(activation: PolicyActivationStateDto | null): void {
    this.activationSignal.set(activation);
  }

  /**
   * Forgets everything the view showed: the phase returns to `idle`, the rows, project, head and
   * activation are cleared, the body cache is dropped and any read still in flight is abandoned (its
   * generation is stale, so it writes nothing). The page calls this on destroy.
   *
   * {@link publishing} is deliberately NOT cleared: a request that is still out keeps its lock, so a
   * press arriving after the view is gone cannot append a second revision from one action.
   */
  reset(): void {
    this.generation += 1;
    this.clearState();
    // The bodies and their in-flight reads belong to the view that is gone; an abandoned read still
    // answers its own caller but caches nothing.
    this.bodies.clear();
    this.lastRequestedRevisionId = null;
    this.phaseSignal.set('idle');
  }

  /** The one read every entry point shares (open's list, reload, a publish's refresh). */
  private async readList(anchor: string, generation: number, foreground: boolean): Promise<void> {
    this.anchorRevisionId = anchor;
    this.followedCursors.clear();
    this.nextCursorSignal.set(null);
    this.pagesRead = 1;
    if (foreground) {
      this.phaseSignal.set('loading');
      this.loadErrorSignal.set(null);
    }

    const read = await settledRead(() => this.data.listRevisions(anchor));
    if (generation !== this.generation) {
      return;
    }
    if (!read.ok) {
      // A background refresh must not cost the revision the server stored, and the rows stay as
      // they are; its refusal still goes to the page's alert, because the screen shows a head that
      // is no longer true. The next read that succeeds clears it.
      if (foreground) {
        this.settleFailure(read.error);
      } else if (isApiRequestError(read.error)) {
        this.loadErrorSignal.set(read.error.error);
      }
      return;
    }

    this.applyPage(read.value);
  }

  /** Writes one served page as the list, the cursor and the head the next publish quotes. */
  private applyPage(page: PolicyRevisionPage): void {
    this.revisionsSignal.set(page.items);
    this.nextCursorSignal.set(page.nextCursor);
    this.headRevisionIdSignal.set(page.headRevisionId);
    this.loadErrorSignal.set(null);
    this.phaseSignal.set('ready');
  }

  /** Writes what one failed read means: `not_found`, or `error` with the server's own copy. */
  private settleFailure(error: unknown): void {
    const failure = classifyReadFailure(error);
    if (failure.error !== null) {
      this.loadErrorSignal.set(failure.error);
    }
    this.phaseSignal.set(failure.notFound ? 'not_found' : 'error');
  }

  /**
   * Re-reads the first page WITHOUT the caller waiting for it, and without any way for it to fail
   * the caller: the read owns its generation (a newer read discards it) and swallows its own
   * failure. Skipped when the project on screen is no longer the one the publish addressed.
   */
  private refreshBehind(projectId: string): void {
    const anchor = this.anchorRevisionId;
    if (anchor === null || this.projectIdSignal() !== projectId) {
      return;
    }
    const generation = ++this.generation;
    // `readList` settles its own state; this catch only keeps a rejection from becoming an
    // unhandled one. Nothing here is the caller's failure.
    void this.readList(anchor, generation, false).catch(() => undefined);
  }

  /** The state every new read starts from; the generation is the caller's to bump. */
  private clearState(): void {
    this.revisionsSignal.set(NO_REVISIONS);
    this.nextCursorSignal.set(null);
    this.projectIdSignal.set(null);
    this.headRevisionIdSignal.set(null);
    this.loadErrorSignal.set(null);
    this.activationSignal.set(null);
    // The project's own reads belong to the view too: their generations are bumped there, so an
    // answer still on its way cannot report the project that just left the screen.
    this.reads.clear();
    this.anchorRevisionId = null;
    this.followedCursors.clear();
    this.pagesRead = 1;
  }

  /**
   * Starts the activation and head reads for the project on screen. Not awaited and never rejected:
   * a refused or slow read is a state of the summary, never a reason for the list to wait or fail.
   */
  private startReads(projectId: string): void {
    this.reads.start(projectId, (activation) => this.applyActivation(activation));
  }
}
