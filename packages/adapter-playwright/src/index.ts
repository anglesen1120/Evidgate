// 04.3.1-T1.1: package entry. Playwright loads a reporter module and uses its default
// export as the reporter class (node_modules/playwright/lib/runner/index.js
// requireOrImportDefaultFunction), so `EvidgateReporter` is the default export here.
// The named exports are the pieces the package's own tests and 04.3.2/04.3.3 extend.
export { EvidgateReporter, type EvidgateReporterOptions } from './reporter.js';
export {
  buildCapabilityDocument,
  currentPlatform,
  ENGINE_FAMILY,
  PROTOCOL_MAJOR,
  PROTOCOL_MINOR,
  type CapabilityDocument,
  type CapabilityInput,
  type CapabilitySupports,
} from './capability.js';
export {
  buildEnvelope,
  EVENT_SCHEMA_VERSION,
  EXT_SCHEMA,
  type EnvelopeInput,
  type EventEnvelope,
  type EventType,
  type ExtPayload,
  type UnsignedEnvelope,
} from './envelope.js';
export {
  EXT_ALLOWED_KEYS,
  filterExt,
  SECRET_KEY_PATTERN,
  type ExtAllowedKey,
  type ExtFilterResult,
} from './extFilter.js';
export { PLACEHOLDER_EXECUTION_KEY, type Identity, type IdentityResolver } from './identity.js';
export {
  assertValidSpoolId,
  CHECKPOINT_FILE,
  CHECKPOINT_TMP_FILE,
  CHUNK_FILE_DIGITS,
  DEFAULT_CHUNK_MAX_BYTES,
  DEFAULT_CHUNK_MAX_LINES,
  DEFAULT_FLUSH_THRESHOLD,
  EvidgateInvalidSpoolIdError,
  SPOOL_DIR_MODE,
  SPOOL_FILE_MODE,
  SPOOL_ID_MAX_LENGTH,
  SpoolWriter,
  type SpoolCheckpoint,
  type SpoolWriterOptions,
} from './spool.js';
export { uuidv7 } from './uuidv7.js';
export {
  assertSupportedEngine,
  EvidgateUnsupportedEngineError,
  parseVersion,
  readInstalledPlaywrightVersion,
  satisfiesSupportedEngine,
  SUPPORTED_ENGINE_MAX_EXCLUSIVE,
  SUPPORTED_ENGINE_MIN,
  SUPPORTED_ENGINE_RANGE,
} from './versionGuard.js';

export { EvidgateReporter as default } from './reporter.js';
