// 09.1.1-T3.2 (00 §9.1; ADR-0008 rule 2): `Idempotency-Key` is generated once per user
// intent by the caller (see `intent.ts`). This interceptor only fills a missing key on
// untyped mutations of the same-origin `/v1` API, and never for GET/HEAD/OPTIONS.
import { DOCUMENT } from '@angular/common';
import type { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';

import { isApiRequestUrl } from './api-origin';
import { newUuid } from './random-id';

const MUTATION_METHODS: Readonly<Record<string, true>> = {
  POST: true,
  PUT: true,
  PATCH: true,
  DELETE: true,
};

export const idempotencyInterceptor: HttpInterceptorFn = (request, next) => {
  const pageHref = inject(DOCUMENT).location.href;
  if (!isApiRequestUrl(request.url, pageHref)) {
    return next(request);
  }
  if (MUTATION_METHODS[request.method.toUpperCase()] !== true) {
    return next(request);
  }
  if (request.headers.has('Idempotency-Key')) {
    return next(request);
  }
  // A fresh key per request, and only for a mutation no caller intent covered; `newUuid()`
  // keeps working where `crypto.randomUUID` is absent (non-secure context).
  return next(request.clone({ headers: request.headers.set('Idempotency-Key', newUuid()) }));
};
