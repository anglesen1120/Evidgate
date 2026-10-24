// 09.1.1-T7 (AC6/AC8 vi + en): every message id the shared UI declares must exist in BOTH
// catalogs. `ng extract-i18n` only sees components reachable from `main.ts`; until the shell
// (09.1.1-T4) or a screen mounts these components, a plain re-extraction would silently
// drop their units. This spec fails in that case instead of shipping English in `vi`.
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const localeDir = resolve(here, '../../../locale');

/** Every non-spec source file under `dir`, recursively. */
function sources(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) return sources(path);
    return /\.(ts|html)$/.test(name) && !name.endsWith('.spec.ts') ? [path] : [];
  });
}

/** `@@id` markers in templates and `:@@id:` markers in `$localize` calls. */
function declaredIds(): string[] {
  const ids = new Set<string>();
  for (const file of sources(here)) {
    for (const match of readFileSync(file, 'utf8').matchAll(/@@([a-z][a-z0-9.-]*[a-z0-9])/g)) {
      const id = match[1];
      if (id !== undefined) ids.add(id);
    }
  }
  return [...ids].sort();
}

function catalogIds(file: string): Set<string> {
  const xml = readFileSync(join(localeDir, file), 'utf8');
  return new Set([...xml.matchAll(/<trans-unit id="([^"]+)"/g)].map((match) => match[1] ?? ''));
}

describe('shared UI message catalogs', () => {
  const ids = declaredIds();

  it('finds the declared ids (the scan itself is not vacuous)', () => {
    expect(ids.length).toBeGreaterThan(40);
    expect(ids.every((id) => id.startsWith('shared.'))).toBe(true);
  });

  it.each(['messages.xlf', 'messages.vi.xlf'])('%s holds every declared id', (file) => {
    const present = catalogIds(file);
    expect(ids.filter((id) => !present.has(id))).toEqual([]);
  });

  it('holds no shared.* unit that no source declares (stale entries)', () => {
    const declared = new Set(ids);
    const stale = [...catalogIds('messages.xlf')].filter(
      (id) => id.startsWith('shared.') && !declared.has(id),
    );
    expect(stale).toEqual([]);
  });
});
