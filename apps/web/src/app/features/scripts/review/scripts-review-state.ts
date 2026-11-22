import { computed, inject, type Signal } from '@angular/core';
import { Router, UrlTree, type ParamMap } from '@angular/router';
import type { HlmIcon } from '@evidgate/ui/icon';
import type { InlineFeedbackTone } from '../../../shared/ui/states/inline-feedback.component';

export interface ReviewNoticeViewModel {
  readonly title: string;
  readonly description: string;
  readonly replace: boolean;
  readonly tone: InlineFeedbackTone;
}
export interface ReviewMetricViewModel {
  readonly title: string;
  readonly value: string;
  readonly detail: string;
  readonly icon: HlmIcon['name'] extends Signal<infer Name> ? Name : never;
}
export function isNoticeKey<T extends string>(
  key: string,
  notices: Readonly<Record<T, ReviewNoticeViewModel>>,
): key is T {
  return Object.hasOwn(notices, key);
}

export function matchesReviewRecord(
  params: ParamMap | undefined,
  allowed: Readonly<Record<string, readonly string[]>>,
): boolean {
  if (!params) return true;
  return Object.entries(allowed).every(([key, choices]) => {
    const values = params.getAll(key);
    return values.length === 0 || (values.length === 1 && choices.includes(values[0] ?? ''));
  });
}

export function injectReviewReturnUrl(): Signal<string> {
  const router = inject(Router);
  return computed(() => {
    const current = router.lastSuccessfulNavigation()?.finalUrl ?? router.parseUrl(router.url);
    const query = { ...current.queryParams };
    delete query['state'];
    return router.serializeUrl(new UrlTree(current.root, query, current.fragment));
  });
}
