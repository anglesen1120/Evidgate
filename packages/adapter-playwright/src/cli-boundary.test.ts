// 04.3.3-T2 (D-Q1, 04.3.3 §7): the dependency arrow between this reporter and
// `@evidgate/cli` points one way, and only through a single lazy `import()`.
//
// Why a source-level spec: the reporter needs the CLI's `uploadFile()` to ship an
// attachment, but it must stay usable — and its spool must stay independent — when the CLI
// package is absent or broken (AC5: a failed upload never loses a test result). So the
// package declares `@evidgate/cli` as a workspace dependency and loads it at RUNTIME in
// exactly one place, `src/attach.ts`, inside one dynamic `import('@evidgate/cli')` that
// only runs when a run actually has an uploadable attachment. Every other file may only
// take TYPES from it (`import type … from '@evidgate/cli'`), which emits nothing. A second
// runtime load would be a static import in disguise: it would make the reporter's module
// graph depend on the CLI, and the failure path (`attachments_unavailable`) unreachable.
//
// The reverse direction is the same rule from the other side: `packages/cli` never imports
// this adapter, or the arrow would point both ways and npm workspaces would have a cycle.
// Comments in the CLI may name the reporter (it ports the writer lock and reads the spool);
// an IMPORT SPECIFIER may not — the CLI half of this pair lives in
// `packages/cli/src/attachmentUpload.imports.test.ts` ("keeps_the_production_sources_free_of_adapter_imports")
// with the same regex, so the two specs cannot drift apart.
import { readFileSync, readdirSync } from 'node:fs';
import { basename, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

/** `packages/adapter-playwright/src` — the specs live beside the sources they scan. */
const reporterSrc = fileURLToPath(new URL('.', import.meta.url));
const repoRoot = fileURLToPath(new URL('../../../', import.meta.url));
/** `packages/cli/src` — the other end of the dependency arrow. */
const cliSrc = join(repoRoot, 'packages', 'cli', 'src');

/** The reporter file allowed to load the CLI at runtime (D-Q1). */
const ATTACH_FILE = 'attach.ts';

/** The quoted specifier, in either quote style. */
const CLI_SPECIFIER = /(['"])@evidgate\/cli\1/gu;

/**
 * A type-only import of the CLI. Deliberately a strict grammar (`import type { … }`, a
 * namespace, or a default binding, then `from` IMMEDIATELY): a loose `[\s\S]*?from` would
 * span into the next statement and could cover a value import it must not cover.
 */
const TYPE_IMPORT =
  /import\s+type\s+(?:\{[^}]*\}|\*\s+as\s+[A-Za-z_$][\w$]*|[A-Za-z_$][\w$]*)\s+from\s*(['"])@evidgate\/cli\1/gu;

/**
 * `typeof import('@evidgate/cli')` — a TYPE position, emitted nowhere. The reporter's
 * `importCli` test seam is typed this way, and so is every annotation that names the CLI
 * module shape.
 */
const TYPE_QUERY = /typeof\s+import\s*\(\s*(['"])@evidgate\/cli\1\s*\)/gu;

/**
 * A runtime dynamic import: the one shape `attach.ts` may use, and only once. A leading
 * bundler comment (`import(/* webpackIgnore: true *\/ '…')`) is tolerated, because it
 * changes nothing about the load — the occurrence is still counted, and its file checked.
 */
const DYNAMIC_IMPORT =
  /(?<!typeof\s)import\s*\(\s*(?:\/\*[\s\S]{0,200}?\*\/\s*)?(['"])@evidgate\/cli\1\s*,?\s*\)/gu;

/** Any `from '@evidgate/cli'` clause, including the value imports that must not exist. */
const FROM_CLI = /from\s*(['"])@evidgate\/cli\1/gu;

/** `import '@evidgate/cli';` — a side-effect import runs the module for real. */
const SIDE_EFFECT_IMPORT = /(?:^|[;\s}])import\s*(['"])@evidgate\/cli\1/mu;

/** `require('@evidgate/cli')` through a CommonJS interop path. */
const REQUIRE_CLI = /\brequire\s*\(\s*(['"])@evidgate\/cli\1\s*\)/u;

/**
 * The forbidden reverse edge, byte-for-byte the regex of the CLI's own boundary spec: an
 * import/require/dynamic-import specifier that names the adapter. Prose is not a dependency.
 */
const FORBIDDEN_ADAPTER_IMPORT =
  /(?:\bfrom\s*|\bimport\s*\(\s*|\brequire\s*\(\s*)['"][^'"]*(?:adapter-playwright|playwright-reporter)[^'"]*['"]/u;

/**
 * Every non-test `.ts` file under `directory`, recursively (sorted for stable failures).
 * A test file is never part of a package's runtime graph, so the specs themselves — which
 * legitimately name both packages — stay out of the scan.
 */
function productionSources(directory: string): string[] {
  const found: string[] = [];
  const entries = readdirSync(directory, { withFileTypes: true });
  // Sorted so a failure lists its files in a stable order.
  entries.sort((a, b) => a.name.localeCompare(b.name));
  for (const entry of entries) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      found.push(...productionSources(path));
      continue;
    }
    if (entry.name.endsWith('.ts') && !entry.name.endsWith('.test.ts')) {
      found.push(path);
    }
  }
  return found;
}

/** How many times `pattern` matches `text`; the pattern is re-cloned so no `lastIndex` leaks. */
function count(text: string, pattern: RegExp): number {
  return [...text.matchAll(new RegExp(pattern.source, 'gu'))].length;
}

describe('@evidgate/cli boundary (04.3.3-T2, D-Q1)', () => {
  const sources = productionSources(reporterSrc);

  it('has sources to scan', () => {
    // A mis-pointed directory would make every assertion below vacuously true.
    expect(sources.length).toBeGreaterThan(10);
    expect(sources.map((file) => basename(file))).toContain(ATTACH_FILE);
  });

  it('AC5: only attach.ts loads the CLI at runtime, and only through one dynamic import', () => {
    const unaccounted: string[] = [];
    const dynamic = new Map<string, number>();
    for (const file of sources) {
      const text = readFileSync(file, 'utf8');
      const name = relative(reporterSrc, file);
      const runtime = count(text, DYNAMIC_IMPORT);
      if (runtime > 0) dynamic.set(name, runtime);
      // Every quoted specifier must be one of the three shapes that emit nothing at run
      // time except the one dynamic import: anything left over is a value reference.
      const explained = count(text, TYPE_IMPORT) + count(text, TYPE_QUERY) + runtime;
      if (count(text, CLI_SPECIFIER) > explained) unaccounted.push(name);
    }

    expect(
      unaccounted,
      'a value reference to @evidgate/cli would make the module graph depend on it ' +
        `(an import type is fine; only ${ATTACH_FILE} may load it at runtime)`,
    ).toEqual([]);
    // Exactly one runtime load in the whole package, in exactly one file.
    expect(
      [...dynamic.keys()],
      `the runtime import('@evidgate/cli') must live in ${ATTACH_FILE} only`,
    ).toEqual([ATTACH_FILE]);
    expect(dynamic.get(ATTACH_FILE), `${ATTACH_FILE} must lazily import the CLI exactly once`).toBe(
      1,
    );
  });

  it('AC5: no static value import of @evidgate/cli anywhere', () => {
    const offenders: string[] = [];
    for (const file of sources) {
      const text = readFileSync(file, 'utf8');
      const name = relative(reporterSrc, file);
      // A `from '@evidgate/cli'` clause that is not the `import type` form above is a
      // static value import; a side-effect import and a `require` are both static too.
      if (count(text, FROM_CLI) > count(text, TYPE_IMPORT)) offenders.push(`${name}: value import`);
      if (SIDE_EFFECT_IMPORT.test(text)) offenders.push(`${name}: side-effect import`);
      if (REQUIRE_CLI.test(text)) offenders.push(`${name}: require`);
    }
    expect(offenders, `static imports of "@evidgate/cli": ${offenders.join(', ')}`).toEqual([]);
  });

  it('§7: the CLI never imports the Playwright adapter (one-way dependency)', () => {
    const cliSources = productionSources(cliSrc);
    expect(cliSources.length).toBeGreaterThan(10);

    const offenders: string[] = [];
    for (const file of cliSources) {
      if (FORBIDDEN_ADAPTER_IMPORT.test(readFileSync(file, 'utf8'))) {
        offenders.push(relative(cliSrc, file));
      }
    }
    expect(offenders, `packages/cli imports the adapter from: ${offenders.join(', ')}`).toEqual([]);
  });
});
