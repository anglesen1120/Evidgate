// Spectral custom function (no imports — spectral loads functions in isolation).
// 00 §9.2: every 4xx/5xx (and default) response must use the
// components/schemas/ErrorEnvelope schema — either by $ref (directly or via a
// components/responses hop) or by an inline schema carrying the complete
// envelope fields (code, message, request_id, retryable, details).
function localRef(doc, ref) {
  if (typeof ref !== 'string' || ref.indexOf('#/') !== 0 || !doc) return undefined;
  var parts = ref.slice(2).split('/');
  var cur = doc;
  for (var i = 0; i < parts.length; i++) {
    if (cur == null) return undefined;
    cur = cur[parts[i]];
  }
  return cur;
}

var ENVELOPE_FIELDS = ['code', 'message', 'request_id', 'retryable', 'details'];

function schemaIsEnvelope(doc, schema, depth) {
  if (!schema || typeof schema !== 'object' || depth > 8) return false;
  if (schema.$ref) {
    var ref = schema.$ref;
    if (ref === '#/components/schemas/ErrorEnvelope') return true;
    var target = localRef(doc, ref);
    if (target) return schemaIsEnvelope(doc, target, depth + 1);
    // external file ref: the event-envelope schema is a request body, never an error
    return false;
  }
  var errorProps =
    schema.properties && schema.properties.error && schema.properties.error.properties;
  if (errorProps) {
    var all = true;
    for (var i = 0; i < ENVELOPE_FIELDS.length; i++) {
      if (!errorProps[ENVELOPE_FIELDS[i]]) all = false;
    }
    if (all) return true;
  }
  var combos = ['allOf', 'oneOf', 'anyOf'];
  for (var k = 0; k < combos.length; k++) {
    var list = schema[combos[k]];
    if (Array.isArray(list)) {
      for (var j = 0; j < list.length; j++) {
        if (schemaIsEnvelope(doc, list[j], depth + 1)) return true;
      }
    }
  }
  return false;
}

module.exports = function evgErrorEnvelope(targetVal, opts, ctx) {
  var results = [];
  if (!targetVal) return results;
  var doc = ctx && ctx.documentInventory ? ctx.documentInventory.unresolved : undefined;
  var responses = targetVal.responses || {};
  var codes = Object.keys(responses);
  for (var i = 0; i < codes.length; i++) {
    var code = codes[i];
    if (!/^(default|[45]\d\d)$/.test(code)) continue;
    var resp = responses[code];
    if (!resp) continue;
    var resolved = resp.$ref ? localRef(doc, resp.$ref) : resp;
    if (!resolved) {
      results.push({ message: 'response ' + code + ': unresolvable $ref ' + resp.$ref });
      continue;
    }
    var content = resolved.content || {};
    var json = content['application/json'];
    if (!json || !json.schema) {
      results.push({
        message: 'response ' + code + ' must return application/json ErrorEnvelope (00 §9.2)',
      });
      continue;
    }
    if (!schemaIsEnvelope(doc, json.schema, 0)) {
      results.push({
        message:
          'response ' +
          code +
          ' schema must $ref components/schemas/ErrorEnvelope or inline the complete envelope (code, message, request_id, retryable, details) (00 §9.2)',
      });
    }
  }
  return results;
};
