// The CLI half of the dual-runtime validators: Ajv 8 against the 2020-12 meta-schema with
// `format` asserted, matching Corvus.Json.Validator's `alwaysAssertFormat` on the server,
// and mapping engine output to the same `details[]` rows the server emits so both suites
// agree on the parity corpus. The registry is the loaded set only, never the file system.
//
// The one Ajv configuration lives in `validateAjv`, the row mapping in `validateErrors` and
// the schema registry in `validateSchemas`; this module is the surface callers import.
export { createContractAjv } from './validateAjv.js';
export {
  bodyTooLargeReason,
  collectValidationErrors,
  invalidJsonReason,
  maxDetailErrors,
  truncatedReason,
  type ContractValidationError,
  type ContractValidationResult,
} from './validateErrors.js';
export {
  canonicalUri,
  compileSchemaDocuments,
  ContractSchemaSet,
  loadContractSchemas,
  type SchemaDocument,
} from './validateSchemas.js';
