// The catalog row contracts: what one `fixtures/web-api/<name>/input.json` document holds and
// what a catalog row adds to it. Kept apart from the rows so both halves and the lookup barrel
// can name the types without importing the whole 139-document list.

/**
 * One fixture document, exactly as `fixtures/web-api/<name>/input.json` stores it.
 *
 * The `evidgate/no-hand-written-wire-dto` exemption below is intentional and narrow: the other
 * declarations of `apps/web` may not mirror an API payload because `contracts/openapi.yaml` and
 * `@evidgate/sdk` own the wire format, but this type describes a *fixture corpus file* whose
 * snake_case member names are the 01.6.1-T3 file format shared with the .NET loader — not a
 * response body the app reads. Coercing these names to camelCase would make the catalog and the
 * corpus disagree, and there is no SDK type to import instead: a fixture stands in for a response
 * of any operation, so it must stay operation-agnostic (`body` is `unknown`, and the contract
 * check of 09.1.1-T12.2 validates it against the operation's own schema).
 */
/* eslint-disable evidgate/no-hand-written-wire-dto -- the 01.6.1-T3 fixture file format (see above). */
export interface FixtureDocument {
  /** The `operationId` of `contracts/openapi.yaml` this document answers. */
  operation_id: string;
  /** `ok` for the default answer; any other name is reachable only through a toggle. */
  variant: string;
  /** The HTTP status the dev backend answers with; `>= 400` carries a 00 §9.2 envelope body. */
  status: number;
  /** Response headers the fixture pins, on top of the backend's `Content-Type: application/json`. */
  headers: Record<string, string>;
  /** Delay in milliseconds the backend applies when no latency toggle overrides it. */
  latency_ms: number;
  /** The response body; absent for a bodyless status (204/304). */
  body?: unknown;
}
/* eslint-enable evidgate/no-hand-written-wire-dto */

/**
 * One catalog row: the corpus directory name, the document it holds, and — for a route that
 * declares a `cursorQueryParameter` (`fixture-routes.ts`, 09.7.1-F15 note 4) — the request cursor
 * that document answers.
 *
 * `cursor` is a catalog fact, not a corpus field: `input.json` is the 01.6.1-T3 file format shared
 * with the .NET loader and holds a *response*, while which request a page answers is the dev
 * backend's business. A row without one answers a request that carries no cursor, which is why the
 * first page of a paged list needs nothing but its variant.
 */
export interface FixtureCatalogRow {
  readonly name: string;
  readonly document: FixtureDocument;
  /** The `cursor` query value this page answers; absent on the first page and every unpaged row. */
  readonly cursor?: string;
  /**
   * Set when the document is derived in TypeScript from a corpus document instead of imported from
   * `fixtures/web-api/<name>/`. A state the corpus has no file for is then one field away from a
   * contract-validated body rather than a second hand-written copy of a whole response.
   */
  readonly derived?: true;
}
