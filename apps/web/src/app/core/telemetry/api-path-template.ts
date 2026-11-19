// 09.1.1-T9.1 (plans/features/09-web-ui §10 T9.1; owner brief: one trace id end to end):
// the API path *template* of a request, e.g. `/v1/projects/:id/runs`.
//
// A concrete API URL names a customer's project, run or artifact in its path, so it can never
// leave the app as an attribute. `apiPathTemplate` maps the URL onto the shape of the route
// the server actually served: the static words of the contract are kept, every other segment
// becomes `:id`. `KNOWN_API_SEGMENTS` is that set of static words; the spec reads
// `contracts/openapi.yaml` and fails when a path introduces a static word this set lacks, or the
// set keeps a word the contract no longer has, so an API addition cannot silently turn a whole
// path into `:id`s or leak a real word.
import { API_BASE_PATH } from '../http/api-origin';

/** The segment every non-static API path segment becomes. */
const ID_SEGMENT = ':id';

/** What an unparsable or non-API URL reports; it never carries any part of the input. */
export const UNPARSED_API_PATH = `${API_BASE_PATH}/unparsed`;

/**
 * Static words of the whole API surface in `contracts/openapi.yaml` (paths relative to `/v1`):
 * `/orgs/{o}/projects`, `/projects/{p}/runs`, `/runs/{r}/cancel`, `/permissions`, … The shell
 * calls the identity routes (`orgs`, `permissions`, `projects`) as well as the run routes, so the
 * set is the contract's, not only the six operations the web fixture records. Hand-maintained;
 * the drift check in `api-path-template.spec.ts` compares it with the contract in both directions.
 */
export const KNOWN_API_SEGMENTS: ReadonlySet<string> = new Set<string>([
  'agents',
  'ai-settings',
  'api-tokens',
  'approve',
  'artifact-kind-policy',
  'artifacts',
  'attempts',
  'audit',
  'auth',
  'authoring',
  'callback',
  'cancel',
  'cancel-invite',
  'candidates',
  'cases',
  'changes',
  'changes:apply',
  'checklist-items',
  'ci',
  'ci-bindings',
  'claim',
  'comparison',
  'complete',
  'confirm',
  'decision',
  'dev',
  'diff',
  'discard',
  'download-url',
  'drafts',
  'dry-run',
  'environment-revisions',
  'environments',
  'events',
  'evidence-revisions',
  'execution-plan',
  'export',
  'finalize',
  'fixtures',
  'git-repo-bindings',
  'github',
  'head',
  'healing',
  'heartbeat',
  'import',
  'incidents',
  'ingest-tokens',
  'installations',
  'invite-token',
  'item',
  'jobs',
  'label',
  'ledger',
  'link',
  'links',
  'logout',
  'manifests',
  'me',
  'manual-cases',
  'memberships',
  'nodes',
  'oidc',
  'orgs',
  'pending',
  'permissions',
  'policies',
  'policy-activation',
  'projects',
  'pull-request',
  'quarantine',
  'reject',
  'releases',
  'reorder',
  'reports',
  'reprocess',
  'rerun',
  'resend',
  'restore',
  'retention',
  'revisions',
  'revoke',
  'revoke-all',
  'roles',
  'rotate',
  'runs',
  'security-events',
  'session',
  'sessions',
  'start',
  'state',
  'suite-revisions',
  'suites',
  'summary',
  'support-grants',
  'switch',
  'templates',
  'token',
  'tree',
  'upload-url',
  'usage',
  'users',
  'versions',
  'waivers',
  'webhooks',
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
