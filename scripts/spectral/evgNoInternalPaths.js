// Spectral custom function (no imports — spectral loads functions in isolation).
// 00 §9.1: operational (/internal/health/*, /metrics) and operator
// (/internal/operator/*) routes are never part of contracts/openapi.yaml.
module.exports = function evgNoInternalPaths(targetVal) {
  var results = [];
  if (!targetVal || typeof targetVal !== 'object') return results;
  var keys = Object.keys(targetVal);
  for (var i = 0; i < keys.length; i++) {
    var p = keys[i];
    var segs = p.split('/').filter(function (s) {
      return s.length > 0;
    });
    if (segs.indexOf('internal') !== -1) {
      results.push({
        message:
          "path '" +
          p +
          "' contains an /internal segment — /internal/* routes are never in the public spec (00 §9.1)",
        path: [p],
      });
    }
  }
  return results;
};
