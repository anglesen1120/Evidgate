// `EVIDGATE_*` configuration resolution. Precedence is `--flag` > env > default; an
// empty env value counts as unset, and no token is accepted from a flag because argv is
// visible in `ps`. Every secret read here is registered for redaction. The spool
// variables are env-only and resolved lazily, so a malformed spool value can never break
// a command that does not touch the spool.
//
// The implementation is split by concern and this module is the surface callers import:
// the shapes (`configTypes`), the env reads (`configEnv`), the spool surface (`configSpool`)
// and the resolver itself (`configResolve`).
// The cap's default is part of this module's surface: `EVIDGATE_SPOOL_MAX_BYTES` is
// resolved here, and the tests document the default without importing the spool module.
export { defaultSpoolMaxBytes } from './spool.js';
export { envNames, registerEnvSecrets, tokenSourceNames } from './configEnv.js';
export {
  defaultSendDeadlineMs,
  maxSendDeadlineMs,
  maxSpoolMaxBytes,
  resolveSendDeadlineMs,
  resolveSpoolDir,
  resolveSpoolMaxBytes,
  resolveSpoolSettings,
  spoolDirMissingError,
} from './configSpool.js';
export {
  formatConfigForVerbose,
  isLoopbackApiUrl,
  resolveConfig,
  resolveTokenSource,
} from './configResolve.js';
export type {
  ConfigFlags,
  ResolveConfigInput,
  ResolvedConfig,
  SpoolSettings,
  TokenSource,
  TokenSourceFields,
} from './configTypes.js';
