// Spectral custom function (no imports — spectral loads functions in isolation).
// T38 / 00 §9.1: every mutation reachable via the `sessionCookie` scheme must
// document `X-XSRF-TOKEN`; a mutation whose security does not include
// sessionCookie (bearer-only, webhook-signature or anonymous routes) must NOT
// document it.
module.exports = function evgXsrf(targetVal, opts, ctx) {
  var results = [];
  if (!targetVal || !ctx || !ctx.path) return results;
  var method = String(ctx.path[ctx.path.length - 1]).toLowerCase();
  if (!/^(post|put|patch|delete)$/.test(method)) return results;
  var params = targetVal.parameters || [];
  var hasXsrf = params.some(function (p) {
    if (!p) return false;
    if (p.$ref === '#/components/parameters/XsrfToken') return true;
    return p.in === 'header' && /^x-xsrf-token$/i.test(p.name || '');
  });
  // Security semantics (OAS): no `security` key on the operation inherits the
  // document-level security (sessionCookie || bearerToken) → cookie-reachable.
  // `security: []` is anonymous → NOT cookie-reachable. A non-empty list is
  // cookie-reachable iff any requirement contains sessionCookie.
  var opSec = targetVal.security;
  var cookieReachable;
  if (opSec === undefined) {
    cookieReachable = true;
  } else if (Array.isArray(opSec)) {
    cookieReachable = opSec.some(function (req) {
      return req && Object.keys(req).indexOf('sessionCookie') !== -1;
    });
  } else {
    cookieReachable = false;
  }
  if (cookieReachable && !hasXsrf) {
    results.push({
      message:
        'cookie-authenticated mutation must document X-XSRF-TOKEN (00 §9.1, T38); ' +
        'make the operation bearer-only via `security:` if it is never cookie-reachable',
    });
  }
  if (!cookieReachable && hasXsrf) {
    results.push({
      message: 'bearer-only mutation must not document X-XSRF-TOKEN (T38)',
    });
  }
  return results;
};
