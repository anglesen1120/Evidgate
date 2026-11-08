// The harness the `style-tokens` rule specs share: one `ESLint` instance with `cwd` =
// repository root, so the flat config of `eslint.config.js` decides which rule runs for
// which file. That is the point of the specs: a rule that is correct but unwired (wrong
// `files` glob, wrong `language`, forgotten plugin entry) must fail there.
//
// The rule contract itself is documented in `docs/design/web/tokens.md`
// § "Lint rule `style-tokens`": the two review risks are a rule that is too loose (it
// misses `rgb()`, `hsl()` or a named colour) and one that is too strict (it flags
// `tokens.css` or a valid `var(--eg-*)` reference). Both directions are covered.

import { fileURLToPath } from 'node:url';

import { ESLint } from 'eslint';

/** One lint message, reduced to what the rule specs assert. */
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

export const REPO_ROOT = fileURLToPath(new URL('../../../', import.meta.url));
export const CSS_RULE = 'style-tokens/no-raw-literals';
const INLINE_RULE = 'style-tokens/no-raw-literals-inline';
/** A component stylesheet path: it must match the apps/web CSS config block. */
export const COMPONENT_STYLE_PATH = 'apps/web/src/app/example.component.css';
/**
 * A component source path: `styles: [...]` lives here, not in a .css file. It is a real
 * file on disk because the TypeScript config block parses these specs with the typed
 * project service, which can only open a file that exists — the linted TEXT is still the
 * synthetic component below.
 */
const COMPONENT_SOURCE_PATH = 'apps/web/src/app/app.ts';

const eslint = new ESLint({ cwd: REPO_ROOT });

/**
 * Pays the cold start once per spec file. The first lintText per config block builds the
 * flat config, the rule bodies and the typed program for a component source (seconds under
 * load), so each spec file registers this in its own `beforeAll`, whose timeout pays for it,
 * and no single case is charged for a warm-up. The two calls stay sequential: they warm two
 * different config blocks, and the typed program is the slow half.
 */
export async function warmEslint(): Promise<void> {
  await eslint.lintText('.warm {\n  color: #fff;\n}\n', { filePath: COMPONENT_STYLE_PATH });
  await eslint.lintText(
    "import { Component } from '@angular/core';\n@Component({ selector: 'a', template: '', styles: [`:host { color: #fff; }`] })\nexport class A {}\n",
    { filePath: COMPONENT_SOURCE_PATH },
  );
}

/**
 * Lints `code` as if it were the file at `filePath`, through the repository config, and keeps
 * both the location and the rendered message.
 *
 * @param code source text
 * @param filePath repository-relative path (decides which config block applies)
 * @returns every lint message ESLint produced, with its text
 */
export async function lintDetailed(code: string, filePath: string): Promise<DetailedFinding[]> {
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
export async function lint(code: string, filePath: string): Promise<Finding[]> {
  const findings = await lintDetailed(code, filePath);
  return findings.map(({ ruleId, messageId, line, column }) => ({
    ruleId,
    messageId,
    line,
    column,
  }));
}

/** Lints a component stylesheet. */
export async function lintComponentStyle(code: string): Promise<Finding[]> {
  return lint(code, COMPONENT_STYLE_PATH);
}

/** Lints a component source and keeps only the inline-styles rule messages, with their text. */
export async function lintInlineStylesDetailed(code: string): Promise<DetailedFinding[]> {
  const findings = await lintDetailed(code, COMPONENT_SOURCE_PATH);
  const fatal = findings.filter((finding) => finding.ruleId === null);
  if (fatal.length > 0) {
    throw new Error(`lintInlineStylesDetailed: ${JSON.stringify(fatal)}`);
  }
  return findings.filter((finding) => finding.ruleId === INLINE_RULE);
}

/** Lints a component source and keeps only the inline-styles rule. */
export async function lintInlineStyles(code: string): Promise<Finding[]> {
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

/** The expected finding of the stylesheet half of the rule. */
export function cssFinding(messageId: string, line: number, column: number): Finding {
  return { ruleId: CSS_RULE, messageId, line, column };
}

/** The expected finding of the inline-styles half of the rule. */
export function inlineFinding(messageId: string, line: number, column: number): Finding {
  return { ruleId: INLINE_RULE, messageId, line, column };
}

/**
 * Wraps `declarations` in a single rule block, one declaration per line, so every expected
 * column in these specs is `property.length + 3` for a value that starts the value text.
 */
export function ruleBlock(...declarations: string[]): string {
  return ['.example {', ...declarations, '}', ''].join('\n');
}

/** An `@Component({ styles: [`…`] })` with the stylesheet laid out one line per entry. */
export function inlineComponent(...styleLines: string[]): string {
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
