import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { installProcessHandlers, reportInternalError } from './bin.js';
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
});
