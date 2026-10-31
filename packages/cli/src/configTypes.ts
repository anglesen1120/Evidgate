// The shapes of the resolved `EVIDGATE_*` surface. They live apart from the resolution code
// so a caller can name `ResolvedConfig` without importing the resolution (and the module
// graph stays one-way: env reads, spool reads, then the resolver).
/** Flags the parser accepts from argv; token values are deliberately absent. */
export interface ConfigFlags {
  apiUrl?: string;
  project?: string;
  verbose?: boolean;
  json?: boolean;
}

export interface ResolveConfigInput {
  readonly env: Readonly<Record<string, string | undefined>>;
  readonly flags?: ConfigFlags;
  /** When true, a token source must be present or `missing_config` is raised. */
  readonly requireToken?: boolean;
  /** Target platform for the cache-dir default; defaults to `process.platform`. */
  readonly platform?: NodeJS.Platform;
  /** Home directory for the cache-dir default; defaults to `os.homedir()`. */
  readonly homedir?: string;
}

/** Where the bearer token comes from, in §5 precedence order. */
export type TokenSource = 'oidc' | 'actions-id-token' | 'ingest' | 'dev';

/**
 * The spool surface: the root (optional here — only the spool commands decide it is
 * required), the run-directory byte cap and the one send deadline. Resolved together by
 * {@link resolveSpoolSettings}, never by `resolveConfig`, so other commands never pay for it.
 */
export interface SpoolSettings {
  /** Absolute spool root (`EVIDGATE_SPOOL_DIR`), or `undefined` when it is unset. */
  readonly spoolDir: string | undefined;
  /** The run-directory byte cap (`EVIDGATE_SPOOL_MAX_BYTES`). */
  readonly spoolMaxBytes: number;
  /** The one send deadline in milliseconds (`EVIDGATE_SEND_DEADLINE`). */
  readonly sendDeadlineMs: number;
}

export interface ResolvedConfig {
  readonly apiUrl: string;
  readonly project: string;
  /** Absolute token-cache directory; the platform default ends in `evidgate`. */
  readonly cacheDir: string;
  readonly env: string | undefined;
  readonly oidcToken: string | undefined;
  readonly actionsIdTokenRequestUrl: string | undefined;
  readonly actionsIdTokenRequestToken: string | undefined;
  readonly ingestToken: string | undefined;
  readonly devToken: string | undefined;
  readonly bindingChallenge: string | undefined;
  /**
   * The spool surface, resolved on first call and memoised. `events send` and
   * `runs finalize` force it in their `validateConfig` pre-flight — before the
   * `--verbose` dump and before any token work — and nothing else calls it, so a
   * malformed `EVIDGATE_SPOOL_MAX_BYTES` / `EVIDGATE_SEND_DEADLINE` cannot break a
   * command that never touches the spool. Throws the same `invalid_config` errors as a
   * direct {@link resolveSpoolSettings} call; only the spool commands decide that an
   * unset `spoolDir` is `missing_config` ({@link resolveSpoolDir}).
   */
  readonly spoolSettings: () => SpoolSettings;
  readonly verbose: boolean;
  readonly json: boolean;
}

/** The fields `resolveTokenSource` reads; `ResolvedConfig` satisfies it structurally. */
export type TokenSourceFields = Pick<
  ResolvedConfig,
  | 'oidcToken'
  | 'actionsIdTokenRequestUrl'
  | 'actionsIdTokenRequestToken'
  | 'ingestToken'
  | 'devToken'
>;
