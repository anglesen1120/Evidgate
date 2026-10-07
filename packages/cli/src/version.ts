// 04.2.4-T2.1: the CLI version, read once from the manifest next to `dist/` (or
// `src/`). Extracted from `main.ts` so `--version` and the `quickstart` template pin
// (`@evidgate/cli@<version>`) read the same value from the same place — a second
// reader could drift from the published package version.
import { readFileSync } from 'node:fs';

/** The package version, read from the manifest next to `dist/` (or `src/`). */
export function readCliVersion(): string {
  const manifest = JSON.parse(
    readFileSync(new URL('../package.json', import.meta.url), 'utf8'),
  ) as { version?: unknown };
  return typeof manifest.version === 'string' ? manifest.version : '0.0.0';
}
