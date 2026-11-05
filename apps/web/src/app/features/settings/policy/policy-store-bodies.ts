// The revision bodies the diff and the publish seed share. A stored revision is immutable, so a
// cached body is never stale and a concurrent read of the same id joins the one already out. Each
// read is checked against the project the CALL was made for, never the one on screen when it lands.

import type { PolicyRevisionDto } from '@evidgate/sdk';

import { ApiRequestError } from '../../../core/http/api-error';
import { FOREIGN_REVISION_FAILURE } from './policy-store-failures';

/**
 * How many bodies the cache holds at once. The oldest read is dropped, because a body is only
 * needed while it is on screen and a project's history is unbounded.
 */
const MAX_CACHED_BODIES = 8;

/** One page's revision bodies, cached for the diff and the publish seed. */
export class PolicyBodyCache {
  private readonly bodies = new Map<string, PolicyRevisionDto>();
  /** The reads still in flight, so the diff and the dialog asking together share one request. */
  private readonly reads = new Map<string, Promise<PolicyRevisionDto>>();

  /**
   * `readRevision` is the adapter call; `isCurrent` decides whether a settled answer still belongs
   * to the view that asked, so a reset or a newer read caches nothing.
   */
  constructor(
    private readonly readRevision: (revisionId: string) => Promise<PolicyRevisionDto>,
    private readonly isCurrent: (generation: number) => boolean,
  ) {}

  /**
   * One body, from the cache when it is there and from the server otherwise.
   *
   * REJECTS with the server's own failure, and with `not_found` for a body of another project than
   * `expectedProjectId` — the project the CALL was made for, captured before anything is read, so a
   * page that switches project mid-read still gets the legitimate body of the project that asked.
   * `null` means nothing was open, so there is nothing to compare against.
   */
  async load(
    id: string,
    expectedProjectId: string | null,
    generation: number,
  ): Promise<PolicyRevisionDto> {
    const cached = this.bodies.get(id);
    if (cached !== undefined) {
      const body = this.bodyOfRequestedProject(cached, expectedProjectId);
      // Re-inserted so the entry just served counts as the newest read.
      this.bodies.delete(id);
      this.bodies.set(id, cached);
      return body;
    }
    const shared = this.reads.get(id);
    if (shared !== undefined) {
      // The joined caller was asked for this revision too, so its own captured project decides.
      return shared.then((revision) => this.bodyOfRequestedProject(revision, expectedProjectId));
    }
    const read = this.readRevision(id).then((revision) => {
      const body = this.bodyOfRequestedProject(revision, expectedProjectId);
      if (this.isCurrent(generation)) {
        this.cache(revision);
      }
      return body;
    });
    this.reads.set(id, read);
    try {
      return await read;
    } finally {
      // Only this read's own entry: a later call may already have started the next one.
      if (this.reads.get(id) === read) {
        this.reads.delete(id);
      }
    }
  }

  /**
   * Drops every cached body and abandons the reads still out: they belong to a view that is gone.
   * An abandoned read still answers its own caller, but it caches nothing.
   */
  clear(): void {
    this.bodies.clear();
    this.reads.clear();
  }

  private bodyOfRequestedProject(
    revision: PolicyRevisionDto,
    expectedProjectId: string | null,
  ): PolicyRevisionDto {
    if (expectedProjectId !== null && revision.project_id !== expectedProjectId) {
      throw new ApiRequestError(FOREIGN_REVISION_FAILURE);
    }
    return revision;
  }

  /** Stores one read body as the newest, dropping the oldest read past the cap. */
  private cache(revision: PolicyRevisionDto): void {
    this.bodies.delete(revision.id);
    this.bodies.set(revision.id, revision);
    while (this.bodies.size > MAX_CACHED_BODIES) {
      const oldest = this.bodies.keys().next();
      if (oldest.done === true) {
        break;
      }
      this.bodies.delete(oldest.value);
    }
  }
}
