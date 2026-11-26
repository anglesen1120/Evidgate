// Spectral custom function (no imports — spectral loads functions in isolation).
// `none` and `authenticated` name no catalogue permission, so both must identify their credential.
// The latter covers signed-in members listing or creating organizations and reading their own
// grants within one organization. See contracts/README.md.
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
