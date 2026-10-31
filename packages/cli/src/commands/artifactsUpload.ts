// 04.2.4-T1.4: `evidgate artifacts upload` — ships one file as evidence (04.2.4 §5,
// contracts/cli.md §12). It is the CLI surface over `uploadFile()`: open the file with the
// allowed-directory guard, hash it, mint an `upload-url`, PUT the bytes to the presigned
// URL, and call `complete`.
//
// Order of operations matters and is observable:
// 1. every flag is validated, and the kind/media-type pair resolved, BEFORE any token work;
// 2. the file is opened (allow-list, regular file, cap) and hashed BEFORE `getToken()`, so a
//    disallowed or oversize file makes no request at all — not even the token exchange;
// 3. the token, the journal (when `EVIDGATE_SPOOL_DIR` is set) and the upload.
//
// The producer id is the manifest slot id of T54 (`shard-<index>`, `shard-1` unsharded); a
// sharded job passes its own `--producer shard-<n>`, because the CLI cannot know the shard
// index. `--execution-key` is required: it is the planned execution's sha256, which only the
// manifest knows. A 401 purges the cached CI token (contracts/cli.md §7) and says so;
// a 403/409/413/422 never retries and prints the server's `error.code` + `request_id`.
import { setTimeout as delay } from 'node:timers/promises';

import { getToken } from '../auth/getToken.js';
import { deleteCachedToken } from '../auth/tokenCache.js';
import { CliError, escapeControlCharacters } from '../errors.js';
import { ExitCode } from '../exitCodes.js';
import { ProducerSpool } from '../spool.js';
import { UploadFailedError, hashUploadFile, uploadFile, type UploadDeps } from '../upload.js';
import { journalFor, type UploadJournal, type UploadJournalLockOptions } from '../uploadJournal.js';
import {
  artifactKinds,
  closeUploadFile,
  mediaTypeFor,
  openUploadFile,
  pathRefused,
  type ArtifactKind,
} from '../uploadPaths.js';
import { resolveUploadDeadlineMs } from '../uploadRetry.js';

import type { CommandDefinition, CommandContext, ParsedValues } from './index.js';

/**
 * The usage-error hint `main.ts` attaches to its parse-level usage errors (`helpHint`
 * there, module-private). Repeated here so the command-level flag usage errors carry the
 * same pointer (contracts/cli.md §5 `usage` row); keep the two literals in sync.
 */
const helpHint = "run 'evidgate --help' for usage";
/** openapi `RunId`: path/body `run_id`, `format: uuid`. */
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
/** openapi `createArtifactUploadUrl.producer_id`: the same shape every producer command uses. */
const producerPattern = /^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$/;
/** openapi `createArtifactUploadUrl.execution_key`: 64 lowercase hex (00 §7). */
const executionKeyPattern = /^[0-9a-f]{64}$/;
/** `attempt_no`: an integer ≥ 1, no sign and no leading zero. */
const attemptPattern = /^[1-9][0-9]{0,8}$/;
/** openapi `createArtifactUploadUrl.media_type`: 1..255 code points, no Cc/Cf character. */
const mediaTypePattern = /^[^\p{Cc}\p{Cf}]{1,255}$/u;
/** [DECISION] T54: the slot id of an unsharded run; a sharded job passes `--producer`. */
export const defaultProducerId = 'shard-1';

/** The seams `main` forwards; the upload adds its own clock/jitter/sleep defaults. */
interface ArtifactsUploadSeams {
  readonly fetch?: typeof fetch;
  readonly sleep?: (ms: number) => Promise<void>;
  readonly now?: () => number;
  readonly random?: () => number;
}

export const artifactsUploadCommand: CommandDefinition = {
  name: 'artifacts upload',
  summary: 'upload one artifact file to a run',
  requireToken: true,
  // Every value option is `multiple: true`, so a repetition is observable and refused
  // instead of silently taking the last value; `--dir` is the one option that is meant to
  // repeat (the allowed-directory allow-list grows with it).
  options: {
    run: { type: 'string', multiple: true },
    file: { type: 'string', multiple: true },
    kind: { type: 'string', multiple: true },
    producer: { type: 'string', multiple: true },
    'execution-key': { type: 'string', multiple: true },
    attempt: { type: 'string', multiple: true },
    'media-type': { type: 'string', multiple: true },
    dir: { type: 'string', multiple: true },
  },
  run: (context) => runArtifactsUpload(context),
};

async function runArtifactsUpload(context: CommandContext): Promise<ExitCode> {
  const { config, io, values, env, deps } = context;
  // `ExchangeDeps` already satisfies the seam set: every extra seam is optional, so `main`'s
  // `deps` is forwarded as-is and the upload's own defaults fill `now`/`random`.
  const seams: ArtifactsUploadSeams = deps;

  // 1. Flags and configuration first: nothing below can reach the network otherwise.
  const runId = requiredFlag(values, 'run');
  if (!uuidPattern.test(runId)) {
    throw usageError('--run must be a UUID');
  }
  const file = requiredFlag(values, 'file');
  const kind = readKind(requiredFlag(values, 'kind'));
  const executionKey = requiredFlag(values, 'execution-key');
  if (!executionKeyPattern.test(executionKey)) {
    throw usageError('--execution-key must be 64 lowercase hex characters (the planned execution)');
  }
  const producerId = flagValue(values, 'producer') ?? defaultProducerId;
  if (!producerPattern.test(producerId)) {
    throw usageError('--producer must be 1..128 of [A-Za-z0-9_.-], starting alphanumeric');
  }
  const attemptNo = readAttempt(flagValue(values, 'attempt'));
  const mediaType = readMediaType(flagValue(values, 'media-type'), kind, file);
  const dirs = dirValues(values);
  // A malformed deadline refuses before the token exchange (`invalid_config`, §3).
  const deadlineMs = resolveUploadDeadlineMs(env);
  const uploadSeams = uploadDeps(seams);

  // 2. The file guard and the hash pass: a refused file makes no request at all. The
  // descriptor stays open until the last PUT has finished — it pins the inode the guard
  // accepted, so the PUT pass can prove the path still resolves to those same bytes.
  const opened = await openUploadFile({ file, kind, dirs, cwd: process.cwd() });
  try {
    const sha256 = await hashUploadFile(opened);

    try {
      // 3. Token, journal, upload.
      const { token } = await getToken(config, deps);
      const journal = await openJournal(config, runId, producerId, {
        deadlineAt: uploadSeams.now() + deadlineMs,
        now: uploadSeams.now,
        sleep: uploadSeams.sleep,
      });
      const result = await uploadFile({
        config,
        token,
        runId,
        producerId,
        executionKey,
        attemptNo,
        kind,
        mediaType,
        file: opened,
        sha256,
        journal,
        deadlineMs,
        deps: uploadSeams,
      });

      const name = escapeControlCharacters(opened.path.split(/[\\/]/).pop() ?? opened.path);
      if (config.json) {
        io.stdout(
          `${JSON.stringify({
            file: name,
            artifact_id: result.artifactId,
            state: result.state,
            bytes: result.bytes,
            scan_state: result.scanState,
          })}\n`,
        );
      } else {
        io.stdout(
          `artifact_id=${result.artifactId} state=${result.state} bytes=${String(result.bytes)} file=${name}\n`,
        );
      }
      return ExitCode.Ok;
    } catch (error) {
      if (error instanceof UploadFailedError && error.status === 401) {
        // Best effort: the server's error is the one that must surface (04.2.1 §6).
        await deleteCachedToken(config).catch(() => undefined);
      }
      throw error;
    }
  } finally {
    await closeUploadFile(opened);
  }
}

/** Builds the transport/seam object; every seam has a production default. */
function uploadDeps(seams: ArtifactsUploadSeams): UploadDeps {
  return {
    fetch: seams.fetch ?? fetch,
    sleep: seams.sleep ?? ((ms) => delay(ms)),
    now: seams.now ?? Date.now,
    random: seams.random ?? Math.random,
  };
}

/**
 * The resume store. `EVIDGATE_SPOOL_DIR` is not required by this command: without it the
 * upload works exactly the same but has no state to resume from after a crash (stated in
 * contracts/cli.md §12). With it, the producer directory is opened/created like the spool
 * commands do, and the journal writes under its `spool.lock` — only around its own writes,
 * so a long PUT never blocks `events send`. A busy lock is waited for with bounded backoff
 * (bounded by the upload deadline) instead of failing the upload at once.
 */
async function openJournal(
  config: CommandContext['config'],
  runId: string,
  producerId: string,
  lock: UploadJournalLockOptions,
): Promise<UploadJournal | undefined> {
  const settings = config.spoolSettings();
  const spoolDir = settings.spoolDir;
  if (spoolDir === undefined) {
    return undefined;
  }
  const spool = await ProducerSpool.open({
    spoolDir,
    runId,
    producerId,
    maxBytes: settings.spoolMaxBytes,
    createIfMissing: true,
  });
  return journalFor({
    spoolDir,
    producerDirectory: spool.directory,
    withLock: (fn) => spool.withLock(fn),
    lock,
  });
}

/** The kind allow-list (T20, openapi enum); anything else is a usage error. */
function readKind(raw: string): ArtifactKind {
  if (!(artifactKinds as readonly string[]).includes(raw)) {
    throw usageError(`--kind must be one of ${artifactKinds.join(', ')}`);
  }
  return raw as ArtifactKind;
}

/** `attempt_no`: openapi integer ≥ 1, default 1 (the first attempt of an execution). */
function readAttempt(raw: string | undefined): number {
  if (raw === undefined) {
    return 1;
  }
  if (!attemptPattern.test(raw)) {
    throw usageError('--attempt must be an integer of at least 1');
  }
  return Number(raw);
}

/**
 * `--media-type` wins; otherwise the kind's static default decides from the file extension
 * (`artifact-kind-defaults.v1.json`). A kind with no static default, or an extension no
 * default of that kind names, has no knowable media type here — the server's per-project
 * policy is the only authority — so the operator must pass `--media-type` instead of the CLI
 * guessing a type the policy would reject.
 */
function readMediaType(raw: string | undefined, kind: ArtifactKind, file: string): string {
  if (raw !== undefined) {
    if (!mediaTypePattern.test(raw)) {
      throw usageError('--media-type must be 1..255 characters with no control character');
    }
    return raw;
  }
  const derived = mediaTypeFor(kind, file);
  if (derived === undefined) {
    throw usageError(
      `--media-type is required for --kind ${kind} and this file: no static media type is known`,
    );
  }
  return derived;
}

/** Every `--dir` value, in order; an empty or control-bearing value is a usage error. */
function dirValues(values: ParsedValues): string[] {
  const raw = values.dir;
  if (raw === undefined) {
    return [];
  }
  const list = Array.isArray(raw) ? raw : [raw];
  const dirs: string[] = [];
  for (const entry of list) {
    if (typeof entry !== 'string' || entry.length === 0) {
      throw usageError('--dir must be a non-empty directory');
    }
    if (/[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]/u.test(entry)) {
      throw pathRefused('--dir contains a control character');
    }
    dirs.push(entry);
  }
  return dirs;
}

function usageError(message: string): CliError {
  return new CliError(message, { code: 'usage', hint: helpHint });
}

/**
 * The single value of one option, or `undefined` when it was absent. Every value option is
 * `multiple: true`, so a repetition arrives as an array; two or more entries (or a
 * non-string entry) are a `usage` error that names the flag and never its value.
 */
function flagValue(values: ParsedValues, flag: string): string | undefined {
  const value = values[flag];
  if (value === undefined) {
    return undefined;
  }
  if (typeof value === 'string') {
    return value;
  }
  if (Array.isArray(value) && value.length === 1 && typeof value[0] === 'string') {
    return value[0];
  }
  if (Array.isArray(value) && value.length > 1) {
    throw usageError(`--${flag} may be given only once`);
  }
  throw usageError(`--${flag} requires a value`);
}

/** A required string option; absent is a `usage` error, a repetition is refused. */
function requiredFlag(values: ParsedValues, flag: string): string {
  const value = flagValue(values, flag);
  if (value === undefined) {
    throw usageError(`missing required flag --${flag}`);
  }
  return value;
}
