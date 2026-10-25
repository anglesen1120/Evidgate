// Spectral custom function (no imports — spectral loads functions in isolation).
// AC3/T51: every operation must declare `x-permission`. The literal value
// `none` is reserved for the non-permission routes of 00 §9.3 — the two
// credential-authenticated routes (POST /v1/ci/token, POST /v1/webhooks/github)
// and the two pre-tenant federated-login routes of 02.3.1
// (GET /v1/auth/oidc/{provider}/start, GET /v1/auth/oidc/{provider}/callback),
// which run before any session or tenant exists — and then requires `x-auth`
// naming the credential. The literal `authenticated` is reserved for
// POST /v1/orgs (02.1.1 §5): any signed-in user, no tenant permission yet (the
// tenant is being created); it too requires `x-auth`. See contracts/README.md.
module.exports = function evgPermission(targetVal) {
  const results = [];
  const perm = targetVal && targetVal['x-permission'];
  if (typeof perm !== 'string' || perm.length === 0) {
    results.push({ message: 'operation must declare x-permission (00 §9.3 / T51)' });
    return results;
  }
  if (perm === 'none' || perm === 'authenticated') {
    const auth = targetVal['x-auth'];
    if (typeof auth !== 'string' || auth.length === 0) {
      results.push({ message: `x-permission: ${perm} requires x-auth naming the credential` });
    }
    return results;
  }
  if (!/^[a-z_]+:[a-z_]+$/.test(perm)) {
    results.push({
      message: `x-permission '${perm}' is not a permission (expected '<area>:<verb>', 'none' or 'authenticated')`,
    });
  }
  return results;
};
