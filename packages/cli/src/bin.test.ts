import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { applyExitCode, installProcessHandlers, reportInternalError } from './bin.js';
import { ExitCode } from './exitCodes.js';
import { resetSecrets, type Io } from './redact.js';

function capture(): { io: Io; stdout: string[]; stderr: string[] } {
  const stdout: string[] = [];
  const stderr: string[] = [];
  return {
    io: { stdout: (text) => stdout.push(text), stderr: (text) => stderr.push(text) },
    stdout,
    stderr,
  };
}

// 04.2.1-T2.1 / CR6, SEC5: the process-level backstops. `bin.ts` exports them so a spec
// can install and exercise them without running the CLI or killing the test process.
describe('bin process backstops', () => {
  const originalExitCode = process.exitCode;

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(() => {
    process.exitCode = originalExitCode;
  });

  it('writes one redacted internal_error line and exits 3', () => {
    const { io, stderr } = capture();
    reportInternalError(new Error('boom'), io);
    expect(stderr.join('')).toBe('evidgate: error internal_error: boom\n');
    expect(process.exitCode).toBe(3);
  });

  it('escapes control characters in the backstop message', () => {
    const { io, stderr } = capture();
    reportInternalError(new Error('x\n::stop-commands::tok'), io);
    expect(stderr.join('')).not.toContain('\n::');
    expect(stderr.join('')).toContain('\\u000a');
  });

  it('registers and disposes the process-level handlers', () => {
    const { io } = capture();
    const before = {
      uncaught: process.listenerCount('uncaughtException'),
      rejection: process.listenerCount('unhandledRejection'),
      stdout: process.stdout.listenerCount('error'),
      stderr: process.stderr.listenerCount('error'),
    };
    const dispose = installProcessHandlers(io);
    expect(process.listenerCount('uncaughtException')).toBe(before.uncaught + 1);
    expect(process.listenerCount('unhandledRejection')).toBe(before.rejection + 1);
    expect(process.stdout.listenerCount('error')).toBe(before.stdout + 1);
    expect(process.stderr.listenerCount('error')).toBe(before.stderr + 1);
    dispose();
    expect(process.listenerCount('uncaughtException')).toBe(before.uncaught);
    expect(process.listenerCount('unhandledRejection')).toBe(before.rejection);
    expect(process.stdout.listenerCount('error')).toBe(before.stdout);
    expect(process.stderr.listenerCount('error')).toBe(before.stderr);
  });

  it('reports an uncaught exception through the redacting io and exits 3', () => {
    const { io, stderr } = capture();
    const before = new Set(process.listeners('uncaughtException'));
    const dispose = installProcessHandlers(io);
    const added = process
      .listeners('uncaughtException')
      .filter((listener) => !before.has(listener)) as Array<(error: unknown) => void>;
    expect(added).toHaveLength(1);
    added[0]?.(new Error('uncaught boom'));
    dispose();
    expect(stderr.join('')).toContain('evidgate: error internal_error: uncaught boom');
    expect(process.exitCode).toBe(3);
  });

  // D-1: the `.then` after `main()` must not overwrite an exit code a process-level
  // backstop already recorded. `applyExitCode` is the exported seam for that ordering.
  it('records main exit code when no backstop has run (D-1)', () => {
    process.exitCode = undefined;
    applyExitCode(ExitCode.Fail);
    expect(process.exitCode).toBe(ExitCode.Fail);
  });

  it('never lowers an exit code a backstop already recorded (D-1)', () => {
    process.exitCode = ExitCode.Error;
    applyExitCode(ExitCode.Ok);
    expect(process.exitCode).toBe(ExitCode.Error);
  });

  it('keeps the backstop exit code when main resolves after a fatal handler (D-1)', () => {
    const { io } = capture();
    const before = new Set(process.listeners('uncaughtException'));
    const dispose = installProcessHandlers(io);
    const added = process
      .listeners('uncaughtException')
      .filter((listener) => !before.has(listener)) as Array<(error: unknown) => void>;
    process.exitCode = undefined;
    added[0]?.(new Error('fatal before main settled'));
    applyExitCode(ExitCode.Ok);
    dispose();
    expect(process.exitCode).toBe(ExitCode.Error);
  });
});
