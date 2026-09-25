// Spectral custom function (no imports — spectral loads functions in isolation).
// AC3/T51: every operation must declare `x-permission`. The literal value
// `none` is reserved for the two non-permission routes of 00 §9.3
// (POST /v1/ci/token, POST /v1/webhooks/github) and then requires `x-auth`
// naming the credential. See contracts/README.md.
module.exports = function evgPermission(targetVal) {
  const results = [];
  const perm = targetVal && targetVal['x-permission'];
  if (typeof perm !== 'string' || perm.length === 0) {
    results.push({ message: 'operation must declare x-permission (00 §9.3 / T51)' });
    return results;
  }
  if (perm === 'none') {
    const auth = targetVal['x-auth'];
    if (typeof auth !== 'string' || auth.length === 0) {
      results.push({ message: 'x-permission: none requires x-auth naming the credential' });
    }
    return results;
  }
  if (!/^[a-z_]+:[a-z_]+$/.test(perm)) {
    results.push({
      message: `x-permission '${perm}' is not a permission (expected '<area>:<verb>' or 'none')`,
    });
  }
  return results;
};
