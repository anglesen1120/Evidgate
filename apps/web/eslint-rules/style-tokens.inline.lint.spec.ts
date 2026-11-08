// The inline half of the `style-tokens` rule: raw colours, lengths and durations inside
// `@Component({ styles: [...] })` fail, and an interpolated unit is reported only where a
// static literal would be. It runs through the real repository config, so a rule that is
// correct but unwired fails here (see `style-tokens.test-support.ts`).
import { beforeAll, describe, expect, it } from 'vitest';

import {
  inlineComponent,
  inlineFinding,
  lintInlineStyles,
  lintInlineStylesDetailed,
  warmEslint,
} from './style-tokens.test-support';

beforeAll(warmEslint);

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
