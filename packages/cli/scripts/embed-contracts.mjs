#!/usr/bin/env node
// Generates packages/cli/src/contracts.generated.ts from the repository `contracts/` tree.
//
// The CLI validates spool lines against the same JSON Schemas the server mirrors, and a
// published package cannot read the repository it was built from, so the schema texts are
// compiled into the package. Regeneration is deterministic (sorted keys, compact JSON), and
// the parity spec fails when this output and `contracts/` disagree: the file is a build
// product, never edited by hand.
import { existsSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const packageDirectory = resolve(scriptDirectory, '..');
const repositoryRoot = resolve(packageDirectory, '../..');
const contractsDirectory = join(repositoryRoot, 'contracts');
const outputFile = join(packageDirectory, 'src', 'contracts.generated.ts');

/**
 * The `contracts/` sub-directories the validator loads, recursively. It mirrors
 * `schemaRoots` in `src/validate.ts`; the parity spec compares the generated keys against
 * this tree, so a root added on one side and not the other fails the tests.
 */
const schemaRoots = ['schemas', 'adapter', 'manifest', 'catalog'];

/** The `*.json` files of one directory tree, ordinally sorted at every level. */
function listJsonFiles(directory) {
  const entries = readdirSync(directory, { withFileTypes: true }).sort((left, right) =>
    left.name < right.name ? -1 : left.name > right.name ? 1 : 0,
  );
  const files = [];
  for (const entry of entries) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      files.push(...listJsonFiles(path));
    } else if (entry.name.endsWith('.json')) {
      files.push(path);
    }
  }
  return files;
}

/**
 * Every schema text keyed by file name without `.json` — the key the validator addresses a
 * document by. A duplicate key is a contract defect (two files would load under one key),
 * so generation fails here rather than shipping an arbitrary winner.
 */
function collectSchemas() {
  const texts = new Map();
  for (const root of schemaRoots) {
    const directory = join(contractsDirectory, root);
    if (!existsSync(directory)) {
      continue;
    }
    for (const file of listJsonFiles(directory)) {
      const key = basename(file, '.json');
      if (texts.has(key)) {
        throw new Error(`duplicate contract schema key '${key}': '${file}' loads under it twice`);
      }
      // Compact JSON keeps the output byte-stable: reformatting a contract file cannot
      // change the generated module, only its content can.
      texts.set(key, JSON.stringify(JSON.parse(readFileSync(file, 'utf8'))));
    }
  }
  if (texts.size === 0) {
    throw new Error(`no contract schema (*.json) found under '${contractsDirectory}'`);
  }
  return new Map([...texts].sort(([left], [right]) => (left < right ? -1 : left > right ? 1 : 0)));
}

const schemas = collectSchemas();
const lines = [
  '// Generated from the repository `contracts/` tree by packages/cli/scripts/embed-contracts.mjs.',
  '// The CLI validates without a checkout, so the schema texts are compiled in; regenerate with',
  '// `npm run embed:contracts -w @evidgate/cli` instead of editing this file.',
  '// prettier-ignore',
  'export const contractSchemaTexts: Readonly<Record<string, string>> = {',
];
for (const [key, text] of schemas) {
  lines.push(`  ${JSON.stringify(key)}: ${JSON.stringify(text)},`);
}
lines.push('};', '');

writeFileSync(outputFile, lines.join('\n'), 'utf8');
process.stdout.write(`embedded ${schemas.size} contract schemas into ${outputFile}\n`);
