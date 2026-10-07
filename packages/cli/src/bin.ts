#!/usr/bin/env node
// 04.2.1-T2.1: the executable shim. It owns the only `process.exitCode` write; `main`
// stays pure and returns the code (04.2.1 §5, `bin evidgate`). The process-level
// backstops are exported so `bin.test.ts` can install them without running the CLI.
import { realpathSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

import { formatInternalError } from './errors.js';
import { ExitCode } from './exitCodes.js';
import { main } from './main.js';
import { createRedactingIo, type Io } from './redact.js';

/** The two writers the real process uses. */
function processIo(): Io {
  return {
    stdout: (text) => {
      process.stdout.write(text);
    },
    stderr: (text) => {
      process.stderr.write(text);
    },
  };
}

/**
 * Writes one redacted `internal_error` line and sets the exit code to 3. Shared by the
 * `main` backstop and the process-level handlers (contracts/cli.md §5).
 */
export function reportInternalError(error: unknown, io: Io): void {
  createRedactingIo(io).stderr(`${formatInternalError(error)}\n`);
  process.exitCode = ExitCode.Error;
}

/**
 * Registers the process-level backstops: an uncaught exception, an unhandled rejection
 * or a stdout/stderr `'error'` (EPIPE) exits 3 — never 1, the gate-FAIL code. Returns a
 * disposer so a spec can unregister them without running the CLI.
 */
export function installProcessHandlers(io: Io): () => void {
  const onFatal = (reason: unknown): void => {
    reportInternalError(reason, io);
  };
  // A broken stream cannot be written to; EPIPE on stdout exits 3 silently.
  const onStreamError = (): void => {
    process.exitCode = ExitCode.Error;
  };
  process.on('uncaughtException', onFatal);
  process.on('unhandledRejection', onFatal);
  process.stdout.on('error', onStreamError);
  process.stderr.on('error', onStreamError);
  return () => {
    process.removeListener('uncaughtException', onFatal);
    process.removeListener('unhandledRejection', onFatal);
    process.stdout.removeListener('error', onStreamError);
    process.stderr.removeListener('error', onStreamError);
  };
}

/** True when this module is the process entry point (not imported by a spec). */
function isEntryPoint(): boolean {
  const entry = process.argv[1];
  if (entry === undefined) {
    return false;
  }
  try {
    return import.meta.url === pathToFileURL(realpathSync(entry)).href;
  } catch {
    return false;
  }
}

if (isEntryPoint()) {
  const io = processIo();
  installProcessHandlers(io);
  main(process.argv.slice(2), process.env, io)
    .then((exitCode) => {
      process.exitCode = exitCode;
    })
    .catch((error: unknown) => {
      // `main` handles every failure itself; this is the last-resort backstop.
      reportInternalError(error, io);
    });
}
