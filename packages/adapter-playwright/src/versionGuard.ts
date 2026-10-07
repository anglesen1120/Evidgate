// 04.3.1-T1.3: the engine version guard (AC5, 04 §8 risk row). The reporter refuses to
// run against a @playwright/test outside `^1.63` so a changed reporter API can never be
// silently mis-mapped. The check is hand-written (no semver dependency is allowed) and
// the version source is injectable for tests.
//
// Prerelease handling: `^1.63` means `>=1.63.0 <2.0.0` and, as in npm's semver, a
// prerelease does not satisfy it (`1.63.0-beta.1` is rejected) — the pinned release is
// the only tested engine.
import { createRequire } from 'node:module';

export const SUPPORTED_ENGINE_RANGE = '^1.63';
export const SUPPORTED_ENGINE_MIN = '1.63.0';
export const SUPPORTED_ENGINE_MAX_EXCLUSIVE = '2.0.0';

/** Thrown at construction when the installed @playwright/test is outside the range. */
export class EvidgateUnsupportedEngineError extends Error {
  readonly installed: string;
  readonly supported: string;

  constructor(installed: string, supported: string = SUPPORTED_ENGINE_RANGE) {
    super(
      `@evidgate/playwright-reporter requires @playwright/test ${supported} ` +
        `(>=${SUPPORTED_ENGINE_MIN} <${SUPPORTED_ENGINE_MAX_EXCLUSIVE}); installed ${installed}`,
    );
    this.name = 'EvidgateUnsupportedEngineError';
    this.installed = installed;
    this.supported = supported;
  }
}

interface ParsedVersion {
  major: number;
  minor: number;
  patch: number;
  prerelease: string;
}

const SEMVER_PATTERN = /^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$/;

/** Parses `major.minor.patch[-prerelease][+build]`; returns null for anything else. */
export function parseVersion(version: string): ParsedVersion | null {
  const match = SEMVER_PATTERN.exec(version.trim());
  if (match === null) return null;
  const [, major, minor, patch, prerelease] = match;
  return {
    major: Number(major),
    minor: Number(minor),
    patch: Number(patch),
    prerelease: prerelease ?? '',
  };
}

/** The hand-written `^1.63` check: `>=1.63.0 <2.0.0`, prereleases rejected. */
export function satisfiesSupportedEngine(version: string): boolean {
  const parsed = parseVersion(version);
  if (parsed === null) return false;
  if (parsed.prerelease !== '') return false;
  if (parsed.major !== 1) return false;
  return parsed.minor >= 63;
}

/** Throws {@link EvidgateUnsupportedEngineError} when the version is outside the range. */
export function assertSupportedEngine(version: string): void {
  if (!satisfiesSupportedEngine(version)) {
    throw new EvidgateUnsupportedEngineError(version);
  }
}

/**
 * Reads the installed `@playwright/test` version from its package.json (D8: the engine
 * version is detected at runtime and travels in the capability document; the package
 * pins no engine of its own beyond the guard).
 */
export function readInstalledPlaywrightVersion(): string {
  const require = createRequire(import.meta.url);
  const pkg = require('@playwright/test/package.json') as { version?: unknown };
  if (typeof pkg.version !== 'string' || pkg.version.length === 0) {
    throw new Error('could not read the installed @playwright/test version from its package.json');
  }
  return pkg.version;
}
