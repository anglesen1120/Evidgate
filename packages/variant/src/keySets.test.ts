// Pins the normaliser's key sets to fixtures/variant-hash/key-sets.json, which the server's
// VariantKeySetPinTests asserts for its own sets. The sets are not exported, so their literals are read from source.
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { normalizeVariant } from './normalize.js';

const here = dirname(fileURLToPath(import.meta.url));
const pin = JSON.parse(
  readFileSync(resolve(here, '../../../fixtures/variant-hash/key-sets.json'), 'utf8'),
) as Record<string, string[]>;
const source = readFileSync(resolve(here, 'normalize.ts'), 'utf8');

const ENTRY = /^\s*(?:'([^'\n]+)'|"([^"\n]+)"|([A-Za-z_$][\w$]*))\s*:\s*true\s*,?\s*$/;

/** The keys of `const <name>: Record<string, true> = { ... };`; throws when the literal or any entry is not understood. */
function sourceKeys(constName: string): string[] {
  const start = new RegExp(`const ${constName}\\s*:\\s*Record<string,\\s*true>\\s*=\\s*\\{`).exec(
    source,
  );
  if (start === null) {
    throw new Error(
      `const ${constName}: Record<string, true> = { ... } not found in normalize.ts: update this pin test`,
    );
  }
  const lines = source.slice(start.index + start[0].length).split('\n');
  const keys: string[] = [];
  let closed = false;
  // The first line is the rest of the opening line; the literal ends at a line that starts with `}`.
  for (const line of lines.slice(1)) {
    const text = line.trim();
    if (text.startsWith('}')) {
      closed = true;
      break;
    }
    if (text === '' || text.startsWith('//')) {
      continue;
    }
    const m = ENTRY.exec(line);
    if (m === null) {
      throw new Error(`${constName}: cannot parse entry ${JSON.stringify(line)} in normalize.ts`);
    }
    keys.push(m[1] ?? m[2] ?? m[3] ?? '');
  }
  if (!closed) {
    throw new Error(`closing brace of ${constName} not found in normalize.ts`);
  }
  if (keys.length === 0) {
    throw new Error(`${constName} parsed to an empty set from normalize.ts`);
  }
  return keys;
}

function drift(actual: readonly string[], pinned: readonly string[]): string {
  const missing = pinned.filter((k) => !actual.includes(k)).sort();
  const extra = actual.filter((k) => !pinned.includes(k)).sort();
  return `missing in normalize.ts [${missing.join(', ')}], extra in normalize.ts [${extra.join(', ')}]`;
}

const SETS = [
  ['ENUM_LIKE', 'enum_like'],
  ['NON_IDENTIFYING', 'non_identifying'],
] as const;

describe('variant key sets match the shared pin', () => {
  it.each(SETS)('%s equals %s in key-sets.json', (constName, pinName) => {
    const pinned = pin[pinName] ?? [];
    expect(pinned.length, `${pinName} missing or empty in key-sets.json`).toBeGreaterThan(0);

    const actual = sourceKeys(constName);

    expect(drift(actual, pinned)).toBe('missing in normalize.ts [], extra in normalize.ts []');
    expect(new Set(actual).size, 'duplicate keys').toBe(actual.length);
    expect(actual.length, 'parsed count differs from the fixture count').toBe(pinned.length);
  });
});

describe('variant key sets behave as pinned', () => {
  it.each(pin['enum_like'] ?? [])(
    'enum-like key %s folds an upper-case secret-shaped token to a refusal',
    (key) => {
      const result = normalizeVariant({ [key]: '  GHP_AAAAAAAA  ' });

      expect(result.ok).toBe(false);
      if (!result.ok) {
        expect(result.problems).toEqual([{ path: `/params/${key}`, reason: 'secret_value' }]);
      }
    },
  );

  it.each(pin['non_identifying'] ?? [])(
    'non-identifying key %s leaves the hash unchanged',
    (key) => {
      const base = normalizeVariant({ mode: 'ci' });
      const withKey = normalizeVariant({ mode: 'ci', [key]: 'free text' });

      expect(base.ok && withKey.ok).toBe(true);
      if (base.ok && withKey.ok) {
        expect(withKey.hash).toBe(base.hash);
      }
    },
  );
});
