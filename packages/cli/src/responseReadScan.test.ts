import { readdirSync, readFileSync } from 'node:fs';
import { join, relative } from 'node:path';

import { describe, expect, it } from 'vitest';

const sourceRoot = join(import.meta.dirname);

/** Direct whole-body reads of a Response; every one must go through `boundedResponse`. */
const wholeBodyRead =
  /\b(?:response|res|answer|reply|result\.response)\s*\.\s*(?:text|json|arrayBuffer|blob|formData|bytes)\s*\(/;

/** A manual reader on a response body: an own read loop must carry its own cap. */
const manualReader = /\.body\b[^\n]*\.getReader\s*\(|\bbody\s*\.getReader\s*\(/;

/** Source lines (1-based) that read a body whole. */
export function wholeBodyReadLines(source: string): number[] {
  return source
    .split('\n')
    .flatMap((line, index) =>
      wholeBodyRead.test(line) || manualReader.test(line) ? [index + 1] : [],
    );
}

/** Files allowed to read a body themselves, each with its own bound. */
const allowed: Readonly<Record<string, string>> = {
  'boundedResponse.ts': 'the bounded read itself',
  'commands/gate.ts': 'two own readers: the 256 KiB decision body and the 64 KiB error-body cut',
};

function productionFiles(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      return productionFiles(path);
    }
    const isTest = /\.(?:test|testing)\.ts$/.test(entry.name);
    return entry.name.endsWith('.ts') && !isTest ? [path] : [];
  });
}

describe('response body reads', () => {
  it('flags a whole-body read (positive control)', () => {
    expect(wholeBodyReadLines('const t = await response.text();')).toEqual([1]);
    expect(wholeBodyReadLines('x = await result.response.json()')).toEqual([1]);
    expect(wholeBodyReadLines('const r = response.body.getReader();')).toEqual([1]);
    expect(wholeBodyReadLines('await reader.read();')).toEqual([]);
  });

  it('finds none outside the bounded read', () => {
    const offenders = productionFiles(sourceRoot)
      .map((path) => relative(sourceRoot, path))
      .filter((path) => allowed[path] === undefined)
      .flatMap((path) =>
        wholeBodyReadLines(readFileSync(join(sourceRoot, path), 'utf8')).map(
          (line) => `${path}:${line}`,
        ),
      );

    expect(offenders).toEqual([]);
  });
});
