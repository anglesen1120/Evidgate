// 04.2.1-T2.1: the exit-code registry shared with 07.6.1's `gate` (00 §9.3,
// contracts/cli.md). Auth and configuration subcodes never change the exit code — they
// surface only as the stderr `error.code` (04.2.1 §5, AC4/AC5).

/**
 * Process exit codes. `Ok`/`Fail`/`Inconclusive` are the gate verdicts 07.6.1 consumes;
 * every tool, usage, configuration, transport or auth error is `Error`.
 */
export const ExitCode = {
  /** Success / gate PASS. */
  Ok: 0,
  /** Gate FAIL (07.6.1). */
  Fail: 1,
  /** Gate INCONCLUSIVE (07.6.1). */
  Inconclusive: 2,
  /** Tool, usage, configuration, transport or auth error. */
  Error: 3,
} as const;

export type ExitCode = (typeof ExitCode)[keyof typeof ExitCode];

/**
 * 10–19 are reserved for replay (04.9.1-T4) and are not emitted by any command yet
 * (04.2.1 §5, OI-04-06).
 */
export const reservedExitCodeRange = { min: 10, max: 19 } as const;
