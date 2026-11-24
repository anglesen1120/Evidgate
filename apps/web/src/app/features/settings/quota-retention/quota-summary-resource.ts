import { computed, linkedSignal, resource, type Signal, type WritableSignal } from '@angular/core';
import type { UsageSummaryDto } from '@evidgate/sdk';

import { QuotaDataService } from '../../../core/data/quota-data.service';

type ReadOutcome = { readonly value: UsageSummaryDto | null } | { readonly error: unknown };
interface ProjectRequest {
  readonly orgId: string;
  readonly limit: number;
  readonly cursors: readonly (string | undefined)[];
  readonly navigation: boolean;
}
interface ServedPage {
  readonly request: ProjectRequest;
  readonly value: UsageSummaryDto | null;
  readonly failed: boolean;
}
interface ReadResult {
  readonly request: ProjectRequest;
  readonly outcome: ReadOutcome;
}
interface PageSource {
  readonly scope: string | null;
  readonly request: ProjectRequest | undefined;
  readonly result: ReadResult | undefined;
}

async function readOnce(
  data: QuotaDataService,
  params: ProjectRequest,
  abortSignal: AbortSignal,
): Promise<ReadOutcome> {
  try {
    const value = await data.summary(params.orgId, abortSignal, params.limit, params.cursors.at(-1));
    if (value?.next_cursor != null && params.cursors.includes(value.next_cursor)) {
      throw new Error('The usage summary repeated a forward cursor.');
    }
    return { value };
  } catch (error) {
    return { error };
  }
}

function retainPage(source: PageSource, old: ServedPage | null | undefined): ServedPage | null {
  const current = source.request;
  if (current === undefined || source.scope !== current.orgId) return null;
  const retained =
    old?.request.orgId === current.orgId && old.request.limit === current.limit ? old : null;
  if (source.result?.request !== current) return current.navigation ? retained : null;
  const outcome = source.result.outcome;
  if ('error' in outcome) {
    return current.navigation && retained
      ? { ...retained, failed: true }
      : { request: current, value: null, failed: true };
  }
  return { request: current, value: outcome.value, failed: false };
}

function pageActions(
  request: WritableSignal<ProjectRequest | undefined>,
  served: Signal<ServedPage | null>,
  paging: Signal<boolean>,
  scope: () => string | null,
) {
  function navigate(forward: boolean): void {
    const current = served();
    if (paging() || current?.value == null || scope() !== current.request.orgId) return;
    const cursors = current.request.cursors;
    if (forward) {
      const cursor = current.value.next_cursor;
      if (cursor === null) return;
      request.set({ ...current.request, cursors: [...cursors, cursor], navigation: true });
    } else if (cursors.length > 1) {
      request.set({ ...current.request, cursors: cursors.slice(0, -1), navigation: true });
    }
  }
  return {
    next: () => navigate(true),
    previous: () => navigate(false),
    setPageSize: (size: number): void => {
      const current = request();
      if (current === undefined || ![20, 50, 100].includes(size) || size === current.limit) return;
      request.set({ ...current, limit: size, cursors: [undefined], navigation: false });
    },
  };
}

export interface QuotaSummaryReadOptions {
  readonly scope: () => string | null;
  readonly orgId: () => string | null;
  readonly started: () => boolean;
  readonly start: () => void;
  readonly data: QuotaDataService;
}

/** A page replacement retains no project-row history, only opaque request cursors. */
export function quotaSummaryRead(options: QuotaSummaryReadOptions) {
  const { scope, orgId, started, start, data } = options;
  const request = linkedSignal((): ProjectRequest | undefined => {
    const active = scope();
    return active === null
      ? undefined
      : { orgId: active, limit: 20, cursors: [undefined], navigation: false };
  });
  let refreshPendingFor: string | null = null;

  const response = resource({
    params: request,
    loader: async ({ params, abortSignal }) => {
      if (refreshPendingFor !== params.orgId) refreshPendingFor = null;
      let outcome = await readOnce(data, params, abortSignal);
      while (!abortSignal.aborted && refreshPendingFor === params.orgId) {
        refreshPendingFor = null;
        outcome = await readOnce(data, params, abortSignal);
      }
      return { request: params, outcome };
    },
  });
  const served = linkedSignal<PageSource, ServedPage | null>({
    source: () => ({
      scope: scope(),
      request: request(),
      result: response.hasValue() ? response.value() : undefined,
    }),
    computation: (source, previous) => retainPage(source, previous?.value),
  });
  const summary = computed(() => served()?.value ?? null);
  const paging = computed(() => response.isLoading());
  const page = computed(() => served()?.request.cursors.length ?? 1);
  const pageSize = computed(() => request()?.limit ?? 20);
  const pagingError = computed(() => served()?.failed === true && summary() !== null);
  const hasNext = computed(() => summary()?.next_cursor != null);
  function reload(): void {
    if (!started()) {
      start();
    } else if (response.isLoading()) {
      refreshPendingFor = orgId();
    } else {
      response.reload();
    }
  }

  return {
    summary,
    load: start,
    reload,
    paging,
    page,
    pageSize,
    pagingError,
    hasNext,
    ...pageActions(request, served, paging, scope),
    retry: () => {
      if (pagingError()) response.reload();
    },
  };
}
