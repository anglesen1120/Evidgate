// 04.3.1-T1.3: the version guard (AC5). `^1.63` accepts 1.63.x and the rest of the 1.x
// line, rejects 1.62.x, 2.x and prereleases, and a rejected engine leaves no spool file
// behind (no partial emit).
import { mkdtempSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, describe, expect, it } from 'vitest';

import { EvidgateReporter } from './reporter.js';
import {
  EvidgateUnsupportedEngineError,
  parseVersion,
  satisfiesSupportedEngine,
} from './versionGuard.js';

const tempDirs: string[] = [];

function tempSpool(): string {
  const dir = mkdtempSync(join(tmpdir(), 'evidgate-guard-'));
  tempDirs.push(dir);
  return dir;
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true });
});

describe('version guard', () => {
  it.each(['1.63.0', '1.63.5', '1.64.0', '1.99.0'])('accepts %s', (version) => {
    expect(satisfiesSupportedEngine(version)).toBe(true);
  });

  it.each(['1.62.9', '2.0.0', '1.63.0-beta.1', '1.62.0-rc.1', 'not-a-version', ''])(
    'rejects %s',
    (version) => {
      expect(satisfiesSupportedEngine(version)).toBe(false);
    },
  );

  it('parses a prerelease and a build suffix', () => {
    expect(parseVersion('1.63.0-beta.1+build.7')).toEqual({
      major: 1,
      minor: 63,
      patch: 0,
      prerelease: 'beta.1',
    });
  });

  it('throws EvidgateUnsupportedEngineError naming installed vs supported, with no spool files', () => {
    const spool = tempSpool();
    let thrown: unknown;
    try {
      new EvidgateReporter({
        spool_dir: spool,
        run_id: 'run-1',
        producer_id: 'pw-0',
        engineVersion: '1.62.9',
      });
    } catch (error) {
      thrown = error;
    }

    expect(thrown).toBeInstanceOf(EvidgateUnsupportedEngineError);
    const error = thrown as EvidgateUnsupportedEngineError;
    expect(error.installed).toBe('1.62.9');
    expect(error.supported).toBe('^1.63');
    expect(error.message).toContain('1.62.9');
    expect(error.message).toContain('^1.63');
    expect(readdirSync(spool)).toEqual([]);
  });

  it('throws for a 2.x engine', () => {
    const spool = tempSpool();
    expect(
      () =>
        new EvidgateReporter({
          spool_dir: spool,
          run_id: 'run-1',
          producer_id: 'pw-0',
          engineVersion: '2.0.0',
        }),
    ).toThrow(EvidgateUnsupportedEngineError);
    expect(readdirSync(spool)).toEqual([]);
  });

  it('constructs for a supported engine', () => {
    const spool = tempSpool();
    expect(
      () =>
        new EvidgateReporter({
          spool_dir: spool,
          run_id: 'run-1',
          producer_id: 'pw-0',
          engineVersion: '1.63.0',
        }),
    ).not.toThrow();
  });
});
