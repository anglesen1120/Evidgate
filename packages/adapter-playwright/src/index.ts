// Package entry. Playwright loads a reporter module and uses its default export as the
// reporter class (node_modules/playwright/lib/runner/index.js requireOrImportDefaultFunction),
// so `EvidgateReporter` is the default. The named exports are what the package's own tests and
// the pipeline stages above import directly.
export {
  EvidgateReporter,
  MAX_WARNING_ENVELOPES,
  type EvidgateReporterOptions,
} from './reporter.js';
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
export {
  createIdentityResolver,
  EVIDGATE_ID_ANNOTATION,
  locationOf,
  PLACEHOLDER_EXECUTION_KEY,
  resolveIdentity,
  TARGET_ENV_PATTERN,
  testKey,
  validTargetEnv,
  type Identity,
  type IdentityResolver,
  type IdentityResolverDeps,
  type VariantOutcome,
  type WarningCode,
  type WarningPayload,
} from './identity.js';
export {
  loadSidecarIndex,
  SIDECAR_FILE_NAME,
  type SidecarFs,
  type SidecarIndex,
  type SidecarLoadOptions,
  type SidecarWarning,
} from './sidecar.js';
export {
  isMergeReplay,
  isProducerId,
  MAX_SHARD_INDEX,
  MERGE_REPORTS_COMMAND,
  PRODUCER_ID_PATTERN,
  resolveProducerId,
  type PlaywrightShard,
  type ProducerShardConfig,
  type ResolvedProducerId,
} from './shard.js';
export { normalizeVariant, type VariantProblem, type VariantResult } from './variant.js';
export {
  attemptOf,
  ERROR_SUMMARY_MAX,
  errorSummary,
  mapStatus,
  STATUS_TABLE,
  type NormalizedStatus,
  type StatusInput,
  type StatusMapping,
} from './status.js';
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
