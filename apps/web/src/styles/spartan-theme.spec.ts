import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import postcss from 'postcss';
import { describe, expect, it } from 'vitest';

function source(name: string): string {
  const paths = [resolve('src/styles', name), resolve('apps/web/src/styles', name)];
  const path = paths.find((candidate) => existsSync(candidate));
  if (!path) throw new Error(`Cannot locate theme source: ${name}`);
  return readFileSync(path, 'utf8');
}

function declarations(css: string, selector: string): Map<string, string> {
  const values = new Map<string, string>();
  postcss.parse(css).walkRules((rule) => {
    if (rule.selectors.includes(selector)) {
      rule.walkDecls((declaration) => {
        values.set(declaration.prop, declaration.value);
      });
    }
  });
  return values;
}

function invalidAliases(aliases: Map<string, string>, tokens: Map<string, string>): string[] {
  return [...aliases]
    .filter(([, value]) => {
      const name = /^var\((--eg-[a-z0-9-]+)\)$/.exec(value)?.[1];
      return name === undefined || !tokens.has(name);
    })
    .map(([name]) => name);
}

const theme = source('spartan-theme.css');
const tokens = source('tokens.css');
const rootTokens = declarations(tokens, ':root');

describe('Spartan theme contract', () => {
  for (const selector of [':root', "html[data-theme='light']", "html[data-theme='dark']"]) {
    it(`${selector} maps every semantic role to an existing Evidgate token`, () => {
      const aliases = declarations(theme, selector);
      const resolvedTokens = new Map([...rootTokens, ...declarations(tokens, selector)]);
      expect(aliases.size).toBe(23);
      expect(invalidAliases(aliases, resolvedTokens)).toEqual([]);
      expect(aliases.get('--background')).toBe('var(--eg-bg)');
      expect(aliases.get('--primary')).toBe('var(--eg-accent)');
      expect(aliases.get('--input')).toBe('var(--eg-border-control)');
      expect(aliases.get('--ring')).toBe('var(--eg-focus)');
      expect(aliases.get('--radius')).toBe('var(--eg-radius-md)');
    });
  }

  it('uses the same aliases while the existing dark tokens supply different values', () => {
    const light = declarations(theme, "html[data-theme='light']");
    const dark = declarations(theme, "html[data-theme='dark']");
    expect(dark).toEqual(light);
    const explicitDark = declarations(tokens, "html[data-theme='dark']");
    const systemDark = declarations(tokens, ":root:not([data-theme='light'])");
    expect(explicitDark.get('--eg-bg')).not.toBe(rootTokens.get('--eg-bg'));
    expect(systemDark.get('--eg-bg')).toBe(explicitDark.get('--eg-bg'));
    expect(theme).toContain('@media (prefers-color-scheme: dark)');
    expect(theme).toContain("html:not([data-theme='light'])");
  });

  it('exposes every semantic color to Tailwind inline without duplicating its value', () => {
    const exposed = new Map<string, string>();
    postcss.parse(theme).walkAtRules('theme', (rule) => {
      expect(rule.params).toBe('inline');
      rule.walkDecls((declaration) => {
        exposed.set(declaration.prop, declaration.value);
      });
    });
    const aliases = declarations(theme, ':root');
    for (const name of aliases.keys()) {
      if (['--radius', '--font-sans', '--font-mono'].includes(name)) continue;
      expect(exposed.get(`--color-${name.slice(2)}`)).toBe(`var(${name})`);
    }
  });

  it('rejects a raw value, a non-Evidgate reference and a missing token', () => {
    expect(invalidAliases(new Map([['--background', 'var(--eg-bg)']]), rootTokens)).toEqual([]);
    for (const value of ['red', 'var(--background)', 'var(--eg-not-defined)']) {
      expect(invalidAliases(new Map([['--background', value]]), rootTokens)).toEqual([
        '--background',
      ]);
    }
  });
});
