// 09.1.1-T12.1 (plan 09.1.1 §2 AC10, §10 T12; 01.6.1-T3 fixture corpus): the fixture documents
// the dev backend can answer with, keyed by the `operationId` of `contracts/openapi.yaml` and a
// `variant`.
//
// The corpus lives at `fixtures/web-api/<name>/` — one `input.json` plus a `meta.json` in the
// 01.6.1-T3 shape (`{"domain":"web-api","kind":"api-response","source":"handcrafted",…}`), no
// `expected.json` because a response is not a golden file. The JSON is imported directly, so the
// bundle carries exactly the corpus and there is no generated copy to drift: adding a state means
// adding a directory and one import here, and `meta.json` is the provenance record the fixture
// loaders (`packages/test-fixtures`) validate.
//
// `input.json` shape (fixed names, snake_case, because it is a file format and not a wire payload):
// `operation_id`, `variant`, `status`, `headers`, `latency_ms` and an optional `body` (omitted for
// a bodyless response). `variant: "ok"` is the answer the backend serves by default; every other
// variant is reached through `FixtureToggles` and exists so a review can show an empty list, a 403
// or a 412 without a server. A row the corpus has no file for is marked `derived`: its document is
// one corpus body with the members that state changes replaced, and the catalog-integrity check
// pins it against that source instead of letting it become a second hand-written response.
//
// Dev-only, like the rest of `core/dev-fixtures`: see the module comment of `fixture-toggles.ts`.

import type { FixtureDocument } from './fixture-document';
import { FIXTURE_CATALOG, type FixtureEntry } from './fixture-catalog-entries';

export { FIXTURE_CATALOG } from './fixture-catalog-entries';
export type { FixtureEntry } from './fixture-catalog-entries';
export type { FixtureCatalogRow, FixtureDocument } from './fixture-document';

/**
 * The entry answering `(operationId, variant)`, or `undefined` when the corpus has none — the dev
 * backend turns that into `501 fixture_not_found` rather than substituting another document, so a
 * stale toggle or a corpus that lost a state is visible in the page, not hidden behind a body that
 * happens to parse.
 */
export function findFixtureEntry(operationId: string, variant: string): FixtureEntry | undefined {
  return FIXTURE_CATALOG.find(
    (entry) => entry.document.operation_id === operationId && entry.document.variant === variant,
  );
}

/**
 * The `id` a document answers for: its own `body.id`, or `undefined` when the body is absent or
 * carries no string `id`. Nothing is inferred — an operation whose body names its resource another
 * way simply never matches by id, and its route must not declare an `idKeyParameter`.
 */
function documentId(document: FixtureDocument): string | undefined {
  const body = document.body;
  if (typeof body !== 'object' || body === null || Array.isArray(body)) {
    return undefined;
  }
  const id = (body as Record<string, unknown>)['id'];
  return typeof id === 'string' ? id : undefined;
}

/**
 * The entry of `operationId` whose own `body.id` equals `id` — the id-keyed selection
 * `fixture-backend.ts` makes for a route that declares an `idKeyParameter`, ahead of the variant
 * (09.7.1-F15 note 3). `undefined` means no document of that operation answers this id, and the
 * caller then falls back to the variant it would have used anyway.
 */
export function findFixtureEntryById(operationId: string, id: string): FixtureEntry | undefined {
  return FIXTURE_CATALOG.find(
    (entry) => entry.document.operation_id === operationId && documentId(entry.document) === id,
  );
}

/**
 * The entry of `operationId` whose catalog row declares `cursor` — the cursor-keyed selection
 * `fixture-backend.ts` makes for a route that declares a `cursorQueryParameter`, ahead of the variant
 * (09.7.1-F15 note 4). `undefined` means no page of that operation answers this cursor — the first
 * request of a list carries none at all — and the caller then falls back to the variant it would
 * have used anyway.
 */
export function findFixtureEntryByCursor(
  operationId: string,
  cursor: string,
): FixtureEntry | undefined {
  return FIXTURE_CATALOG.find(
    (entry) => entry.document.operation_id === operationId && entry.cursor === cursor,
  );
}
