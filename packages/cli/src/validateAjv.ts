// The one Ajv configuration every Evidgate schema check shares: the 2020-12 meta-schema
// with `ajv-formats` installed (format assertions on, like the server's
// `alwaysAssertFormat`) and `strict: false`, so OpenAPI 3.1 documents carrying `x-*`
// keywords and JSON-Schema-2020-12-only constructs compile.
import { Ajv2020 } from 'ajv/dist/2020.js';
// ajv-formats ships CJS: the plugin is module.exports (typed `default`). Under
// NodeNext ESM import, the binding is the module namespace whose `default`
// member is the plugin — the call is therefore `ajvFormats.default(ajv)` (the
// shape both Node's interop and TS's CJS modelling agree on).
import ajvFormats from 'ajv-formats';

/**
 * The one Ajv configuration every Evidgate schema check shares: the
 * 2020-12 meta-schema with `ajv-formats` installed (format assertions on, like
 * the server's `alwaysAssertFormat`) and `strict: false`, so OpenAPI 3.1
 * documents carrying `x-*` keywords and JSON-Schema-2020-12-only constructs
 * compile. `loadContractSchemas` and the OpenAPI response validator
 * (`createOpenApiResponseValidator`) both build their engine here — never a
 * second configuration.
 */
export function createContractAjv(): Ajv2020 {
  const ajv = new Ajv2020({ allErrors: true, strict: false });
  ajvFormats.default(ajv);
  return ajv;
}
