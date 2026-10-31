// The exit-code registry shared with `gate` (contracts/cli.md). Auth and configuration
// subcodes never change the exit code; they surface only as the stderr `error.code`.

/**
 * Process exit codes. `Ok`/`Fail`/`Inconclusive` are the gate verdicts; every tool, usage,
 * configuration, transport or auth error is `Error`.
 */
export const ExitCode = {
  /** Success / gate PASS. */
  Ok: 0,
  /** Gate FAIL. */
  Fail: 1,
  /** Gate INCONCLUSIVE. */
  Inconclusive: 2,
  /** Tool, usage, configuration, transport or auth error. */
  Error: 3,
} as const;

export type ExitCode = (typeof ExitCode)[keyof typeof ExitCode];

/**
 * 10–19 are reserved for replay and are not emitted by any command yet.
 */
export const reservedExitCodeRange = { min: 10, max: 19 } as const;
