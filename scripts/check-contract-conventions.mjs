#!/usr/bin/env node
// 01.3.1 contract conventions gate (AC2-AC4, AC6).
// Invoked by scripts/check-contract-conventions.sh after the spectral stage.
// Prints "E <check>: <detail>" per violation; exits non-zero if any.
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { basename, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parse } from 'yaml';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const errors = [];
const fail = (check, msg) => errors.push(`E ${check}: ${msg}`);

// ---------- load spec ----------
const spec = parse(readFileSync(join(ROOT, 'contracts/openapi.yaml'), 'utf8'));
const METHODS = ['get', 'post', 'put', 'patch', 'delete'];
const MUTATIONS = ['post', 'put', 'patch', 'delete'];

const ops = []; // { path, method, op }
for (const [p, item] of Object.entries(spec.paths ?? {})) {
  for (const m of METHODS) {
    if (item[m]) ops.push({ path: p, method: m, op: item[m] });
  }
}

// ---------- AC3: every 00 §9.3 endpoint row as a stub operation ----------
// Expected table transcribed from plans/00-overview.md §9.3 (paths relative to
// the /v1 server). Update only via a contract change to 00.
const EXPECTED = [
  ['/ci/token', 'post', 'none', 'v1'],
  // The federated-login browser routes are documented by 02 §4.2 and 00 §5.4.8 rather than by
  // the 00 §9.3 core table; they are listed here so the "no undocumented operation" rule below
  // keeps holding, and they are pre-tenant with no permission (02.3.1-T1.1).
  ['/auth/oidc/{provider}/start', 'get', 'none', 'v1'],
  ['/auth/oidc/{provider}/callback', 'get', 'none', 'v1'],
  // 02.3.1-T2's invite staging and explicit second-provider link (02 §4.2, 00 §5.4.8): the same
  // pre-tenant browser surface, listed for the same reason — these carry no catalogue permission a
  // caller could be denied for (the linking routes act only on the caller's own session), which is
  // also why the viewer and ci_producer deny matrices derive them as unpermissioned.
  ['/auth/invite-token', 'post', 'none', 'v1'],
  ['/auth/link/{provider}', 'post', 'none', 'v1'],
  ['/auth/link/pending', 'get', 'none', 'v1'],
  ['/auth/link/pending', 'delete', 'none', 'v1'],
  ['/auth/link/confirm', 'post', 'none', 'v1'],
  // 02.3.2-T2.1's session surface (02 §5, 02.3.2 §5): the same pre-tenant browser form — the caller is the
  // session the cookie proves, resolved inside the definer, so no catalogue permission is carried (the admin
  // path of the revoke route checks `membership:write` inside the definer, never as a route permission).
  ['/auth/sessions', 'get', 'none', 'v1'],
  ['/auth/sessions/{s}', 'delete', 'none', 'v1'],
  ['/auth/sessions/revoke-all', 'post', 'none', 'v1'],
  ['/auth/logout', 'post', 'none', 'v1'],
  ['/projects/{p}/manifests', 'post', 'run:create', 'v1'],
  ['/projects/{p}/runs', 'post', 'run:create', 'v1'],
  ['/runs/{r}', 'get', 'run:read', 'v1'],
  ['/runs/{r}/cancel', 'post', 'run:cancel', 'v1'],
  ['/runs/{r}/events', 'post', 'event:write', 'v1'],
  ['/runs/{r}/finalize', 'post', 'event:write', 'v1'],
  ['/artifacts/upload-url', 'post', 'artifact:write', 'v1'],
  ['/artifacts/{a}/complete', 'post', 'artifact:write', 'v1'],
  ['/runs/{r}/decision', 'get', 'decision:read', 'v1'],
  ['/projects/{p}/runs', 'get', 'run:read', 'v1'],
  ['/runs/{r}/rerun', 'post', 'run:create', 'v1'],
  ['/projects/{p}/environments/{e}/revisions', 'post', 'environment:write', 'v1'],
  ['/environment-revisions/{id}', 'get', 'run:read', 'v1'],
  ['/projects/{p}/policies', 'post', 'policy:write', 'v1'],
  ['/policies/{id}', 'get', 'policy:read', 'v1'],
  ['/policies/{id}/revisions', 'get', 'policy:read', 'v1'],
  ['/projects/{p}/policy-activation', 'post', 'policy:write', 'v1'],
  // 07.1.1 F18 (S116): the two additive reads of the policy surface. 00 §9.3
  // carries them in the policy-read row that follows the activation row.
  ['/projects/{p}/policy-activation', 'get', 'policy:read', 'v1'],
  ['/projects/{p}/policies/head', 'get', 'policy:read', 'v1'],
  ['/runs/{r}/reports/{report_id}/comparison', 'get', 'run:read', 'v1'],
  ['/runs/{r}/reprocess', 'post', 'run:create', 'v1'],
  ['/projects/{p}/releases', 'post', 'run:create', 'v1'],
  ['/releases/{id}', 'get', 'run:read', 'v1'],
  ['/releases/{id}/runs', 'post', 'run:create', 'v1'],
  ['/releases/{id}/decision', 'get', 'decision:read', 'v1'],
  ['/artifacts/{a}/download-url', 'get', 'artifact:read', 'v1'],
  ['/orgs', 'get', 'authenticated', 'v1'],
  ['/orgs', 'post', 'authenticated', 'v1'],
  ['/orgs/{o}', 'get', 'organization:read', 'v1'],
  ['/orgs/{o}', 'patch', 'organization:write', 'v1'],
  ['/orgs/{o}/projects', 'post', 'project:create', 'v1'],
  ['/orgs/{o}/projects', 'get', 'project:read', 'v1'],
  ['/projects/{p}', 'get', 'project:read', 'v1'],
  ['/projects/{p}', 'patch', 'project:write', 'v1'],
  ['/orgs/{o}/memberships', 'post', 'membership:write', 'v1'],
  ['/orgs/{o}/memberships', 'get', 'membership:read', 'v1'],
  ['/orgs/{o}/memberships/{m}', 'get', 'membership:read', 'v1'],
  ['/orgs/{o}/memberships/{m}', 'patch', 'membership:write', 'v1'],
  ['/orgs/{o}/memberships/{m}', 'delete', 'membership:write', 'v1'],
  ['/orgs/{o}/memberships/{m}/roles', 'post', 'membership:write', 'v1'],
  ['/orgs/{o}/memberships/{m}/roles/{r}', 'delete', 'membership:write', 'v1'],
  // 02.1.2-T2's two invite-lifecycle routes; 00 §9.3 documents them in the membership row.
  ['/orgs/{o}/memberships/{m}/resend', 'post', 'membership:write', 'v1'],
  ['/orgs/{o}/memberships/{m}/cancel-invite', 'post', 'membership:write', 'v1'],
  ['/permissions', 'get', 'role:read', 'v1'],
  ['/roles', 'get', 'role:read', 'v1'],
  ['/roles', 'post', 'role:write', 'v1'],
  ['/roles/{r}', 'patch', 'role:write', 'v1'],
  ['/projects/{p}/ci-bindings', 'post', 'ci_binding:write', 'v1'],
  ['/projects/{p}/ci-bindings', 'get', 'project:read', 'v1'],
  ['/ci-bindings/{b}', 'patch', 'ci_binding:write', 'v1'],
  ['/ci-bindings/{b}', 'delete', 'ci_binding:write', 'v1'],
  ['/projects/{p}/ingest-tokens', 'post', 'ingest_token:write', 'v1'],
  ['/projects/{p}/ingest-tokens', 'get', 'ingest_token:read', 'v1'],
  ['/ingest-tokens/{t}/rotate', 'post', 'ingest_token:write', 'v1'],
  ['/ingest-tokens/{t}', 'delete', 'ingest_token:write', 'v1'],
  ['/orgs/{o}/audit', 'get', 'audit:read', 'v1'],
  ['/orgs/{o}/audit/export', 'get', 'audit:read', 'v1'],
  ['/support-grants', 'post', 'support:grant', 'v1'],
  ['/support-grants/{g}/approve', 'post', 'support:approve', 'v1'],
  ['/support-grants/{g}/revoke', 'post', 'support:approve', 'v1'],
  ['/orgs/{o}/security-events', 'get', 'security_event:read', 'v1'],
  ['/projects/{p}/cases', 'get', 'case:read', 'v1'],
  ['/cases/{c}', 'get', 'case:read', 'v1'],
  ['/cases/{c}', 'patch', 'case:write', 'v1'],
  ['/cases/{c}/revisions', 'get', 'case:read', 'v1'],
  ['/projects/{p}/suites', 'post', 'suite:write', 'v1'],
  ['/projects/{p}/suites', 'get', 'suite:read', 'v1'],
  ['/suites/{s}', 'get', 'suite:read', 'v1'],
  ['/suites/{s}/revisions', 'post', 'suite:write', 'v1'],
  ['/suite-revisions/{r}', 'get', 'suite:read', 'v1'],
  ['/suite-revisions/{r}/diff', 'get', 'suite:read', 'v1'],
  ['/webhooks/github', 'post', 'none', 'v1'],
  ['/drafts/{d}/dry-run', 'post', 'draft:dry_run', 'v2'],
  ['/drafts/{d}/pull-request', 'post', 'draft:merge_request', 'v2'],
  ['/projects/{p}/incidents', 'get', 'healing:triage', 'v1'],
  ['/incidents/{i}', 'get', 'healing:triage', 'v1'],
  ['/incidents/{i}/label', 'post', 'healing:triage', 'v1'],
  ['/waivers', 'post', 'policy:write', 'v1'],
  ['/waivers/{w}/approve', 'post', 'policy:write', 'v1'],
  ['/waivers/{w}/revoke', 'post', 'policy:write', 'v1'],
  ['/projects/{p}/waivers', 'get', 'policy:read', 'v1'],
  ['/quarantine', 'post', 'quarantine:write', 'v1'],
  ['/quarantine/{q}', 'delete', 'quarantine:write', 'v1'],
  ['/projects/{p}/quarantine', 'get', 'policy:read', 'v1'],
  ['/orgs/{o}/usage/summary', 'get', 'usage:read', 'v1'],
  ['/orgs/{o}/usage/ledger', 'get', 'usage:read', 'v1'],
  ['/agents/jobs/claim', 'post', 'job:claim', 'v2'],
  ['/jobs/{j}/heartbeat', 'post', 'job:execute', 'v2'],
  ['/healing/candidates/{c}/approve', 'post', 'healing:approve', 'v2'],
  ['/healing/candidates/{c}/revoke', 'post', 'healing:revoke', 'v2'],
  ['/projects/{p}/healing/candidates', 'get', 'healing:triage', 'v1'],
  ['/healing/candidates/{c}', 'get', 'healing:triage', 'v1'],
  ['/healing/candidates/{c}/reject', 'post', 'healing:approve', 'v2'],
  ['/healing/switch', 'get', 'healing:triage', 'v1'],
  ['/healing/switch', 'put', 'healing:configure', 'v1'],
  ['/projects/{p}/authoring/tree', 'get', 'authoring:read', 'v1'],
  ['/projects/{p}/authoring/nodes', 'post', 'authoring:write', 'v1'],
  ['/projects/{p}/authoring/nodes/{n}', 'patch', 'authoring:write', 'v1'],
  ['/projects/{p}/authoring/nodes/{n}/reorder', 'post', 'authoring:write', 'v1'],
  ['/projects/{p}/authoring/nodes/{n}', 'delete', 'authoring:write', 'v1'],
  ['/projects/{p}/authoring/checklist-items', 'get', 'authoring:read', 'v1'],
  ['/projects/{p}/authoring/checklist-items', 'post', 'authoring:write', 'v1'],
  ['/projects/{p}/authoring/checklist-items/{i}', 'patch', 'authoring:write', 'v1'],
  ['/projects/{p}/authoring/checklist-items/{i}', 'delete', 'authoring:write', 'v1'],
  ['/projects/{p}/authoring/manual-cases', 'get', 'authoring:read', 'v1'],
  ['/projects/{p}/authoring/manual-cases', 'post', 'authoring:write', 'v1'],
  ['/projects/{p}/authoring/manual-cases/{m}', 'get', 'authoring:read', 'v1'],
  ['/projects/{p}/authoring/manual-cases/{m}', 'patch', 'authoring:write', 'v1'],
  ['/projects/{p}/authoring/manual-cases/{m}', 'delete', 'authoring:write', 'v1'],
  ['/projects/{p}/authoring/manual-cases/{m}/state', 'post', 'authoring:write', 'v1'],
  ['/projects/{p}/authoring/manual-cases/{m}/item', 'post', 'authoring:write', 'v1'],
  // 13.1.1-T3 (S80): the version timeline (routes 22, 23) and the four restore routes (6, 11, 18, 24).
  // 00 §9.3's authoring row carries them; the two reads are `authoring:read`, the four restores `authoring:write`.
  ['/projects/{p}/authoring/versions', 'get', 'authoring:read', 'v1'],
  ['/projects/{p}/authoring/versions/{v}', 'get', 'authoring:read', 'v1'],
  ['/projects/{p}/authoring/nodes/{n}/restore', 'post', 'authoring:write', 'v1'],
  ['/projects/{p}/authoring/checklist-items/{i}/restore', 'post', 'authoring:write', 'v1'],
  ['/projects/{p}/authoring/manual-cases/{m}/restore', 'post', 'authoring:write', 'v1'],
  ['/projects/{p}/authoring/versions/{v}/restore', 'post', 'authoring:write', 'v1'],
  // 13.1.1-T5 (S84): route 25, the import/export pair. The import is the one authoring write whose body is a file
  // (CSV/XLSX/JSON) and the export is a download, so both carry `x-version: v1` like the rest of 13.1.1 §5.
  ['/projects/{p}/authoring/import', 'post', 'authoring:write', 'v1'],
  ['/projects/{p}/authoring/export', 'get', 'authoring:read', 'v1'],
];

const byKey = new Map(ops.map((o) => [`${o.method} ${o.path}`, o]));
const expectedKeys = new Set();
for (const [p, m, perm, ver] of EXPECTED) {
  const key = `${m} ${p}`;
  expectedKeys.add(key);
  const found = byKey.get(key);
  if (!found) {
    fail('coverage', `missing operation ${m.toUpperCase()} ${p} (00 §9.3)`);
    continue;
  }
  if (found.op['x-permission'] !== perm)
    fail(
      'coverage',
      `${key}: x-permission '${found.op['x-permission']}' != expected '${perm}' (00 §9.3)`,
    );
  if (found.op['x-version'] !== ver)
    fail('coverage', `${key}: x-version '${found.op['x-version']}' != expected '${ver}' (00 §9.3)`);
  if (!found.op.operationId) fail('coverage', `${key}: missing operationId`);
  if (!Array.isArray(found.op.tags) || found.op.tags.length === 0)
    fail('coverage', `${key}: missing tags`);
}
for (const { path, method } of ops) {
  if (!expectedKeys.has(`${method} ${path}`))
    fail('coverage', `undocumented operation ${method.toUpperCase()} ${path} not in 00 §9.3`);
  if (path.split('/').includes('internal'))
    fail('coverage', `path ${path} uses /internal — forbidden by 00 §9.1`);
}

// ---------- AC2: shared components present and used ----------
const compParams = spec.components?.parameters ?? {};
for (const name of ['Cursor', 'Limit', 'IdempotencyKey', 'IfMatch', 'XsrfToken']) {
  if (!compParams[name]) fail('components', `components/parameters/${name} missing (00 §9.1)`);
}
if (compParams.Limit) {
  const s = compParams.Limit.schema ?? {};
  if (s.default !== 50 || s.maximum !== 200)
    fail('components', 'limit parameter must default 50, max 200 (00 §9.1)');
}
if (compParams.IdempotencyKey) {
  const s = compParams.IdempotencyKey.schema ?? {};
  if (s.maxLength !== 128) fail('components', 'Idempotency-Key must be maxLength 128 (00 §7)');
}
const env = spec.components?.schemas?.ErrorEnvelope?.properties?.error?.properties ?? {};
for (const f of ['code', 'message', 'request_id', 'retryable', 'details']) {
  if (!(f in env)) fail('components', `ErrorEnvelope missing error.${f} (00 §9.2)`);
}
const det = env.details?.items?.properties ?? {};
for (const f of ['index', 'path', 'reason']) {
  if (!(f in det)) fail('components', `ErrorEnvelope error.details[] missing ${f} (00 §9.2)`);
}
const cl = spec.components?.schemas?.CursorList?.properties ?? {};
if (!('items' in cl) || !('next_cursor' in cl))
  fail('components', 'CursorList must have items + next_cursor (00 §9.1)');

// shared params must be referenced, not reinvented (provider headers such as
// X-Hub-Signature-256 are allowed inline)
const INLINE_PARAM_ALLOWED = new Set(['X-Hub-Signature-256', 'traceparent']);
// mutations document Idempotency-Key or are exempt — same list as the
// spectral evgIdempotency function (keep both in sync; README lists them too).
// Justifications: ci/token = no side effect beyond ci_token_issue, jti
// single-use (02 §4 endpoint table, FR-02-6); webhooks/github = delivery_id
// dedupe (00 §9.3); events = per-event event_id dedupe (00 §9.4);
// pull-request = idempotent by (branch, commit) (00 §9.3); heartbeat =
// extends only the current epoch (00 §9.3); ci-bindings/ingest-tokens/rotate = the
// response carries a one-time secret, and the replay store persists response bodies,
// so they are never replayed (02.4.1, 02.4.2, 00 §5.7 S3); the auth routes of 02.3.1-T2 =
// the invite staging writes only a short-lived cookie (no resource), and the link surface is
// driven by one staged-link cookie that one confirmation spends, so a replay cannot write twice; the session
// routes of 02.3.2-T2 = revocation is a row state (`revoked_at` set once) and every answer is the same on a
// repeat, so a key could change nothing the second time.
const IDEM_EXEMPT = new Set([
  'post /ci/token',
  'post /projects/{p}/ci-bindings',
  'post /projects/{p}/ingest-tokens',
  'post /ingest-tokens/{t}/rotate',
  'post /webhooks/github',
  'post /runs/{r}/events',
  'post /drafts/{d}/pull-request',
  'post /jobs/{j}/heartbeat',
  'post /projects/{p}/policies',
  'post /auth/invite-token',
  'post /auth/link/{provider}',
  'delete /auth/link/pending',
  'post /auth/link/confirm',
  'delete /auth/sessions/{s}',
  'post /auth/sessions/revoke-all',
  'post /auth/logout',
]);
for (const { path, method, op } of ops) {
  for (const prm of op.parameters ?? []) {
    if (prm.$ref) {
      if (!prm.$ref.startsWith('#/components/parameters/'))
        fail('components', `${method} ${path}: param $ref '${prm.$ref}' is not a shared component`);
    } else if (!INLINE_PARAM_ALLOWED.has(prm.name)) {
      fail(
        'components',
        `${method} ${path}: inline param '${prm.name}' must be a shared component`,
      );
    }
  }
  // every response carries X-Request-Id (00 §9.1) — resolve one local ref hop
  for (const [code, resp] of Object.entries(op.responses ?? {})) {
    const resolved = resp?.$ref?.startsWith('#/components/responses/')
      ? spec.components.responses[resp.$ref.split('/').pop()]
      : resp;
    const hdrs = Object.keys(resolved?.headers ?? {});
    if (!hdrs.some((h) => h.toLowerCase() === 'x-request-id'))
      fail('components', `${method} ${path} response ${code} lacks X-Request-Id (00 §9.1)`);
  }
  if (MUTATIONS.includes(method)) {
    const params = (op.parameters ?? []).concat(spec.paths[path]?.parameters ?? []);
    const hasKey = params.some(
      (p) =>
        p &&
        (p.$ref === '#/components/parameters/IdempotencyKey' ||
          (p.in === 'header' && /^idempotency-key$/i.test(p.name ?? ''))),
    );
    if (!hasKey && !IDEM_EXEMPT.has(`${method} ${path}`))
      fail('idempotency', `${method} ${path} lacks Idempotency-Key and is not exempt (00 §9.1)`);
  }
}

// ---------- AC4: schema validity via ajv ----------
const ajv = new Ajv2020({ strict: false, allErrors: true });
addFormats(ajv);

const SCHEMA_DIRS = ['contracts/schemas', 'contracts/adapter'];
const loadSchema = (dir, f) => JSON.parse(readFileSync(join(ROOT, dir, f), 'utf8'));
const ex = (f) => JSON.parse(readFileSync(join(ROOT, 'contracts/examples', f), 'utf8'));
const compile = (schema, label) => {
  try {
    return ajv.compile(schema);
  } catch (e) {
    fail(label, `schema does not compile: ${e.message}`);
    return null;
  }
};

const evSchema = loadSchema('contracts/schemas', 'event-envelope.v1.json');
const evValidate = compile(evSchema, 'event-envelope');
const validEvent = ex('event.single.valid.json');
if (evValidate && !evValidate(validEvent))
  fail('event-envelope', `00 §9.4 example rejected: ${JSON.stringify(evValidate.errors)}`);
if (evValidate && !evValidate(ex('event-batch.valid.json')))
  fail('event-envelope', `valid batch rejected: ${JSON.stringify(evValidate.errors)}`);
const bigBatch = { events: Array.from({ length: 501 }, () => ({ ...validEvent })) };
if (evValidate && evValidate(bigBatch))
  fail('event-envelope', '501-event batch accepted (cap is 500, 00 §9.3)');
const badEvent = { events: [{ ...validEvent }] };
delete badEvent.events[0].event_id;
if (evValidate && evValidate(badEvent)) fail('event-envelope', 'event without event_id accepted');
const badKey = { events: [{ ...validEvent, execution_key: 'not-hex' }] };
if (evValidate && evValidate(badKey)) fail('event-envelope', 'non-hex execution_key accepted');
const badFinished = { ...validEvent };
delete badFinished.status;
if (evValidate && evValidate(badFinished))
  fail('event-envelope', 'test.finished without status accepted');

const capSchema = loadSchema('contracts/adapter', 'capability.v1.json');
const capValidate = compile(capSchema, 'capability');
const validCap = ex('capability.valid.json');
if (capValidate && !capValidate(validCap))
  fail('capability', `valid document rejected: ${JSON.stringify(capValidate.errors)}`);
const badCap1 = JSON.parse(JSON.stringify(validCap));
delete badCap1.supports.retry;
if (capValidate && capValidate(badCap1))
  fail('capability', 'document missing supports.retry accepted');
const badCap2 = { ...validCap, mode: 'bogus' };
if (capValidate && capValidate(badCap2)) fail('capability', 'mode=bogus accepted');
const badCap3 = { ...validCap, platforms: [] };
if (capValidate && capValidate(badCap3)) fail('capability', 'empty platforms accepted');

// ---------- AC6: schema dialect + naming ----------
const SNAKE = /^[a-z][a-z0-9_]*$/;
const UPPER_SNAKE = /^[A-Z][A-Z0-9_]*$/;
const LOWER_ENUM = /^[a-z][a-z0-9_.]*$/;

function walkSchema(node, file, trail) {
  if (!node || typeof node !== 'object') return;
  if (node.properties && typeof node.properties === 'object') {
    for (const name of Object.keys(node.properties)) {
      if (!SNAKE.test(name)) fail('naming', `${file}: property '${name}' is not snake_case`);
      walkSchema(node.properties[name], file, [...trail, name]);
    }
  }
  if (Array.isArray(node.enum)) {
    const prop = trail[trail.length - 1];
    for (const v of node.enum) {
      if (typeof v !== 'string') continue;
      if (prop === 'decision') {
        if (!UPPER_SNAKE.test(v))
          fail('naming', `${file}: decision enum '${v}' must be UPPER_CASE (00 §7)`);
      } else if (!LOWER_ENUM.test(v)) {
        fail('naming', `${file}: enum '${v}' (${prop ?? 'anonymous'}) must be lower_snake (00 §7)`);
      }
    }
  }
  for (const k of ['items', 'additionalProperties', 'if', 'then']) {
    if (node[k] && typeof node[k] === 'object') walkSchema(node[k], file, trail);
  }
  for (const k of ['$defs', 'definitions']) {
    if (node[k] && typeof node[k] === 'object')
      for (const sub of Object.values(node[k])) walkSchema(sub, file, trail);
  }
  for (const k of ['allOf', 'anyOf', 'oneOf']) {
    if (Array.isArray(node[k])) for (const sub of node[k]) walkSchema(sub, file, trail);
  }
}

// 04.7.1-T1.4 / 05.1.1-T1.1: every schema file is discovered the way both
// loaders discover it — recursively under contracts/schemas, contracts/adapter
// (so adapter/operations/*.v1.json is covered) and contracts/manifest — and
// its key is the file name without `.json`.
const SCHEMA_ROOTS = ['contracts/schemas', 'contracts/adapter', 'contracts/manifest'];

function* schemaFiles(relativeDirectory) {
  const entries = readdirSync(join(ROOT, relativeDirectory), { withFileTypes: true }).sort(
    (a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0),
  );
  for (const entry of entries) {
    const relative = `${relativeDirectory}/${entry.name}`;
    if (entry.isDirectory()) yield* schemaFiles(relative);
    else if (entry.name.endsWith('.json')) yield relative;
  }
}

const refs = []; // { file, ref }
function collectRefs(node, file) {
  if (!node || typeof node !== 'object') return;
  if (typeof node.$ref === 'string') refs.push({ file, ref: node.$ref });
  for (const value of Object.values(node)) {
    if (Array.isArray(value)) for (const item of value) collectRefs(item, file);
    else if (value && typeof value === 'object') collectRefs(value, file);
  }
}

const schemaKeys = new Set();
for (const root of SCHEMA_ROOTS) {
  if (!existsSync(join(ROOT, root))) continue;
  for (const relative of schemaFiles(root)) {
    const file = basename(relative);
    if (!/\.v\d+\.json$/.test(file))
      fail('dialect', `${relative}: schema filename must carry a .vN version suffix`);
    const schema = JSON.parse(readFileSync(join(ROOT, relative), 'utf8'));
    if (schema.$schema !== 'https://json-schema.org/draft/2020-12/schema')
      fail('dialect', `${relative}: $schema '${schema.$schema}' is not JSON Schema 2020-12`);
    if (typeof schema.$id !== 'string' || !schema.$id.includes(file))
      fail('dialect', `${relative}: $id must reference its .vN filename (got '${schema.$id}')`);
    schemaKeys.add(basename(file, '.json'));
    walkSchema(schema, relative, []);
    collectRefs(schema, relative);
  }
}

// A schema may address a sibling only by its canonical URI
// (urn:evidgate:contract:<key>, the same address the loaders register in their
// in-memory registry); anything else would need file-system or network
// resolution, which both runtimes turn off.
for (const { file, ref } of refs) {
  if (ref.startsWith('#')) continue;
  const target = /^urn:evidgate:contract:([^#]+)(#.*)?$/.exec(ref);
  if (!target) {
    fail(
      'ref',
      `${file}: '$ref: ${ref}' is neither a local pointer nor a urn:evidgate:contract:<key> address`,
    );
  } else if (!schemaKeys.has(target[1])) {
    fail('ref', `${file}: $ref '${ref}' names no loaded contract schema key ('${target[1]}')`);
  }
}

// AC6 also applies to every schema inside openapi.yaml: component schemas and
// inline request/response/parameter schemas (header *names* are exempt — the
// walk only inspects property names and enum values inside schema subtrees).
const specSchemas = [];
for (const [name, s] of Object.entries(spec.components?.schemas ?? {}))
  specSchemas.push([`components.schemas.${name}`, s]);
for (const { path, method, op } of ops) {
  for (const prm of op.parameters ?? []) {
    const resolved = prm.$ref?.startsWith('#/components/parameters/')
      ? spec.components.parameters[prm.$ref.split('/').pop()]
      : prm;
    if (resolved?.schema)
      specSchemas.push([`${method} ${path} param ${resolved.name}`, resolved.schema]);
  }
  const rb = op.requestBody?.content?.['application/json']?.schema;
  if (rb) specSchemas.push([`${method} ${path} requestBody`, rb]);
  for (const [code, resp] of Object.entries(op.responses ?? {})) {
    const resolved = resp?.$ref?.startsWith('#/components/responses/')
      ? spec.components.responses[resp.$ref.split('/').pop()]
      : resp;
    const s = resolved?.content?.['application/json']?.schema;
    if (s) specSchemas.push([`${method} ${path} response ${code}`, s]);
  }
}
for (const [label, schema] of specSchemas) walkSchema(schema, `openapi.yaml ${label}`, []);

// ---------- report ----------
if (errors.length) {
  for (const e of errors) console.error(e);
  process.exit(1);
}
console.log(
  `check-contract-conventions: ${ops.length} operations, ${EXPECTED.length} §9.3 rows, conventions OK`,
);
