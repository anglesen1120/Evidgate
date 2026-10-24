// 09.1.1-T11.4 (AC9): the `style-tokens` lint rule, proved through the REAL repository
// ESLint configuration rather than the rule in isolation.
//
// Every case below constructs an `ESLint` instance with `cwd` = repository root, so the
// flat config of `eslint.config.js` decides which rule runs for which file. That is the
// point of the spec: a rule that is correct but unwired (wrong `files` glob, wrong
// `language`, forgotten plugin entry) must fail here.
//
// The rule contract itself is documented in `docs/design/web/tokens.md`
// § "Lint rule `style-tokens`": the two review risks are a rule that is too loose (it
// misses `rgb()`, `hsl()` or a named colour) and one that is too strict (it flags
// `tokens.css` or a valid `var(--eg-*)` reference). Both directions are covered.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

import { ESLint } from 'eslint';
import { describe, expect, it } from 'vitest';

/** One lint message, reduced to what this spec asserts. */
interface Finding {
  ruleId: string | null;
  messageId: string | null;
  line: number;
  column: number;
}

const REPO_ROOT = fileURLToPath(new URL('../../../', import.meta.url));
const CSS_RULE = 'style-tokens/no-raw-literals';
const INLINE_RULE = 'style-tokens/no-raw-literals-inline';
/** A component stylesheet path: it must match the apps/web CSS config block. */
const COMPONENT_STYLE_PATH = 'apps/web/src/app/example.component.css';
/**
 * A component source path: `styles: [...]` lives here, not in a .css file. It is a real
 * file on disk because the TypeScript config block parses these specs with the typed
 * project service, which can only open a file that exists — the linted TEXT is still the
 * synthetic component below.
 */
const COMPONENT_SOURCE_PATH = 'apps/web/src/app/app.ts';

const eslint = new ESLint({ cwd: REPO_ROOT });

/**
 * Lints `code` as if it were the file at `filePath`, through the repository config.
 *
 * @param code source text
 * @param filePath repository-relative path (decides which config block applies)
 * @returns every lint message ESLint produced
 */
async function lint(code: string, filePath: string): Promise<Finding[]> {
  const results = await eslint.lintText(code, { filePath });
  const result = results[0];
  if (!result) {
    throw new Error(`ESLint returned no result for ${filePath}`);
  }
  return result.messages.map((message) => ({
    ruleId: message.ruleId,
    messageId: message.messageId ?? null,
    line: message.line ?? 0,
    column: message.column ?? 0,
  }));
}

/** Lints a component stylesheet. */
async function lintComponentStyle(code: string): Promise<Finding[]> {
  return lint(code, COMPONENT_STYLE_PATH);
}

/** Lints a component source and keeps only the inline-styles rule. */
async function lintInlineStyles(code: string): Promise<Finding[]> {
  const findings = await lint(code, COMPONENT_SOURCE_PATH);
  // A parse or project-service failure arrives as a `ruleId === null` message; filtering it out
  // would let a broken snippet pass vacuously, so fail loudly instead.
  const fatal = findings.filter((finding) => finding.ruleId === null);
  if (fatal.length > 0) {
    throw new Error(`lintInlineStyles: ${JSON.stringify(fatal)}`);
  }
  return findings.filter((finding) => finding.ruleId === INLINE_RULE);
}

function cssFinding(messageId: string, line: number, column: number): Finding {
  return { ruleId: CSS_RULE, messageId, line, column };
}

function inlineFinding(messageId: string, line: number, column: number): Finding {
  return { ruleId: INLINE_RULE, messageId, line, column };
}

/**
 * Wraps `declarations` in a single rule block, one declaration per line, so every expected
 * column in this spec is `property.length + 3` for a value that starts the value text.
 */
function ruleBlock(...declarations: string[]): string {
  return ['.example {', ...declarations, '}', ''].join('\n');
}

/** An `@Component({ styles: [`…`] })` with the stylesheet laid out one line per entry. */
function inlineComponent(...styleLines: string[]): string {
  return [
    "import { Component } from '@angular/core';",
    '',
    '@Component({',
    "  selector: 'app-example',",
    "  template: '',",
    '  styles: [`',
    ...styleLines,
    '`],',
    '})',
    'export class ExampleComponent {}',
    '',
  ].join('\n');
}

describe('style-tokens/no-raw-literals (apps/web/**/*.css)', () => {
  describe('raw colours fail', () => {
    it('fails on a 3 digit hex colour', async () => {
      const findings = await lintComponentStyle(ruleBlock('color: #fff;'));
      expect(findings).toEqual([cssFinding('rawHexColour', 2, 8)]);
    });

    it('fails on a 4 digit hex colour', async () => {
      const findings = await lintComponentStyle(ruleBlock('color: #fff8;'));
      expect(findings).toEqual([cssFinding('rawHexColour', 2, 8)]);
    });

    it('fails on a 6 digit hex colour', async () => {
      const findings = await lintComponentStyle(ruleBlock('background-color: #aabbcc;'));
      expect(findings).toEqual([cssFinding('rawHexColour', 2, 19)]);
    });

    it('fails on an 8 digit hex colour', async () => {
      const findings = await lintComponentStyle(ruleBlock('border-color: #10202b99;'));
      expect(findings).toEqual([cssFinding('rawHexColour', 2, 15)]);
    });

    it('fails on rgb()', async () => {
      const findings = await lintComponentStyle(ruleBlock('color: rgb(0 0 0 / 50%);'));
      expect(findings).toEqual([cssFinding('rawColourFunction', 2, 8)]);
    });

    it('fails on rgba()', async () => {
      const findings = await lintComponentStyle(ruleBlock('background: rgba(0, 0, 0, 0.5);'));
      expect(findings).toEqual([cssFinding('rawColourFunction', 2, 13)]);
    });

    it('fails on hsl()', async () => {
      const findings = await lintComponentStyle(ruleBlock('border-color: hsl(0 0% 0%);'));
      expect(findings).toEqual([cssFinding('rawColourFunction', 2, 15)]);
    });

    it('fails on oklch()', async () => {
      const findings = await lintComponentStyle(ruleBlock('color: oklch(60% 0.1 200);'));
      expect(findings).toEqual([cssFinding('rawColourFunction', 2, 8)]);
    });

    it('fails on both operands of a color-mix() built from raw values', async () => {
      const findings = await lintComponentStyle(
        ruleBlock('background: color-mix(in srgb, #fff 10%, red);'),
      );
      expect(findings).toEqual([
        cssFinding('rawHexColour', 2, 32),
        cssFinding('rawNamedColour', 2, 42),
      ]);
    });

    it('fails on a named colour', async () => {
      const findings = await lintComponentStyle(ruleBlock('color: red;'));
      expect(findings).toEqual([cssFinding('rawNamedColour', 2, 8)]);
    });

    it('fails on a named colour in a custom property', async () => {
      const findings = await lintComponentStyle(ruleBlock('--local-accent: red;'));
      expect(findings).toEqual([cssFinding('rawNamedColour', 2, 17)]);
    });

    it('fails on a hex colour in a custom property', async () => {
      const findings = await lintComponentStyle(ruleBlock('--local-accent: #aabbcc;'));
      expect(findings).toEqual([cssFinding('rawHexColour', 2, 17)]);
    });

    it('fails on a raw hex colour in a var() fallback', async () => {
      const findings = await lintComponentStyle(ruleBlock('color: var(--x, #fff);'));
      expect(findings).toEqual([cssFinding('rawHexColour', 2, 17)]);
    });

    it('fails on a raw colour in a shadow', async () => {
      const findings = await lintComponentStyle(
        ruleBlock('box-shadow: 0 1px 0 rgba(0, 0, 0, 0.2);'),
      );
      // Both halves of the literal are reported: the `1px` offset (an elevation token
      // covers the whole shadow) and the raw colour function that follows it.
      expect(findings).toEqual([
        cssFinding('rawLength', 2, 15),
        cssFinding('rawColourFunction', 2, 21),
      ]);
    });
  });

  describe('raw lengths fail', () => {
    it('fails on a px padding', async () => {
      const findings = await lintComponentStyle(ruleBlock('padding: 16px;'));
      expect(findings).toEqual([cssFinding('rawLength', 2, 10)]);
    });

    it('fails on a rem gap', async () => {
      const findings = await lintComponentStyle(ruleBlock('gap: 0.5rem;'));
      expect(findings).toEqual([cssFinding('rawLength', 2, 6)]);
    });

    it('fails on an em font size', async () => {
      const findings = await lintComponentStyle(ruleBlock('font-size: 1em;'));
      expect(findings).toEqual([cssFinding('rawLength', 2, 12)]);
    });

    it('fails on every length of a two-value margin', async () => {
      const findings = await lintComponentStyle(ruleBlock('margin: 4px 8px;'));
      expect(findings).toEqual([cssFinding('rawLength', 2, 9), cssFinding('rawLength', 2, 13)]);
    });

    it('fails on a border radius', async () => {
      const findings = await lintComponentStyle(ruleBlock('border-radius: 6px;'));
      expect(findings).toEqual([cssFinding('rawLength', 2, 16)]);
    });

    it('fails on a px length inside calc()', async () => {
      const findings = await lintComponentStyle(ruleBlock('padding: calc(100% - 16px);'));
      expect(findings).toEqual([cssFinding('rawLength', 2, 22)]);
    });

    it('fails on a 1px border: --eg-border-width is the token for it', async () => {
      const findings = await lintComponentStyle(ruleBlock('border: 1px solid var(--eg-border);'));
      expect(findings).toEqual([cssFinding('rawLength', 2, 9)]);
    });

    it('fails on a px inset', async () => {
      const findings = await lintComponentStyle(ruleBlock('inset: 0 2px;'));
      expect(findings).toEqual([cssFinding('rawLength', 2, 10)]);
    });
  });

  describe('token references and literal-free values pass', () => {
    it('passes a stylesheet that only references tokens and zero/auto/percentage values', async () => {
      const findings = await lintComponentStyle(
        ruleBlock(
          'color: var(--eg-text);',
          'background: transparent;',
          'border-color: currentcolor;',
          'margin: 0;',
          'margin-left: auto;',
          'border: var(--eg-border-width) solid var(--eg-border);',
          'box-shadow: var(--eg-elevation-1);',
          'font: inherit;',
          'line-height: 1.5;',
          'width: 100%;',
          'padding: var(--eg-space-5);',
          'gap: var(--eg-space-4);',
          'border-radius: var(--eg-radius-md);',
          'background: color-mix(in srgb, var(--eg-text) 10%, transparent);',
          '--local-gap: var(--eg-space-4);',
        ),
      );
      expect(findings).toEqual([]);
    });

    it('does not read a colour or a length out of a string, a url() or a font name', async () => {
      const findings = await lintComponentStyle(
        ruleBlock(
          'content: "#fff";',
          'background-image: url(#eg-icon);',
          "font-family: 'red', 'Liberation Mono', monospace;",
        ),
      );
      expect(findings).toEqual([]);
    });
  });

  describe('@media / @supports / @container preludes are exempt, their declarations are not', () => {
    it('passes a token-only declaration inside a media query with a literal breakpoint', async () => {
      const findings = await lintComponentStyle(
        '@media (min-width: 768px) {\n.example {\npadding: var(--eg-space-4);\n}\n}\n',
      );
      expect(findings).toEqual([]);
    });

    it('fails on a raw length inside a media query with a literal breakpoint', async () => {
      const findings = await lintComponentStyle(
        '@media (min-width: 768px) {\n.example {\npadding: 16px;\n}\n}\n',
      );
      expect(findings).toEqual([cssFinding('rawLength', 3, 10)]);
    });

    it('never checks the @supports prelude declaration but checks the rule body', async () => {
      const findings = await lintComponentStyle(
        '@supports (border-radius: 6px) {\n.example {\ninset: 2px;\n}\n}\n',
      );
      expect(findings).toEqual([cssFinding('rawLength', 3, 8)]);
    });

    it('never checks the @container prelude but checks the rule body', async () => {
      const findings = await lintComponentStyle(
        '@container (min-width: 400px) {\n.example {\ngap: 8px;\n}\n}\n',
      );
      expect(findings).toEqual([cssFinding('rawLength', 3, 6)]);
    });
  });

  describe('tokens.css is the only file allowed raw values', () => {
    const tokensPath = `${REPO_ROOT}apps/web/src/styles/tokens.css`;

    it('produces no finding inside the real tokens.css', async () => {
      const tokensSource = readFileSync(tokensPath, 'utf8');
      // The exemption must not be vacuous: the file really is full of raw values.
      expect(tokensSource).toContain('--eg-accent: #006b66;');
      expect(tokensSource).toContain('--eg-space-5: 16px;');

      const findings = await lint(tokensSource, 'apps/web/src/styles/tokens.css');
      expect(findings.map((finding) => finding.ruleId)).not.toContain(CSS_RULE);
    });

    it('fails on the same declarations outside tokens.css', async () => {
      const findings = await lintComponentStyle(
        ruleBlock('--eg-accent: #006b66;', '--eg-space-5: 16px;'),
      );
      expect(findings).toEqual([cssFinding('rawHexColour', 2, 14)]);
    });

    it('produces no finding in the current app.css and styles.css', async () => {
      const appCss = readFileSync(`${REPO_ROOT}apps/web/src/app/app.css`, 'utf8');
      const stylesCss = readFileSync(`${REPO_ROOT}apps/web/src/styles.css`, 'utf8');

      expect(await lint(appCss, 'apps/web/src/app/app.css')).toEqual([]);
      expect(await lint(stylesCss, 'apps/web/src/styles.css')).toEqual([]);
    });
  });
});

describe('style-tokens/no-raw-literals-inline (inline @Component styles)', () => {
  it('fails on a raw hex colour in a template literal', async () => {
    const findings = await lintInlineStyles(inlineComponent('.example {', 'color: #fff;', '}'));
    expect(findings).toEqual([inlineFinding('rawHexColour', 8, 8)]);
  });

  it('fails on a raw length in a template literal', async () => {
    const findings = await lintInlineStyles(inlineComponent('.example {', 'padding: 16px;', '}'));
    expect(findings).toEqual([inlineFinding('rawLength', 8, 10)]);
  });

  it('fails on a raw named colour in a single-line template entry', async () => {
    const findings = await lintInlineStyles(inlineComponent('.example { color: red; }'));
    expect(findings).toEqual([inlineFinding('rawNamedColour', 7, 19)]);
  });

  it('fails on a raw named colour in a plain string entry', async () => {
    const findings = await lintInlineStyles(
      [
        "import { Component } from '@angular/core';",
        '',
        '@Component({',
        "  selector: 'app-example',",
        "  template: '',",
        "  styles: ['.example { color: red; }'],",
        '})',
        'export class ExampleComponent {}',
        '',
      ].join('\n'),
    );
    expect(findings).toEqual([inlineFinding('rawNamedColour', 6, 31)]);
  });

  it('passes token-only inline styles', async () => {
    const findings = await lintInlineStyles(
      inlineComponent('.example {', 'color: var(--eg-text);', 'padding: var(--eg-space-4);'),
    );
    expect(findings).toEqual([]);
  });

  it('never checks an @media prelude inside inline styles', async () => {
    const findings = await lintInlineStyles(
      inlineComponent(
        '@media (min-width: 768px) {',
        '.example {',
        'gap: var(--eg-space-4);',
        '}',
        '}',
      ),
    );
    expect(findings).toEqual([]);
  });

  it('fails loudly when the snippet does not parse instead of reporting a pass', async () => {
    // Unbalanced source: the typed parser emits a `ruleId === null` fatal message, which must
    // not be filtered away into an empty (passing) finding list.
    await expect(
      lintInlineStyles('@Component({ styles: [ `.example { color: #fff; }` ] '),
    ).rejects.toThrow(/lintInlineStyles/);
  });
});
