#!/usr/bin/env node
// 04.2.1-T2.1: the executable shim. It owns the only `process.exitCode` write; `main`
// stays pure and returns the code (04.2.1 §5, `bin evidgate`).
import { ExitCode } from './exitCodes.js';
import { main } from './main.js';
import type { Io } from './redact.js';

const io: Io = {
  stdout: (text) => {
    process.stdout.write(text);
  },
  stderr: (text) => {
    process.stderr.write(text);
  },
};

main(process.argv.slice(2), process.env, io)
  .then((exitCode) => {
    process.exitCode = exitCode;
  })
  .catch((error: unknown) => {
    // `main` handles every failure itself; this is the last-resort backstop.
    const message = error instanceof Error ? error.message : 'unexpected failure';
    process.stderr.write(`evidgate: error internal_error: ${message}\n`);
    process.exitCode = ExitCode.Error;
  });
