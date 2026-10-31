// Everything the reporter takes from its options and the environment, resolved once.
// It is resolved in the constructor on purpose: a missing option, an unsafe producer id or
// an unsupported Playwright version must throw before any callback runs, so a misconfigured
// run leaves no half-written spool behind.
import { delimiter } from 'node:path';

import type { CliModule } from './attach.js';
import { DEFAULT_DRAIN_DEADLINE_MS } from './drain.js';
import { validTargetEnv, type IdentityResolver, type WarningPayload } from './identity.js';
import type { EvidgateReporterOptions } from './reporter.js';
import { isMergeReplay, isProducerId } from './shard.js';
import type { SidecarFs } from './sidecar.js';
import type { SpoolWriterOptions } from './spool.js';
import { assertSupportedEngine, readInstalledPlaywrightVersion } from './versionGuard.js';

/**
 * The most warning `run.log` envelopes one run may emit. A hostile suite (thousands of
 * conflicting annotations) must not be able to flood the spool; the overflow is
 * summarised by one `warnings_truncated` envelope at `onEnd`.
 */
export const MAX_WARNING_ENVELOPES = 100;

/** The options and environment values the reporter reads, read exactly once. */
export interface ReporterSettings {
  readonly spoolDir: string;
  readonly runId: string;
  readonly explicitProducerId: string | undefined;
  readonly engineVersion: string;
  readonly writerLock: SpoolWriterOptions['lock'];
  readonly mergeReplay: boolean;
  readonly stepsEnabled: boolean;
  readonly mode: 'full' | 'ingest_only';
  readonly identity: IdentityResolver | undefined;
  readonly sidecarFs: SidecarFs | undefined;
  readonly targetEnv: string | undefined;
  readonly targetEnvWarning: WarningPayload | undefined;
  readonly allowUnmapped: boolean;
  readonly attachmentDirs: readonly string[];
  readonly attachmentDrainDeadlineMs: number;
  readonly importCli: (() => Promise<CliModule>) | undefined;
}

export function resolveReporterSettings(options: EvidgateReporterOptions): ReporterSettings {
  const spoolDir = options.spool_dir ?? process.env.EVIDGATE_SPOOL_DIR;
  const runId = options.run_id ?? process.env.EVIDGATE_RUN_ID;
  const producerId = options.producer_id;
  if (spoolDir === undefined || spoolDir.length === 0) {
    throw new Error(
      '@evidgate/playwright-reporter: the spool_dir option or EVIDGATE_SPOOL_DIR is required',
    );
  }
  if (runId === undefined || runId.length === 0) {
    throw new Error(
      '@evidgate/playwright-reporter: the run_id option or EVIDGATE_RUN_ID is required',
    );
  }
  if (producerId !== undefined && producerId.length > 0 && !isProducerId(producerId)) {
    throw new Error(
      `@evidgate/playwright-reporter: invalid producer_id ${JSON.stringify(producerId)}: ` +
        'must match ^[A-Za-z0-9._-]{1,128}$ (one safe spool path segment)',
    );
  }
  const engineVersion = options.engineVersion ?? readInstalledPlaywrightVersion();
  // The engine guard runs before the spool writer exists, so a rejected version leaves no
  // partial emit behind.
  assertSupportedEngine(engineVersion);
  const targetEnvFields = readTargetEnv();
  return {
    spoolDir,
    runId,
    explicitProducerId: producerId !== undefined && producerId.length > 0 ? producerId : undefined,
    engineVersion,
    writerLock: options.lock,
    // Only the CLI arguments can tell a `merge-reports` replay apart this early.
    mergeReplay: isMergeReplay(options.argv ?? process.argv.slice(2)),
    stepsEnabled: options.steps ?? true,
    mode: options.mode ?? 'full',
    identity: options.identity,
    sidecarFs: options.sidecarFs,
    ...targetEnvFields,
    allowUnmapped: process.env.EVIDGATE_ALLOW_UNMAPPED === '1',
    attachmentDirs: attachmentDirs(options.attachment_dirs ?? []),
    attachmentDrainDeadlineMs: options.attachment_drain_deadline_ms ?? DEFAULT_DRAIN_DEADLINE_MS,
    importCli: options.importCli,
  };
}

/**
 * The target env is read once here. An invalid value falls back to the Playwright project
 * name at the identity layer, so the warning says what the fallback is instead of failing.
 */
function readTargetEnv(): Pick<ReporterSettings, 'targetEnv' | 'targetEnvWarning'> {
  const raw = process.env.EVIDGATE_TARGET_ENV;
  const targetEnv = validTargetEnv(raw?.trim());
  const targetEnvWarning: WarningPayload | undefined =
    raw !== undefined && targetEnv === undefined
      ? {
          code: 'invalid_target_env',
          message:
            'EVIDGATE_TARGET_ENV does not match ^[a-z0-9][a-z0-9_-]{0,63}$; ' +
            'falling back to the Playwright project name',
        }
      : undefined;
  return { targetEnv, targetEnvWarning };
}

/**
 * The option plus `EVIDGATE_ATTACHMENT_DIRS`. The separator is the platform's (`:` on
 * POSIX), so a Windows CI job splits by its own; an empty segment is dropped rather than
 * resolving to the root directory.
 */
function attachmentDirs(configured: readonly string[]): readonly string[] {
  const envDirs = process.env.EVIDGATE_ATTACHMENT_DIRS;
  const fromEnv =
    envDirs === undefined || envDirs.length === 0
      ? []
      : envDirs
          .split(delimiter)
          .map((dir) => dir.trim())
          .filter((dir) => dir.length > 0);
  return [...configured, ...fromEnv];
}
