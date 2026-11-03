// Spectral custom function (no imports — spectral loads functions in isolation).
// 00 §9.1: "Mutations that create resources or side effects require
// Idempotency-Key". The exemption list below is grounded in 00 (and 02 §4 for
// ci/token) and mirrored in contracts/README.md — extend it only with a
// justification from the plan contracts.
var IDEMPOTENCY_EXEMPT = {
  'post /ci/token':
    'Idempotency-Key not required — no side effect beyond ci_token_issue; jti single-use rejects replays (02 §4 endpoint table, FR-02-6/02.4.1)',
  'post /projects/{p}/ci-bindings':
    'the response carries a one-time challenge and is never replayed, so no key can be honoured (00 §5.7 S3, 02.4.1)',
  'post /projects/{p}/ingest-tokens':
    'the response carries a one-time secret and is never replayed, so no key can be honoured (00 §5.7 S3, 02.4.2)',
  'post /ingest-tokens/{t}/rotate':
    'the response carries a one-time secret and is never replayed, so no key can be honoured (00 §5.7 S3, 02.4.2)',
  'post /webhooks/github': 'replay-safe by provider delivery_id dedupe (00 §9.3)',
  'post /runs/{r}/events':
    'per-event event_id dedupe is the arbiter; a batch-level key cannot cover per-event acks (00 §9.4)',
  'post /drafts/{d}/pull-request': 'idempotent by (branch, commit) per 00 §9.3',
  'post /jobs/{j}/heartbeat':
    'naturally idempotent — the server extends only the current epoch (00 §9.3)',
  'post /projects/{p}/policies':
    'the RFC 8785 content hash is the idempotency key: an identical body returns the stored revision (07.1.1 §5)',
  'put /projects/{p}/retention':
    'naturally idempotent: the same body upserts the same two windows on (org_id, project_id), so a replay creates no second row and cannot change the answer (06.4.1 §5)',
  'post /auth/invite-token':
    'no resource is created: the route validates the presented token and stages it in a short-lived __Host- cookie, so a repeat writes the same cookie (02.3.1-T2 Q1)',
  'post /auth/link/{provider}':
    'no resource is created: the answer is a state cookie and the provider authorization URL, and a repeat replaces the browser own pending state (02.3.1-T2 Q4)',
  'delete /auth/link/pending':
    'DELETE is idempotent by contract: the answer is 204 whether or not a staged link existed (02.3.1-T2 Q4)',
  'post /auth/link/confirm':
    'one staged-link cookie buys one confirmation and is deleted on every terminal outcome, so a replay cannot write a second identity; the store converges on already_linked (02.3.1-T2 Q4, AC6)',
  'delete /auth/sessions/{s}':
    'revocation is a row state: the first call sets revoked_at and every repeat answers the same 404 not_found on a dead row, so no key could change the answer (02.3.2-T2.1 §5)',
  'post /auth/sessions/revoke-all':
    'naturally idempotent: revoking an already revoked row changes nothing and the answer is the same 204 (02.3.2-T2.1 §5)',
  'post /auth/logout':
    'naturally idempotent: the session row is revoked once and replaying the old cookie is 401 session_revoked, so a key could not change the outcome (02.3.2-T2.1 §5, AC8)',
  'post /dev/session':
    'no resource is created: the route sets the session cookies of one fixed development user, so a repeat only replaces the caller own session (Development-only route)',
};

module.exports = function evgIdempotency(targetVal, opts, ctx) {
  var results = [];
  if (!targetVal || !ctx || !ctx.path) return results;
  var method = String(ctx.path[ctx.path.length - 1]).toLowerCase();
  if (!/^(post|put|patch|delete)$/.test(method)) return results;
  var path = String(ctx.path[ctx.path.length - 2]);
  var key = method + ' ' + path;
  var params = (targetVal.parameters || []).concat(
    ctx.documentInventory && ctx.documentInventory.unresolved
      ? ((ctx.documentInventory.unresolved.paths || {})[path] || {}).parameters || []
      : [],
  );
  var hasKey = params.some(function (p) {
    if (!p) return false;
    if (p.$ref === '#/components/parameters/IdempotencyKey') return true;
    return p.in === 'header' && /^idempotency-key$/i.test(p.name || '');
  });
  if (!hasKey && !IDEMPOTENCY_EXEMPT[key]) {
    results.push({
      message:
        'mutation requires Idempotency-Key (00 §9.1) or a documented exemption; ' +
        'exemptions: ' +
        Object.keys(IDEMPOTENCY_EXEMPT).join(', '),
    });
  }
  return results;
};
