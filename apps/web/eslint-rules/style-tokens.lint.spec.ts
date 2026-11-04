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

/**
 * A finding together with its rendered message, for the cases whose point is the reported
 * value (`message` data) rather than only the location.
 */
interface DetailedFinding extends Finding {
  message: string;
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
 * Lints `code` as if it were the file at `filePath`, through the repository config, and keeps
 * both the location and the rendered message.
 *
 * @param code source text
 * @param filePath repository-relative path (decides which config block applies)
 * @returns every lint message ESLint produced, with its text
 */
async function lintDetailed(code: string, filePath: string): Promise<DetailedFinding[]> {
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
    message: message.message,
  }));
}

/**
 * Lints `code` as if it were the file at `filePath`, through the repository config.
 *
 * @param code source text
 * @param filePath repository-relative path (decides which config block applies)
 * @returns every lint message ESLint produced
 */
async function lint(code: string, filePath: string): Promise<Finding[]> {
  const findings = await lintDetailed(code, filePath);
  return findings.map(({ ruleId, messageId, line, column }) => ({
    ruleId,
    messageId,
    line,
    column,
  }));
}

/** Lints a component stylesheet. */
async function lintComponentStyle(code: string): Promise<Finding[]> {
  return lint(code, COMPONENT_STYLE_PATH);
}

/** Lints a component source and keeps only the inline-styles rule messages, with their text. */
async function lintInlineStylesDetailed(code: string): Promise<DetailedFinding[]> {
  const findings = await lintDetailed(code, COMPONENT_SOURCE_PATH);
  const fatal = findings.filter((finding) => finding.ruleId === null);
  if (fatal.length > 0) {
    throw new Error(`lintInlineStylesDetailed: ${JSON.stringify(fatal)}`);
  }
  return findings.filter((finding) => finding.ruleId === INLINE_RULE);
}

/** Lints a component source and keeps only the inline-styles rule. */
async function lintInlineStyles(code: string): Promise<Finding[]> {
  // A parse or project-service failure arrives as a `ruleId === null` message; filtering it out
  // would let a broken snippet pass vacuously, so fail loudly instead.
  const findings = await lintInlineStylesDetailed(code);
  return findings.map(({ ruleId, messageId, line, column }) => ({
    ruleId,
    messageId,
    line,
    column,
  }));
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

  describe('offsets are UTF-16 code units, so a non-BMP character does not shift them', () => {
    it('reports a hex colour that follows an emoji in a comment', async () => {
      // Regression (maskNonCode): splitting the value with `[...text]` counts an emoji as one
      // element while every offset is a code unit, so the masking shifted this literal one
      // character left and reported the wrong column and value. The comment sits between two
      // value tokens, so it is part of the value text the detector sees.
      const findings = await lintDetailed(
        ruleBlock('background: var(--x) /* 😀 */ #fff;'),
        COMPONENT_STYLE_PATH,
      );
      expect(findings).toHaveLength(1);
      expect(findings[0]).toMatchObject(cssFinding('rawHexColour', 2, 31));
      expect(findings[0]?.message).toContain("'#fff'");
    });

    it('reports a hex colour that follows an emoji in a quoted string', async () => {
      const findings = await lintDetailed(
        ruleBlock('--local-label: "😀" #aabbcc;'),
        COMPONENT_STYLE_PATH,
      );
      expect(findings).toHaveLength(1);
      expect(findings[0]).toMatchObject(cssFinding('rawHexColour', 2, 21));
      expect(findings[0]?.message).toContain("'#aabbcc'");
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

    it('fails on a custom-property length in a plain custom property', async () => {
      const findings = await lintComponentStyle(ruleBlock('--local-gap: 8px;'));
      expect(findings).toEqual([cssFinding('rawLength', 2, 14)]);
    });

    it('fails on a rem length in a custom property', async () => {
      const findings = await lintComponentStyle(ruleBlock('--local-inset: 0.5rem;'));
      expect(findings).toEqual([cssFinding('rawLength', 2, 16)]);
    });

    it('fails on a scroll-margin', async () => {
      const findings = await lintComponentStyle(ruleBlock('scroll-margin: 8px;'));
      expect(findings).toEqual([cssFinding('rawLength', 2, 16)]);
    });

    it('fails on a logical scroll-padding longhand', async () => {
      const findings = await lintComponentStyle(ruleBlock('scroll-padding-block-start: 1.5rem;'));
      expect(findings).toEqual([cssFinding('rawLength', 2, 29)]);
    });

    it('fails on an outline-offset', async () => {
      const findings = await lintComponentStyle(ruleBlock('outline-offset: 2px;'));
      expect(findings).toEqual([cssFinding('rawLength', 2, 17)]);
    });
  });

  describe('raw motion durations fail', () => {
    it('fails on a ms duration in a transition shorthand', async () => {
      const findings = await lintComponentStyle(ruleBlock('transition: opacity 200ms ease;'));
      expect(findings).toEqual([cssFinding('rawDuration', 2, 21)]);
    });

    it('fails on a decimal-second duration in animation-duration', async () => {
      const findings = await lintComponentStyle(ruleBlock('animation-duration: .3s;'));
      expect(findings).toEqual([cssFinding('rawDuration', 2, 21)]);
    });

    it('fails on a duration in a transition-delay', async () => {
      const findings = await lintComponentStyle(ruleBlock('transition-delay: 100ms;'));
      expect(findings).toEqual([cssFinding('rawDuration', 2, 19)]);
    });

    it('fails on every duration of an animation shorthand', async () => {
      const findings = await lintComponentStyle(ruleBlock('animation: fade 1.5s ease-in 200ms;'));
      expect(findings).toEqual([
        cssFinding('rawDuration', 2, 17),
        cssFinding('rawDuration', 2, 30),
      ]);
    });

    it('fails on a duration in a custom property', async () => {
      const findings = await lintComponentStyle(ruleBlock('--local-fade: 120ms;'));
      expect(findings).toEqual([cssFinding('rawDuration', 2, 15)]);
    });

    it('fails on a duration in a var() fallback', async () => {
      // A fallback is ordinary value text, exactly as it is for a colour.
      const findings = await lintComponentStyle(
        ruleBlock('transition: opacity var(--local-fade, 200ms) var(--eg-ease);'),
      );
      expect(findings).toEqual([cssFinding('rawDuration', 2, 39)]);
    });

    it('reports the raw value in the message', async () => {
      const findings = await lintDetailed(
        ruleBlock('transition: opacity 200ms ease;'),
        COMPONENT_STYLE_PATH,
      );
      expect(findings).toHaveLength(1);
      expect(findings[0]?.message).toContain("'200ms'");
      expect(findings[0]?.message).toContain('var(--eg-duration-fast)');
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
          '--local-zero: 0;',
          '--local-ratio: 1.5;',
          '--local-share: 100%;',
          '--local-calc: calc(var(--eg-space-2) * 2);',
          'scroll-margin: var(--eg-space-2);',
          'outline-offset: var(--eg-focus-offset);',
          'transition: opacity var(--eg-duration-fast) var(--eg-ease);',
          'transition-duration: 0s;',
          'animation-delay: 0ms;',
          '--local-fade: var(--eg-duration-standard);',
        ),
      );
      expect(findings).toEqual([]);
    });

    it('passes CSS system colour keywords, the forced-colors way of styling', async () => {
      // A system colour is not a brand literal: it resolves to the user's OS colour and is the
      // recommended way to keep a control visible under `forced-colors: active` (WCAG 2.2
      // SC 1.4.3/1.4.11). It carries no palette value, so it is not reported.
      const findings = await lintComponentStyle(
        ruleBlock(
          'color: CanvasText;',
          'background-color: Canvas;',
          'border-color: ButtonText;',
          'outline-color: LinkText;',
        ),
      );
      expect(findings).toEqual([]);
    });

    it('does not check sizing, transform, tracking or table spacing properties', async () => {
      // Documented exclusions (tokens.md, "Deliberately not checked"): there is no numeric token
      // to reference, so a literal there is not a token violation.
      const findings = await lintComponentStyle(
        ruleBlock(
          'width: 240px;',
          'height: 48px;',
          'min-width: 360px;',
          'max-width: 1280px;',
          'transform: translateY(-2px);',
          'letter-spacing: 0.5px;',
          'word-spacing: 1px;',
          'text-indent: 2em;',
          'border-spacing: 4px;',
          'outline-offset: var(--eg-focus-offset);',
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

    it('passes a reduced-motion block that disables the transition', async () => {
      // The media query, not the rule, is where the duration decision lives: `none` inside
      // the block carries no time, so nothing is reported.
      const findings = await lintComponentStyle(
        '@media (prefers-reduced-motion: reduce) {\n.example {\ntransition: none;\n}\n}\n',
      );
      expect(findings).toEqual([]);
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
      // Both halves of the exemption are proved: the colour AND the length that tokens.css is
      // allowed to declare are raw literals everywhere else.
      expect(findings).toEqual([cssFinding('rawHexColour', 2, 14), cssFinding('rawLength', 3, 15)]);
    });

    it('produces no finding in the current styles.css', async () => {
      const stylesCss = readFileSync(`${REPO_ROOT}apps/web/src/styles.css`, 'utf8');

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

  it('fails on a raw duration in a transition of an inline style', async () => {
    const findings = await lintInlineStyles(
      inlineComponent('.example {', 'transition: opacity 200ms ease;', '}'),
    );
    expect(findings).toEqual([inlineFinding('rawDuration', 8, 21)]);
  });

  it('fails on a raw decimal-second duration in an inline style', async () => {
    const findings = await lintInlineStyles(
      inlineComponent('.example {', 'animation-duration: .3s;', '}'),
    );
    expect(findings).toEqual([inlineFinding('rawDuration', 8, 21)]);
  });

  it('passes token references and zero durations in inline styles', async () => {
    const findings = await lintInlineStyles(
      inlineComponent(
        '.example {',
        'transition: opacity var(--eg-duration-fast) var(--eg-ease);',
        'transition-duration: 0s;',
        '}',
      ),
    );
    expect(findings).toEqual([]);
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

  it('passes a reduced-motion block inside inline styles', async () => {
    const findings = await lintInlineStyles(
      inlineComponent(
        '@media (prefers-reduced-motion: reduce) {',
        '.example { transition: none; }',
        '}',
      ),
    );
    expect(findings).toEqual([]);
  });

  it('fails on a raw length in a custom property of an inline style', async () => {
    const findings = await lintInlineStyles(
      inlineComponent('.example {', '--local-gap: 8px;', 'padding: var(--local-gap);', '}'),
    );
    expect(findings).toEqual([inlineFinding('rawLength', 8, 14)]);
  });

  it('fails on a raw duration in a custom property of an inline style', async () => {
    const findings = await lintInlineStyles(
      inlineComponent(
        '.example {',
        '--local-fade: 120ms;',
        'transition: opacity var(--local-fade);',
        '}',
      ),
    );
    expect(findings).toEqual([inlineFinding('rawDuration', 8, 15)]);
  });

  it('fails on an interpolation immediately followed by a raw unit', async () => {
    const findings = await lintInlineStylesDetailed(
      [
        "import { Component } from '@angular/core';",
        '',
        'const space = 8;',
        '',
        '@Component({',
        "  selector: 'app-example',",
        "  template: '',",
        '  styles: [',
        '    `.example { padding: ${space}px; }`',
        '  ],',
        '})',
        'export class ExampleComponent {}',
        '',
      ].join('\n'),
    );
    expect(findings).toHaveLength(1);
    expect(findings[0]).toMatchObject(inlineFinding('rawLength', 9, 34));
    expect(findings[0]?.message).toContain("'${space}px'");
  });

  it('fails on an interpolation followed by a unit in a custom property', async () => {
    const findings = await lintInlineStyles(
      [
        "import { Component } from '@angular/core';",
        '',
        'const gap = 1.5;',
        '',
        '@Component({',
        "  selector: 'app-example',",
        "  template: '',",
        '  styles: [',
        '    `.example { --local-gap: ${gap}rem; }`',
        '  ],',
        '})',
        'export class ExampleComponent {}',
        '',
      ].join('\n'),
    );
    expect(findings).toEqual([inlineFinding('rawLength', 9, 36)]);
  });

  it('fails on an interpolation immediately followed by a time unit', async () => {
    const findings = await lintInlineStylesDetailed(
      [
        "import { Component } from '@angular/core';",
        '',
        'const duration = 200;',
        '',
        '@Component({',
        "  selector: 'app-example',",
        "  template: '',",
        '  styles: [',
        '    `.example { transition-duration: ${duration}ms; }`',
        '  ],',
        '})',
        'export class ExampleComponent {}',
        '',
      ].join('\n'),
    );
    expect(findings).toHaveLength(1);
    expect(findings[0]).toMatchObject(inlineFinding('rawDuration', 9, 49));
    expect(findings[0]?.message).toContain("'${duration}ms'");
  });

  it('passes an interpolation followed by a percentage or used through var()', async () => {
    const findings = await lintInlineStyles(
      [
        "import { Component } from '@angular/core';",
        '',
        'const share = 50;',
        'const gap = "var(--eg-space-4)";',
        '',
        '@Component({',
        "  selector: 'app-example',",
        "  template: '',",
        '  styles: [',
        '    `.example { padding: ${share}%; gap: ${gap}; }`',
        '  ],',
        '})',
        'export class ExampleComponent {}',
        '',
      ].join('\n'),
    );
    expect(findings).toEqual([]);
  });

  it('never scans interpolation source as stylesheet text', async () => {
    // The expression is JavaScript: `"#fff"` there is not a declaration value, so it is masked
    // before the entry is read as a stylesheet.
    const findings = await lintInlineStyles(
      [
        "import { Component } from '@angular/core';",
        '',
        '@Component({',
        "  selector: 'app-example',",
        "  template: '',",
        '  styles: [',
        '    `.example { color: ${"#fff"}; }`',
        '  ],',
        '})',
        'export class ExampleComponent {}',
        '',
      ].join('\n'),
    );
    expect(findings).toEqual([]);
  });

  it('does not report an interpolated unit where a static length would not be checked', async () => {
    // `width` and `transform` are deliberately outside the length contract (see tokens.md), so
    // `width: ${n}px` is not a raw length there — the same reason `width: 8px` passes.
    const findings = await lintInlineStyles(
      [
        "import { Component } from '@angular/core';",
        '',
        'const size = 240;',
        'const offset = -2;',
        '',
        '@Component({',
        "  selector: 'app-example',",
        "  template: '',",
        '  styles: [',
        '    `.example { width: ${size}px; transform: translateY(${offset}px); }`',
        '  ],',
        '})',
        'export class ExampleComponent {}',
        '',
      ].join('\n'),
    );
    expect(findings).toEqual([]);
  });

  it('checks a stylesheet held in a module-level const referenced by styles', async () => {
    const findings = await lintInlineStyles(
      [
        "import { Component } from '@angular/core';",
        '',
        "const EXAMPLE_STYLES = '.example { padding: 16px; }';",
        '',
        '@Component({',
        "  selector: 'app-example',",
        "  template: '',",
        '  styles: [EXAMPLE_STYLES],',
        '})',
        'export class ExampleComponent {}',
        '',
      ].join('\n'),
    );
    expect(findings).toEqual([inlineFinding('rawLength', 3, 45)]);
  });

  it('passes a token-only stylesheet held in a module-level const', async () => {
    const findings = await lintInlineStyles(
      [
        "import { Component } from '@angular/core';",
        '',
        'const EXAMPLE_STYLES = `',
        '.example { padding: var(--eg-space-4); }',
        '`;',
        '',
        '@Component({',
        "  selector: 'app-example',",
        "  template: '',",
        '  styles: [EXAMPLE_STYLES],',
        '})',
        'export class ExampleComponent {}',
        '',
      ].join('\n'),
    );
    expect(findings).toEqual([]);
  });

  it('does not resolve a styles reference that is not a module-level const', async () => {
    // A function call, an import or a `let` is a documented limitation: the rule must stay
    // silent rather than invent a result for a value it cannot see.
    const findings = await lintInlineStyles(
      [
        "import { Component } from '@angular/core';",
        '',
        'let sharedStyles = ".example { color: red; }";',
        '',
        '@Component({',
        "  selector: 'app-example',",
        "  template: '',",
        '  styles: [sharedStyles],',
        '})',
        'export class ExampleComponent {}',
        '',
      ].join('\n'),
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
