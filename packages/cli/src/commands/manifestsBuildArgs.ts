// The `manifests build` flag set after validation: target environments in flag order, the
// optional environment revision in canonical lowercase, and the resolved shard count. Every
// check here runs before `getToken()`, so a bad value makes no request; messages name the
// flag shape and never echo the value.
import { CliError } from '../errors.js';
import { defaultTargetEnv } from '../manifestEmit.js';
import { parseShardCount, type ShardCount } from '../manifestShards.js';

import { flagValue, uuidPattern } from './eventsSendArgs.js';

import type { ParsedValues } from './index.js';

/** `manifest.v1` target_env: 1–64 characters of lowercase letters, digits, `_` and `-`. */
const targetEnvPattern = /^[a-z0-9][a-z0-9_-]{0,63}$/;
/** `manifest.v1` caps `matrix.target_envs` at 32 entries. */
const maxTargetEnvs = 32;

export interface BuildFlags {
  readonly targetEnvs: readonly string[];
  readonly environmentRevisionId?: string;
  readonly shards: ShardCount;
}

/** Reads and validates the three optional flags; repetition of a single-value flag is refused. */
export function readBuildFlags(values: ParsedValues): BuildFlags {
  const environmentRevisionId = readEnvironmentRevisionId(values);
  return {
    targetEnvs: readTargetEnvs(values),
    shards: readShardCount(values),
    ...(environmentRevisionId === undefined ? {} : { environmentRevisionId }),
  };
}

/** `--target-env` is meant to repeat, so every entry is kept in flag order. */
function readTargetEnvs(values: ParsedValues): string[] {
  const raw = values['target-env'];
  if (raw === undefined) {
    return [defaultTargetEnv];
  }
  const entries = Array.isArray(raw) ? raw : [raw];
  const envs: string[] = [];
  const seen = new Set<string>();
  for (const entry of entries) {
    if (typeof entry !== 'string' || !targetEnvPattern.test(entry)) {
      throw new CliError('a --target-env value is not a valid target environment name', {
        code: 'manifest_target_env_invalid',
        hint: 'use 1 to 64 characters: lowercase letters, digits, "_" or "-", starting with a letter or digit',
      });
    }
    if (seen.has(entry)) {
      throw new CliError('the same --target-env was given more than once', {
        code: 'manifest_target_env_duplicate',
        hint: 'give each target environment once',
      });
    }
    seen.add(entry);
    envs.push(entry);
  }
  if (envs.length > maxTargetEnvs) {
    throw new CliError(`more than ${maxTargetEnvs} --target-env values were given`, {
      code: 'manifest_target_env_invalid',
      hint: `a manifest carries at most ${maxTargetEnvs} target environments`,
    });
  }
  return envs;
}

/** The optional environment revision, normalised the way the route names a revision. */
function readEnvironmentRevisionId(values: ParsedValues): string | undefined {
  const raw = flagValue(values, 'environment-revision-id');
  if (raw === undefined) {
    return undefined;
  }
  if (!uuidPattern.test(raw)) {
    throw new CliError('--environment-revision-id must be a UUID', {
      code: 'manifest_environment_revision_invalid',
    });
  }
  return raw.toLowerCase();
}

/** The shard count; absence means one shard over every execution. */
function readShardCount(values: ParsedValues): ShardCount {
  const raw = flagValue(values, 'shards');
  return raw === undefined ? 1 : parseShardCount(raw);
}
