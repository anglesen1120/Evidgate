// 04.2.3-T2.1: `evidgate runs finalize` — declares one producer slot done at
// `POST /v1/runs/{r}/finalize` (openapi `finalizeRun`, 00 §9.3, 05.3.2-T1) and prints the
// 202 reconcile-accepted state (04.2.3 §2 AC6, §5, §8; contracts/cli.md §11).
//
// The two values the server arbitrates are read from the CLI's own ack cursor, never from
// flags (D7, 2026-10-23): `last_sequence` is `cursor.json`'s `last_acked_sequence` and
// `count` its `acked_count`. A spool that still holds unacked events is refused with
// `unsent_events` (exit 3) naming the run and the unsent range, so a finalize can never
// declare a sequence the server has not acknowledged. A cursor whose `acked_count`
// exceeds `last_acked_sequence` is `spool_corrupt` (exit 3) rather than a declaration
// nobody could have produced (S54 SC9). A missing producer directory is `spool_missing`
// (exit 3) — finalize never creates a spool.
//
// Every flag is validated BEFORE `getToken()`, so a bad flag makes no request at all and
// not even the token exchange (the `runs create` rule, 04.2.2 §8/§9). Each option is
// single-use: every option is `multiple: true`, so a repetition is refused as `usage`
// instead of silently taking the last value.
//
// The slot is read and the spool purged under the producer's exclusive `spool.lock`
// (D2/`spoolLock.ts`), the same lock `events send` holds, so two CLI processes can never
// interleave an ack, a finalize or a purge. The purge runs only after a 202: a failed
// finalize keeps every byte, and a failed purge after a 202 is a stderr warning, not an
// error — the run IS finalized at that point.
//
// A 403 `producer_slot_taken`, a 422 `unknown_producer`, a 422 `manifest_invalid` or a 409
// is never retried: the command makes exactly one request and exits 3 with the server's
// `error.code` and `request_id` verbatim (T9). The hint says the command is idempotent, so
// re-running it after fixing the cause is safe. A 401 purges the cached CI token
// (04.2.1-T1 hand-over, contracts/cli.md §7); a 403 or 422 keeps it.
import { createHash } from 'node:crypto';

import { finalizeRun } from '@evidgate/sdk';

import { getToken } from '../auth/getToken.js';
import { deleteCachedToken } from '../auth/tokenCache.js';
import { createApiClient } from '../client.js';
import { resolveSpoolDir, spoolDirMissingError } from '../config.js';
import { CliError } from '../errors.js';
import { ExitCode } from '../exitCodes.js';
import { serverErrorFromResponse, transportError } from '../serverError.js';
import { ProducerSpool, SpoolCorruptError, spoolCursorFileName } from '../spool.js';

import type { CommandDefinition, ParsedValues } from './index.js';

const requestTimeoutMs = 30_000;
/**
 * The usage-error hint `main.ts` attaches to its parse-level usage errors (`helpHint`
 * there, module-private). Repeated here so the command-level flag usage errors carry the
 * same pointer (contracts/cli.md §5 `usage` row); keep the two literals in sync.
 */
const helpHint = "run 'evidgate --help' for usage";
/** openapi `RunId`: path parameter `r`, `format: uuid`. */
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
/**
 * openapi `finalizeRun` `producer_id`: `^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$`. The same
 * shape is a safe spool path segment (`spool.ts` `assertValidSpoolId` is broader).
 */
const producerPattern = /^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$/;
/** openapi `finalizeRun` `shard`: an integer 1..256 (the 1-based manifest shard index). */
const minShard = 1;
const maxShard = 256;
/** Decimal digits with no leading zero and no sign; the range check follows. */
const shardDigitsPattern = /^[1-9][0-9]{0,2}$/;
/** openapi `finalizeRun` `manifest_hash`: `sha256:` + 64 lowercase hex (00 §7, RFC 8785). */
const manifestHashPattern = /^sha256:[0-9a-f]{64}$/;
/**
 * The derived-key namespace. Versioned and part of the pre-image, so a later change to
 * the derivation cannot silently reuse a stored key.
 */
const finalizeKeyVersion = 'evidgate.finalize-key.v1';
/** The `state` a 202 means: the slot is closed and reconciliation runs asynchronously. */
const reconcileAcceptedState = 'reconcile_accepted';

/** Inputs of the derived key: exactly the values a `runs finalize` request is built from. */
export interface FinalizeIdempotencyKeyInput {
  /** The `--run` value. */
  readonly runId: string;
  /** The `--producer` value. */
  readonly producerId: string;
  /** The `--shard` value, already parsed to an integer. */
  readonly shard: number;
  /** `cursor.json`'s `last_acked_sequence`. */
  readonly lastSequence: number;
  /** `cursor.json`'s `acked_count`. */
  readonly count: number;
  /** The `--manifest-hash` value. */
  readonly manifestHash: string;
}

/**
 * The derived `Idempotency-Key`: the lowercase hex SHA-256 of the UTF-8 JSON array
 * `['evidgate.finalize-key.v1', run_id, producer_id, shard, last_sequence, count,
 * manifest_hash]`.
 *
 * A JSON array is unambiguous (no separator can collide with a value) and the field order
 * is fixed, so two invocations with the same inputs — in this process or after a restart —
 * derive the same key, and a body that differs in any of these values never reuses it.
 * Every value the command sends feeds the key. The result is 64 hex characters, inside the
 * 128-char route limit (contracts/cli.md §11). The key carries no secret and is never
 * printed.
 */
export function deriveFinalizeIdempotencyKey(input: FinalizeIdempotencyKeyInput): string {
  const preImage = JSON.stringify([
    finalizeKeyVersion,
    input.runId,
    input.producerId,
    input.shard,
    input.lastSequence,
    input.count,
    input.manifestHash,
  ]);
  return createHash('sha256').update(preImage, 'utf8').digest('hex');
}

export const runsFinalizeCommand: CommandDefinition = {
  name: 'runs finalize',
  summary: 'finalize a producer slot once its events are acked',
  requireToken: true,
  // The spool root is required by the spool commands (D5). Refusing it here — before the
  // `--verbose` config dump and before any token work — is the `quickstart` project-guard
  // pattern (contracts/cli.md §1/§10). Forcing the lazy `spoolSettings()` (CR8) is what
  // makes a malformed `EVIDGATE_SPOOL_MAX_BYTES` this command's `invalid_config` and never
  // `runs create`'s or `auth login`'s.
  validateConfig: (config) => {
    if (config.spoolSettings().spoolDir === undefined) {
      throw spoolDirMissingError();
    }
  },
  options: {
    // `multiple: true` is what makes a repetition observable: with the default `false`
    // `parseArgs` keeps the last value and reports nothing, so `--producer a --producer b`
    // would quietly send `b` (contracts/cli.md §11).
    run: { type: 'string', multiple: true },
    producer: { type: 'string', multiple: true },
    shard: { type: 'string', multiple: true },
    'manifest-hash': { type: 'string', multiple: true },
    'keep-spool': { type: 'boolean', multiple: true },
  },
  run: async ({ config, io, values, deps, env }) => {
    // Flags first, token second: an invalid flag must not reach the network, and a
    // repeated flag (an array of two or more entries from `parseArgs`) is refused rather
    // than silently taking the last value.
    const runId = requiredFlag(values, 'run');
    if (!uuidPattern.test(runId)) {
      throw usageError('--run must be a UUID');
    }
    const producerId = requiredFlag(values, 'producer');
    if (!producerPattern.test(producerId)) {
      throw usageError('--producer must be 1..128 of [A-Za-z0-9_.-], starting alphanumeric');
    }
    const shard = parseShard(requiredFlag(values, 'shard'));
    const manifestHash = requiredFlag(values, 'manifest-hash');
    if (!manifestHashPattern.test(manifestHash)) {
      throw usageError('--manifest-hash must be sha256:<64 lowercase hex>');
    }
    const keepSpool = flagSwitch(values, 'keep-spool');

    // Configuration before the token too: a missing spool directory is `missing_config`
    // (refused already in `validateConfig`) and costs no token exchange. The settings are
    // memoised by that pre-flight (CR8), and `resolveSpoolDir` makes a relative value
    // absolute, so the spool path never depends on a later `chdir` (D5) — the fallback
    // only narrows the type here.
    const settings = config.spoolSettings();
    const spoolDir = settings.spoolDir ?? resolveSpoolDir(env);
    const spool = await ProducerSpool.open({
      spoolDir,
      runId,
      producerId,
      // The run-directory cap is resolved by `validateConfig`; finalize never appends,
      // but `ProducerSpool` validates the value it is opened with (D4).
      maxBytes: settings.spoolMaxBytes,
    });

    return spool.withLock(async () => {
      const cursor = await spool.cursor();
      const unsent = await spool.unsentRange();
      if (unsent !== null) {
        throw new CliError(
          `refusing to finalize run ${runId} producer ${producerId}: ` +
            `unsent spooled events ${unsent.first}..${unsent.last}`,
          {
            code: 'unsent_events',
            hint: `run 'evidgate events send --run ${runId}' first, then finalize`,
          },
        );
      }
      const lastSequence = cursor.lastAckedSequence;
      const count = cursor.ackedCount;
      // SC9: `cursor.json` is the only source of both declared values, and a count larger
      // than the sequence it claims to cover cannot be a real ack prefix — it is
      // corruption, not a value worth sending. `cursor()` already checked the shape and
      // `last_acked_sequence === next_unacked_sequence - 1`; this is the remaining
      // cross-field invariant (0 <= count <= last_sequence).
      if (count > lastSequence) {
        throw new SpoolCorruptError({
          chunk: spoolCursorFileName,
          lineNumber: 1,
          reason: `acked_count (${count}) must not exceed last_acked_sequence (${lastSequence})`,
        });
      }
      const idempotencyKey = deriveFinalizeIdempotencyKey({
        runId,
        producerId,
        shard,
        lastSequence,
        count,
        manifestHash,
      });

      const { token } = await getToken(config, deps);
      const client = createApiClient(config, token, deps.fetch ?? fetch);
      const result = await finalizeRun({
        client,
        path: { r: runId },
        headers: { 'Idempotency-Key': idempotencyKey },
        body: {
          producer_id: producerId,
          shard,
          last_sequence: lastSequence,
          count,
          manifest_hash: manifestHash,
        },
        signal: AbortSignal.timeout(requestTimeoutMs),
      });
      const response = result.response;
      if (response === undefined) {
        // A network error, a timeout or a refused redirect: the SDK returns no response.
        throw transportError(result.error);
      }
      if (!response.ok) {
        if (response.status === 401) {
          // Best-effort: the server's error is the one that must surface (04.2.1 §6).
          await deleteCachedToken(config).catch(() => undefined);
        }
        // A 403 `producer_slot_taken`, a 422 `unknown_producer`/`manifest_invalid` and a
        // 409 therefore reach stderr as `evidgate: error <code>` with the server's
        // `request_id`, and exit 3. Exactly one request is made: nothing here retries,
        // and the hint says a re-run is safe because the server arbitrates on the slot's
        // closed marker, never on the key.
        throw finalizeError(result.error, response);
      }
      if (response.status !== 202) {
        // Only a 202 means "accepted, reconcile asynchronous"; anything else 2xx is a
        // contract violation and must not be reported as a finalize.
        throw new CliError('the run finalize response was not valid', {
          code: 'request_failed',
        });
      }

      if (config.json) {
        io.stdout(
          `${JSON.stringify({
            run_id: runId,
            producer_id: producerId,
            shard,
            last_sequence: lastSequence,
            count,
            manifest_hash: manifestHash,
            state: reconcileAcceptedState,
          })}\n`,
        );
      } else {
        io.stdout(
          `evidgate: finalized run=${runId} producer=${producerId} shard=${shard} ` +
            `last_sequence=${lastSequence} count=${count}; ` +
            'reconcile accepted (asynchronous)\n',
        );
      }

      if (!keepSpool) {
        try {
          await spool.purge();
        } catch (error) {
          // The run IS finalized; a leftover spool is only local disk. Never fail the
          // command (exit 0) and never echo a path or the raw error text.
          io.stderr(
            `evidgate: warning: the run was finalized but its spool could not be purged ` +
              `(errno ${errnoName(error)}); delete the producer directory manually\n`,
          );
        }
      }
      return ExitCode.Ok;
    });
  },
};

function usageError(message: string): CliError {
  return new CliError(message, { code: 'usage', hint: helpHint });
}

/**
 * Maps a non-2xx finalize reply through §5 and adds the one hint the command owns: the
 * server's slot close makes a repeat a no-op 202, so re-running after fixing the cause is
 * safe. The server code, `request_id` and `detail:` lines are carried over untouched
 * (T9) — this only re-wraps so the hint is not lost (`ServerErrorFallback` has no hint).
 */
function finalizeError(error: unknown, response: Response): CliError {
  const mapped = serverErrorFromResponse(error, response, {
    code: 'request_failed',
    message: `the run finalize failed (HTTP ${response.status})`,
  });
  return new CliError(mapped.message, {
    code: mapped.code,
    hint: 'finalize is idempotent: fix the cause and re-run the same command',
    ...(mapped.requestId === undefined ? {} : { requestId: mapped.requestId }),
    ...(mapped.details === undefined ? {} : { details: mapped.details }),
  });
}

/**
 * The single value of one option, or `undefined` when it was absent. Every option is
 * declared `multiple: true`, so `parseArgs` reports a repetition as an array instead of
 * silently keeping the last value; two or more entries (or a non-string entry) are a
 * `usage` error that names the flag and never its value.
 */
function flagValue(values: ParsedValues, flag: string): string | undefined {
  const value = values[flag];
  if (value === undefined) {
    return undefined;
  }
  if (typeof value === 'string') {
    return value;
  }
  if (!Array.isArray(value)) {
    throw usageError(`--${flag} must be a single value`);
  }
  if (value.length !== 1) {
    throw usageError(`--${flag} may be given only once`);
  }
  const [only] = value;
  if (typeof only !== 'string') {
    throw usageError(`--${flag} must be a single value`);
  }
  return only;
}

/** A required string option; absent is a `usage` error, a repetition is refused. */
function requiredFlag(values: ParsedValues, flag: string): string {
  const value = flagValue(values, flag);
  if (value === undefined) {
    throw usageError(`missing required flag --${flag}`);
  }
  if (value.length === 0) {
    throw usageError(`--${flag} must be a single, non-empty value`);
  }
  return value;
}

/**
 * A boolean switch: `undefined` is false, `true` is true, and a repetition is refused —
 * `--keep-spool --keep-spool` must not silently read as one `true`.
 */
function flagSwitch(values: ParsedValues, flag: string): boolean {
  const value = values[flag];
  if (value === undefined) {
    return false;
  }
  if (typeof value === 'boolean') {
    return value;
  }
  if (!Array.isArray(value)) {
    throw usageError(`--${flag} must be a flag without a value`);
  }
  if (value.length !== 1) {
    throw usageError(`--${flag} may be given only once`);
  }
  const [only] = value;
  if (typeof only !== 'boolean') {
    throw usageError(`--${flag} must be a flag without a value`);
  }
  return only;
}

/**
 * `--shard` is the slot's own 1-based manifest index: decimal digits with no sign and no
 * leading zero, then the openapi range 1..256. `Number` of the digits is exact here
 * because the pattern caps the value at three digits.
 */
function parseShard(raw: string): number {
  if (!shardDigitsPattern.test(raw)) {
    throw usageError(`--shard must be an integer from ${minShard} to ${maxShard}`);
  }
  const shard = Number(raw);
  if (shard < minShard || shard > maxShard) {
    throw usageError(`--shard must be an integer from ${minShard} to ${maxShard}`);
  }
  return shard;
}

/** The errno code of a filesystem failure, or `unknown` — never the message or a path. */
function errnoName(error: unknown): string {
  const code = (error as NodeJS.ErrnoException | undefined)?.code;
  return typeof code === 'string' && /^[A-Z0-9_]{1,32}$/.test(code) ? code : 'unknown';
}
