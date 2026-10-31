// The `CliError`s `events send` raises itself, with their exact codes and hints: every one of
// them is user-facing contract (contracts/cli.md §5), so the wording is shared from one place.
import { CliError } from '../errors.js';

/**
 * The usage-error hint `main.ts` attaches to its parse-level usage errors (`helpHint`
 * there, module-private). Repeated here so the command-level flag usage errors carry the
 * same pointer (contracts/cli.md §5 `usage` row); keep the two literals in sync.
 */
export const helpHint = "run 'evidgate --help' for usage";

/** A `--from` input problem: unreadable, not JSON, a gap, a duplicate or an odd line. */
export function inputError(message: string): CliError {
  return new CliError(message, {
    code: 'invalid_spool_input',
    hint: 'the intake reads one JSON event envelope per line, in ascending sequence order',
  });
}

/** A line the envelope schema refuses (or that is not an envelope at all). */
export function envelopeInvalid(message: string): CliError {
  return new CliError(message, {
    code: 'envelope_invalid',
    hint: 'each spool line must be one event envelope',
  });
}

export function usageError(message: string): CliError {
  return new CliError(message, { code: 'usage', hint: helpHint });
}

/**
 * The cap failure found before sending: the run directory is already over
 * `EVIDGATE_SPOOL_MAX_BYTES`, so nothing can be appended and the drain stops rather than send
 * part of a spool it cannot grow. The message names the run, the bytes in use, the cap and
 * the unsent range of the producer being drained.
 */
export function capBreach(o: {
  readonly runId: string;
  readonly producerId: string;
  readonly usedBytes: number;
  readonly capBytes: number;
  readonly first: number;
  readonly last: number;
}): CliError {
  return new CliError(
    `spool cap exceeded for run ${o.runId}: ${o.usedBytes} bytes in use > ${o.capBytes} bytes ` +
      `(EVIDGATE_SPOOL_MAX_BYTES); unsent sequences ${o.first}..${o.last} of producer ` +
      `${o.producerId}`,
    {
      code: 'spool_cap_exceeded',
      hint:
        `raise EVIDGATE_SPOOL_MAX_BYTES (currently ${o.capBytes}), or send and finalize the ` +
        'spooled events before queueing more',
    },
  );
}
