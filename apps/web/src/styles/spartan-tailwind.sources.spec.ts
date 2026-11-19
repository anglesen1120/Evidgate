// `source(none)` turns Tailwind's automatic detection off, so a feature directory missing from the
// `@source` list contributes no classes at all: its screens lose the arbitrary utilities that no
// other registered directory happens to use (width caps, spacing scales, height caps). The list is
// therefore an invariant over every feature that renders a template, not a hand-kept convenience.
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

function workspace(): string {
  const root = [resolve('.'), resolve('apps/web')].find((candidate) =>
    existsSync(join(candidate, 'src/styles/spartan-tailwind.css')),
  );
  if (root === undefined) throw new Error('Cannot locate apps/web');
  return root;
}

const web = workspace();
const stylesheet = readFileSync(join(web, 'src/styles/spartan-tailwind.css'), 'utf8');

function registered(): Set<string> {
  const names = new Set<string>();
  for (const match of stylesheet.matchAll(/@source\s+'\.\.\/app\/features\/([a-z-]+)'/g)) {
    if (match[1] !== undefined) names.add(match[1]);
  }
  return names;
}

function renders(path: string): boolean {
  if (path.endsWith('.html')) return true;
  return path.endsWith('.ts') && readFileSync(path, 'utf8').includes('template:');
}

function walk(dir: string, found: string[]): string[] {
  for (const entry of readdirSync(dir)) {
    const path = join(dir, entry);
    if (statSync(path).isDirectory()) walk(path, found);
    else if (renders(path)) found.push(path);
  }
  return found;
}

function templateDirectories(): string[] {
  const features = join(web, 'src/app/features');
  return readdirSync(features)
    .filter((entry) => statSync(join(features, entry)).isDirectory())
    .filter((entry) => walk(join(features, entry), []).length > 0)
    .sort();
}

describe('Tailwind content sources', () => {
  it('keeps automatic detection off, so the list is the only content source', () => {
    expect(stylesheet).toContain('source(none)');
  });

  it('registers every feature directory that renders a template', () => {
    const directories = templateDirectories();
    expect(directories.length).toBeGreaterThanOrEqual(5);
    const listed = registered();
    expect(listed.size).toBeGreaterThanOrEqual(5);
    expect(directories.filter((name) => !listed.has(name))).toEqual([]);
  });
});
