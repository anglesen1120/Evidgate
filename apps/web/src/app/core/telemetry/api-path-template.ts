// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; owner brief: one trace id end to end):
// the API path *template* of a request, e.g. `/v1/projects/:id/runs`.
//
// A concrete API URL names a customer's project, run or artifact in its path, so it can never
// leave the app as an attribute. `apiPathTemplate` maps the URL onto the shape of the route
// the server actually served: the static words of the recorded contract are kept, every
// other segment becomes `:id`. `KNOWN_API_SEGMENTS` is that set of static words, taken from
// `core/http/testing/openapi-operations.fixture.json` (recorded from `contracts/openapi.yaml`
// by `apps/web/scripts/record-openapi-fixture.mjs`, paths relative to `/v1`); the spec reads
// that fixture and fails when a new operation introduces a static word this set lacks, so an
// API addition cannot silently turn a whole path into `:id`s or leak a real word.
import { API_BASE_PATH } from '../http/api-origin';

/** The segment every non-static API path segment becomes. */
const ID_SEGMENT = ':id';

/** What an unparsable or non-API URL reports; it never carries any part of the input. */
export const UNPARSED_API_PATH = `${API_BASE_PATH}/unparsed`;

/**
 * Static words of the recorded API surface (fixture paths are relative to `/v1`):
 * `/projects/{p}/runs`, `/projects/{p}/manifests`, `/runs/{r}`, `/runs/{r}/cancel`,
 * `/artifacts/{a}/complete`. Hand-maintained; the drift check in `api-path-template.spec.ts`
 * is what keeps it honest.
 */
export const KNOWN_API_SEGMENTS: ReadonlySet<string> = new Set<string>([
  'artifacts',
  'cancel',
  'complete',
  'manifests',
  'projects',
  'runs',
]);

/**
 * The route template for `url` resolved against `pageHref`, e.g.
 * `/v1/projects/:id/runs`. Query strings and fragments are dropped with the URL parse, empty
 * segments are collapsed, and any input that does not parse or whose path is not `/v1` or
 * under it reports `UNPARSED_API_PATH`.
 */
export function apiPathTemplate(url: string, pageHref: string): string {
  let pathname: string;
  try {
    ({ pathname } = new URL(url, pageHref));
  } catch {
    return UNPARSED_API_PATH;
  }
  if (pathname !== API_BASE_PATH && pathname.startsWith(`${API_BASE_PATH}/`) === false) {
    return UNPARSED_API_PATH;
  }
  const segments = pathname.split('/').filter((segment) => segment !== '');
  // `segments[0]` is the `v1` of the base path itself.
  const rest = segments.slice(1);
  if (rest.length === 0) {
    return API_BASE_PATH;
  }
  const templated = rest.map((segment) => (KNOWN_API_SEGMENTS.has(segment) ? segment : ID_SEGMENT));
  return `${API_BASE_PATH}/${templated.join('/')}`;
}
