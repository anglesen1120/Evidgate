// F5 (P3): every spool open is `O_NONBLOCK` and a write target is `lstat`-refused unless it
// is a regular file, so a FIFO planted at a record or chunk path is refused with a clear
// error instead of blocking the reporter — an `open` of a FIFO waits for its peer forever,
// which would hang a test run and, in production, a Playwright worker at `onEnd`.
import { execFileSync } from 'node:child_process';
import { lstatSync, mkdirSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, describe, expect, it } from 'vitest';

import { buildEnvelope, EXT_SCHEMA, type UnsignedEnvelope } from './envelope.js';
import { SHARDS_FILE, SHARDS_TMP_FILE, SpoolWriter, type SpoolWriterOptions } from './spool.js';

/**
 * A blocked open would hang the whole run, so each spec is given a short deadline. It is a
 * backstop only: the refusals below happen before any waiting open can be reached.
 */
const NO_HANG_MS = 5000;

const tempDirs: string[] = [];

function tempSpool(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-fifo-'));
  tempDirs.push(dir);
  return dir;
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

/** `mkfifo` is POSIX-only; the specs below are skipped where the tool is absent. */
const mkfifoAvailable = (() => {
  if (process.platform === 'win32') return false;
  try {
    execFileSync('mkfifo', [join(tempSpool(), 'probe')], { stdio: 'ignore' });
    return true;
  } catch {
    return false;
  }
})();

function producerDir(spool: string): string {
  return join(spool, 'run-1', 'pw-0');
}

function options(spool: string, overrides: Partial<SpoolWriterOptions> = {}): SpoolWriterOptions {
  return { spoolDir: spool, runId: 'run-1', producerId: 'pw-0', ...overrides };
}

function envelope(producerId: string): UnsignedEnvelope {
  return buildEnvelope({
    producer_id: producerId,
    execution_key: '0'.repeat(64),
    attempt_no: 1,
    type: 'test.started',
    ext: { schema: EXT_SCHEMA },
  });
}

function mkfifo(path: string): void {
  execFileSync('mkfifo', [path], { stdio: 'ignore' });
}

describe('planted FIFOs (F5)', () => {
  it(
    'refuses a FIFO at shards.json instead of blocking the record read',
    { timeout: NO_HANG_MS },
    () => {
      if (!mkfifoAvailable) return;
      const spool = tempSpool();
      const dir = producerDir(spool);
      mkdirSync(dir, { recursive: true });
      const record = join(dir, SHARDS_FILE);
      mkfifo(record);

      expect(() => new SpoolWriter(options(spool))).toThrow(
        /refusing to read non-regular spool record/,
      );
      // Nothing was opened for writing through it, and it is still a pipe.
      expect(lstatSync(record).isFIFO()).toBe(true);
    },
  );

  it(
    'refuses a FIFO at a chunk instead of blocking the chunk scan',
    { timeout: NO_HANG_MS },
    () => {
      if (!mkfifoAvailable) return;
      const spool = tempSpool();
      const dir = producerDir(spool);
      mkdirSync(dir, { recursive: true });
      const chunk = join(dir, '000001.jsonl');
      mkfifo(chunk);

      expect(() => new SpoolWriter(options(spool))).toThrow(
        /refusing to use non-regular spool file/,
      );
      expect(lstatSync(chunk).isFIFO()).toBe(true);
    },
  );

  it(
    'refuses a FIFO at the chunk a rotation must open for append, without hanging',
    { timeout: NO_HANG_MS },
    () => {
      if (!mkfifoAvailable) return;
      const spool = tempSpool();
      const dir = producerDir(spool);
      // one line per chunk, so the second flush rotates into `000002.jsonl`
      const writer = new SpoolWriter(options(spool, { chunkMaxLines: 1 }));
      writer.append(envelope('pw-0'));
      writer.flush();
      const chunk = join(dir, '000002.jsonl');
      mkfifo(chunk);

      writer.append(envelope('pw-0'));
      expect(() => writer.flush()).toThrow(/non-regular spool file/);
      // The failed rotation restored chunk 1 and kept the envelope queued (CR4/PS4), and
      // not one byte went into the pipe.
      expect(writer.pendingCount).toBe(1);
      expect(readFileSync(join(dir, '000001.jsonl'), 'utf8').trim().split('\n')).toHaveLength(1);
      expect(lstatSync(chunk).isFIFO()).toBe(true);
      // The close-time retry hits the same refusal rather than blocking.
      expect(() => writer.close()).toThrow(/non-regular spool file/);
    },
  );

  it(
    'replaces a FIFO planted at the staging name instead of writing into it',
    { timeout: NO_HANG_MS },
    () => {
      if (!mkfifoAvailable) return;
      const spool = tempSpool();
      const dir = producerDir(spool);
      const writer = new SpoolWriter(options(spool));
      const staging = join(dir, SHARDS_TMP_FILE);
      mkfifo(staging);

      writer.append(envelope('pw-0'));
      expect(() => writer.close()).not.toThrow();
      // The leftover staging entry is removed before the O_EXCL create, so the record is a
      // real file with the right value and the pipe is gone.
      expect(lstatSync(join(dir, SHARDS_FILE)).isFile()).toBe(true);
      expect(readFileSync(join(dir, SHARDS_FILE), 'utf8')).toContain('"sequence_next":2');
      expect(lstatSync(staging, { throwIfNoEntry: false })).toBeUndefined();
    },
  );
});
