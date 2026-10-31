// `evidgate events send` — the CLI's send path (contracts/cli.md §11). One invocation does up
// to two things in this order: **intake** (`--from <file|->`), which validates the input and
// spools it per producer, then **drain**, which packs the unacked events and sends them under
// one deadline. The halves live in `eventsSendIntake.ts` and `eventsSendDrain.ts`; the flag
// validators in `eventsSendArgs.ts` and the error wording in `eventsSendErrors.ts`.
//
// A 401 purges the cached CI token (contracts/cli.md §7); 403/422 and every other 4xx keep it
// and surface the server's `error.code` (when it is a safe identifier) and `request_id`.
import { assertValidSpoolId } from '../spool.js';
import { resolveSpoolDir, spoolDirMissingError } from '../config.js';
import { ExitCode } from '../exitCodes.js';

import { createEnvelopeValidator } from '../batcher.js';

import {
  flagValue,
  producerPattern,
  requiredFlag,
  stdinName,
  uuidPattern,
} from './eventsSendArgs.js';
import { usageError } from './eventsSendErrors.js';
import { intake } from './eventsSendIntake.js';
import { drain } from './eventsSendDrain.js';

import type { CommandContext, CommandDefinition } from './index.js';
import type { EventsSendSeams } from './eventsSendTypes.js';

export const eventsSendCommand: CommandDefinition = {
  name: 'events send',
  summary: 'send the spooled event envelopes of a run',
  requireToken: true,
  // The spool root is required and refused here, before the `--verbose` dump and before any
  // flag, token or filesystem work. Forcing `spoolSettings()` is also what makes a malformed
  // `EVIDGATE_SPOOL_MAX_BYTES` or `EVIDGATE_SEND_DEADLINE` an `invalid_config` only for the
  // commands that read the spool surface: `runs create`, `auth`, `config check` and
  // `manifests upload` never read those variables at all.
  validateConfig: (config) => {
    if (config.spoolSettings().spoolDir === undefined) {
      throw spoolDirMissingError();
    }
  },
  options: {
    // `multiple: true` is what makes a repetition observable: with the default `false`
    // `parseArgs` keeps the last value and reports nothing, so `--run a --run b` would
    // quietly send `b`.
    run: { type: 'string', multiple: true },
    producer: { type: 'string', multiple: true },
    from: { type: 'string', multiple: true },
  },
  run: (context) => runEventsSend(context),
};

async function runEventsSend(context: CommandContext): Promise<ExitCode> {
  const { config, io, values, env, deps } = context;
  const seams = deps as EventsSendSeams;

  // Flags first, token second: an invalid flag reaches neither the token exchange nor the
  // spool. Both ids become path segments verbatim, so the spool-id rules are applied
  // in addition to the wire shapes.
  const runId = requiredFlag(values, 'run');
  if (!uuidPattern.test(runId)) {
    throw usageError('--run must be a UUID');
  }
  assertValidSpoolId('run_id', runId);
  const onlyProducer = flagValue(values, 'producer');
  if (onlyProducer !== undefined) {
    if (!producerPattern.test(onlyProducer)) {
      throw usageError('--producer must be 1..128 of [A-Za-z0-9_.-], starting alphanumeric');
    }
    assertValidSpoolId('producer_id', onlyProducer);
  }
  const from = flagValue(values, 'from');
  if (from !== undefined && from.trim().length === 0) {
    throw usageError(`--from must be a file path or ${stdinName}`);
  }

  // Configuration before the token too: `validateConfig` already forced the lazy
  // settings, so this is a memoised read and cannot re-parse or re-fail.
  const settings = config.spoolSettings();
  const spoolDir = settings.spoolDir ?? resolveSpoolDir(env);
  const maxBytes = settings.spoolMaxBytes;
  // One Ajv compile for the whole invocation: the validator is stateless, and both the
  // intake and the drain must apply exactly the same verdicts as the server. The schemas are
  // the ones compiled into the package, so no contracts directory is read.
  const validate = createEnvelopeValidator();

  if (from !== undefined) {
    await intake({
      spoolDir,
      runId,
      onlyProducer,
      from,
      maxBytes,
      validate,
      stdin: seams.stdin ?? process.stdin,
    });
  }

  return drain({
    config,
    runId,
    onlyProducer,
    spoolDir,
    maxBytes,
    sendDeadlineMs: settings.sendDeadlineMs,
    validate,
    io,
    seams,
    deps,
  });
}
