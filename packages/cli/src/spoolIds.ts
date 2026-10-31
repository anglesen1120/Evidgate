// `run_id`/`producer_id` become path segments verbatim, so they are validated as single safe
// segments and the resolved path is re-checked against the spool root: a run directory must
// stay inside the configured root even after a `..` that the pattern already refuses.
import { readdir } from 'node:fs/promises';
import { join, resolve, sep } from 'node:path';

import { InvalidSpoolIdError } from './spoolErrors.js';
import { directoryBytes } from './spoolFs.js';
import { spoolIdMaxLength } from './spoolLimits.js';

import type { Dirent } from 'node:fs';

/** The only characters allowed in a `run_id`/`producer_id` path segment (writer parity). */
const spoolIdPattern = /^[A-Za-z0-9._-]+$/;
const controlCharPattern = /[\u0000-\u001f\u007f]/;

/** Validates a spool path segment (the reporter's writer applies the same rule). */
export function assertValidSpoolId(field: 'run_id' | 'producer_id', value: string): void {
  if (typeof value !== 'string' || value.length === 0) {
    throw new InvalidSpoolIdError(field, String(value), 'must not be empty');
  }
  if (value === '.' || value === '..') {
    throw new InvalidSpoolIdError(field, value, 'must not be "." or ".."');
  }
  if (value.includes('/') || value.includes('\\')) {
    throw new InvalidSpoolIdError(field, value, 'must not contain a path separator');
  }
  if (controlCharPattern.test(value)) {
    throw new InvalidSpoolIdError(field, value, 'must not contain a control character');
  }
  if (!spoolIdPattern.test(value)) {
    throw new InvalidSpoolIdError(
      field,
      value,
      'must contain only letters, digits, ".", "_" and "-"',
    );
  }
  if (value.length > spoolIdMaxLength) {
    throw new InvalidSpoolIdError(field, value, `must be at most ${spoolIdMaxLength} characters`);
  }
}

/**
 * Defence in depth: even after id validation, the resolved directory must stay
 * inside the resolved spool root.
 */
export function assertWithinSpoolDir(
  spoolDir: string,
  directory: string,
  field: 'run_id' | 'producer_id',
  value: string,
): void {
  const root = resolve(spoolDir);
  const target = resolve(directory);
  if (target !== root && !target.startsWith(root + sep)) {
    throw new InvalidSpoolIdError(field, value, `resolves outside the spool directory ${root}`);
  }
}

/** The sorted producer ids of a run (valid single segments only; symlinks never listed). */
export async function listProducers(spoolDir: string, runId: string): Promise<string[]> {
  assertValidSpoolId('run_id', runId);
  const runDirectory = join(spoolDir, runId);
  assertWithinSpoolDir(spoolDir, runDirectory, 'run_id', runId);
  let entries: Dirent[];
  try {
    entries = await readdir(runDirectory, { withFileTypes: true });
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') return [];
    throw error;
  }
  const producers: string[] = [];
  for (const entry of entries) {
    if (!entry.isDirectory() || entry.isSymbolicLink()) continue;
    if (entry.name === '.' || entry.name === '..') continue;
    if (!spoolIdPattern.test(entry.name) || entry.name.length > spoolIdMaxLength) continue;
    producers.push(entry.name);
  }
  return producers.sort(compareStrings);
}

/** Total bytes of every regular file under `<spool_dir>/<run_id>` (the cap base). */
export function runDirBytes(spoolDir: string, runId: string): Promise<number> {
  assertValidSpoolId('run_id', runId);
  const runDirectory = join(spoolDir, runId);
  assertWithinSpoolDir(spoolDir, runDirectory, 'run_id', runId);
  return directoryBytes(runDirectory);
}

function compareStrings(a: string, b: string): number {
  if (a === b) return 0;
  return a < b ? -1 : 1;
}
