// The org's usage summary read: one `resource` keyed on the active organization, and the rule that
// answers a refusal arriving while a read is in flight. It lives beside the store rather than in
// it: the store owns the state and the computed views, this owns the request.
//
// Nothing is requested until a consumer calls `load()`, and `load()` is idempotent — the badge and
// the tab both call it. A refusal that lands mid-read asks for one trailing read when that read
// settles, resolved or failed, and a burst still costs one.

import { resource, type ResourceRef } from '@angular/core';
import type { UsageSummaryDto } from '@evidgate/sdk';

import { QuotaDataService } from '../../../core/data/quota-data.service';

/** One read reported instead of thrown: a failed read still answers a refusal that landed. */
type ReadOutcome = { readonly value: UsageSummaryDto | null } | { readonly error: unknown };

/** What the read needs from the store: the request key, and the first-call flag it may set. */
export interface QuotaSummaryReadOptions {
  /** The organization to read, or `null` while nothing may be read (no tenant, no permission). */
  readonly scope: () => string | null;
  /** The active organization, kept with a trailing read so it never runs against the next one. */
  readonly orgId: () => string | null;
  /** Whether `load()` or `reload()` already started the read. */
  readonly started: () => boolean;
  /** Records that the read started; the store owns the state this sets. */
  readonly start: () => void;
  readonly data: QuotaDataService;
}

export interface QuotaSummaryRead {
  readonly summary: ResourceRef<UsageSummaryDto | null | undefined>;
  /**
   * Idempotent: the first call starts the read, later calls ask for the same request.
   */
  readonly load: () => void;
  /**
   * Re-reads the summary. `resource.reload()` refuses while a read is in flight, so that refusal
   * is not dropped: it asks for exactly one trailing read, and a burst still costs one.
   */
  readonly reload: () => void;
}

/**
 * One read, reported instead of thrown: a failed read still has to answer the refusal that
 * landed while it was in flight, which a throw would skip.
 */
async function readOnce(
  data: QuotaDataService,
  params: string,
  abortSignal: AbortSignal,
): Promise<ReadOutcome> {
  try {
    return { value: await data.summary(params, abortSignal) };
  } catch (error) {
    return { error };
  }
}

/** The summary read and the two entry points that trigger it, in the caller's injection context. */
export function quotaSummaryRead(options: QuotaSummaryReadOptions): QuotaSummaryRead {
  const { scope, orgId, started, start, data } = options;

  // A refusal that lands while a read is in flight is answered by one more read when that one
  // settles, resolved or failed. The summary read never reports its own refusal
  // (`reportQuota: false`), so the trailing read cannot ask for another one by itself. The org is
  // kept with the request: a refresh asked for by one organization's read never runs against the
  // next organization's.
  let refreshPendingFor: string | null = null;

  const summary = resource<UsageSummaryDto | null, string | undefined>({
    params: () => scope() ?? undefined,
    loader: async ({ params, abortSignal }) => {
      if (refreshPendingFor !== params) {
        refreshPendingFor = null;
      }
      let outcome = await readOnce(data, params, abortSignal);
      while (refreshPendingFor === params) {
        refreshPendingFor = null;
        outcome = await readOnce(data, params, abortSignal);
      }
      if ('error' in outcome) {
        throw outcome.error;
      }
      return outcome.value;
    },
  });

  function load(): void {
    start();
  }

  function reload(): void {
    if (!started()) {
      start();
      return;
    }
    if (summary.isLoading()) {
      refreshPendingFor = orgId();
      return;
    }
    summary.reload();
  }

  return { summary, load, reload };
}
