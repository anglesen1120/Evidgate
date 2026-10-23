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
