// 09.1.1-T6.1 (plans/features/09-web-ui §10 T6.1): the i18n lint contract of apps/web. These
// cases run the repository's own eslint.config.js against probe files, so a rule dropped from
// the config — or a rule option that stops demanding the `@@area.name` custom id — fails here
// instead of shipping an untranslated string into the `vi` bundle.
import assert from 'node:assert/strict';
import { rm, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

import { ESLint } from 'eslint';

// apps/web/scripts/lint-i18n.test.mjs → repository root.
const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const appDir = join(repoRoot, 'apps', 'web', 'src', 'app');

// Probe paths under apps/web/src/app/, so the apps/web blocks of eslint.config.js apply.
const HTML_PROBE = 'apps/web/src/app/__lint_probe__.html';
const TS_PROBE = 'apps/web/src/app/__lint_probe__.ts';
const TS_PROBE_FILE = join(appDir, '__lint_probe__.ts');

const I18N = '@angular-eslint/template/i18n';
const RESTRICTED = 'no-restricted-syntax';

// One instance: the TypeScript project service is expensive to build and is reused per file.
const eslint = new ESLint({ cwd: repoRoot });

/** `[ruleId, message]` pairs of a lint result, so a failure names the rule that actually fired. */
function findings(result) {
  return result.messages.map((message) => [message.ruleId, message.message]);
}

function reported(result, ruleId) {
  const messages = result.messages.filter((message) => message.ruleId === ruleId);
  assert.ok(
    messages.length > 0,
    `expected ${ruleId} to report; findings: ${JSON.stringify(findings(result))}`,
  );
  return messages;
}

function clean(result, ruleId) {
  const messages = result.messages.filter((message) => message.ruleId === ruleId);
  assert.deepEqual(
    messages.map((message) => message.message),
    [],
    `${ruleId} must not report; findings: ${JSON.stringify(findings(result))}`,
  );
}

async function lintTemplate(template) {
  const [result] = await eslint.lintText(template, { filePath: HTML_PROBE });
  return result;
}

async function lintTypeScript(source) {
  // The project service resolves a file through apps/web/tsconfig.json and refuses a virtual
  // path that is not on disk ("was not found by the project service"), so the TypeScript probe
  // is real for the duration of the call and removed again afterwards.
  await writeFile(TS_PROBE_FILE, source, 'utf8');
  try {
    const [result] = await eslint.lintText(source, { filePath: TS_PROBE });
    return result;
  } finally {
    await rm(TS_PROBE_FILE, { force: true });
  }
}

// The app project has `"types": []`, so `$localize` is declared per probe: a module-local
// declaration cannot collide with the global from `@angular/localize/init`.
const TS_PREAMBLE = [
  '// Written by apps/web/scripts/lint-i18n.test.mjs, removed in its finally block.',
  'declare const $localize: {',
  '  (message: string, ...expressions: readonly unknown[]): string;',
  '  (parts: TemplateStringsArray, ...expressions: readonly unknown[]): string;',
  '};',
  '',
].join('\n');

test('template: a bare text node is reported', async () => {
  const result = await lintTemplate('<p>Hello</p>');
  assert.match(reported(result, I18N)[0].message, /i18n attribute/);
});

test('template: a visible attribute without its i18n-<attribute> marker is reported', async () => {
  const result = await lintTemplate('<img alt="Logo" src="x" />');
  assert.match(reported(result, I18N)[0].message, /"alt" has no corresponding i18n attribute/);
});

test('template: an i18n marker without a @@ custom id is reported', async () => {
  const result = await lintTemplate('<p i18n>Hello</p>');
  assert.match(reported(result, I18N)[0].message, /Missing custom ID on element/);
});

test('template: a custom id on the element text passes', async () => {
  const result = await lintTemplate('<p i18n="@@probe.hello">Hello</p>');
  clean(result, I18N);
});

test('template: a custom id on a visible attribute passes', async () => {
  const result = await lintTemplate('<div i18n-title="@@probe.title" title="Hello"></div>');
  clean(result, I18N);
});

test('template: a non-visible attribute (data-testid) needs no marker', async () => {
  const result = await lintTemplate('<p data-testid="probe"></p>');
  clean(result, I18N);
});

// The rule's semantics, asserted rather than assumed: `{{ expr }}` alone compiles to a
// `BoundText` whose literal parts hold no letters, which the shipped `boundTextAllowedPattern`
// allows; as soon as a literal letter joins it, the node is reported like a plain text node.
test('template: an interpolation-only text node passes', async () => {
  const result = await lintTemplate('<p>{{ probe }}</p>');
  clean(result, I18N);
});

test('template: a bound text with literal letters is reported', async () => {
  const result = await lintTemplate('<p>Hello {{ probe }}</p>');
  reported(result, I18N);
});

// D3: a visible string hidden inside the interpolation is invisible to the i18n rule (its
// literal parts are empty), so the config's own restricted-syntax selectors own that case.
test('template: a literal string in a text interpolation is reported', async () => {
  const result = await lintTemplate("<p>{{ 'literal' }}</p>");
  const [message] = reported(result, RESTRICTED);
  assert.match(message.message, /i18n="@@area\.name"/);
  assert.match(message.message, /09\.1\.1-T6\.1/);
});

test('template: a plain interpolation passes the literal-text rule', async () => {
  const result = await lintTemplate('<p>{{ name }}</p>');
  clean(result, RESTRICTED);
});

test('template: a piped interpolation passes the literal-text rule', async () => {
  const result = await lintTemplate('<p>{{ count | number }}</p>');
  clean(result, RESTRICTED);
});

// Pipe and call arguments are not rendered text of their own, and the selectors must not
// over-match them (a `{{ x | date: 'short' }}` pattern is not a translatable sentence).
test('template: literal pipe and call arguments are not reported', async () => {
  const result = await lintTemplate("<p>{{ x | slice: 'a' }} {{ fn('b') }}</p>");
  clean(result, RESTRICTED);
});

// Implemented as reported, both branches: either branch can render visible English.
test('template: a conditional whose branches are literals is reported', async () => {
  const result = await lintTemplate("<p>{{ cond ? 'a' : 'b' }}</p>");
  assert.equal(reported(result, RESTRICTED).length, 2);
});

// Attribute interpolations and control-flow conditions are outside the rule's scope on purpose.
test('template: literals outside text interpolation are not reported', async () => {
  const result = await lintTemplate(
    `<div title="{{ 'attr' }}"></div>@if ('cond') { <p>Hello</p> }`,
  );
  clean(result, RESTRICTED);
});

test('typescript: the $localize call form is reported', async () => {
  const result = await lintTypeScript(
    `${TS_PREAMBLE}export const bare = $localize('bare call');\n`,
  );
  const [message] = reported(result, RESTRICTED);
  assert.match(message.message, /@@area\.name/);
  assert.match(message.message, /09\.1\.1-T6\.1/);
});

test('typescript: a $localize message without a custom id is reported', async () => {
  const result = await lintTypeScript(
    `${TS_PREAMBLE}export const plain = $localize\`plain text\`;\n`,
  );
  const [message] = reported(result, RESTRICTED);
  assert.match(message.message, /custom id/);
  assert.match(message.message, /09\.1\.1-T6\.1/);
});

test('typescript: a $localize message with @@area.name passes', async () => {
  const result = await lintTypeScript(
    `${TS_PREAMBLE}export const hello = $localize\`:@@probe.hello:Hello\`;\n`,
  );
  clean(result, RESTRICTED);
});

test('typescript: a $localize message with meaning|description@@area.name passes', async () => {
  const result = await lintTypeScript(
    TS_PREAMBLE +
      'export const titled = $localize`:greeting|Shown on the home page@@app.title:Evidgate`;\n',
  );
  clean(result, RESTRICTED);
});

// Only the first template element carries the metadata, so the text after `${…}` must stay
// untested — otherwise every interpolated message would be reported on its tail.
test('typescript: the template element after an interpolation is not tested', async () => {
  const result = await lintTypeScript(
    TS_PREAMBLE +
      'declare const name: string;\n' +
      'export const mixed = $localize`:@@probe.mixed:Hello ${name} and welcome`;\n',
  );
  clean(result, RESTRICTED);
});

// F4(a): the metadata regex requires at least one id character, so an empty `@@:` id — which
// `ng extract-i18n` would key on `` and no translator could address — is reported.
test('typescript: an empty custom id is reported', async () => {
  const result = await lintTypeScript(`${TS_PREAMBLE}export const empty = $localize\`:@@:Hi\`;\n`);
  const [message] = reported(result, RESTRICTED);
  assert.match(message.message, /custom id/);
  assert.match(message.message, /09\.1\.1-T6\.1/);
});

// F4(b): `$localize` reached through a global object is a MemberExpression tag/callee, which a
// `tag.name` / `callee.name` selector never sees.
test('typescript: the member-expression call form is reported', async () => {
  const result = await lintTypeScript(
    `${TS_PREAMBLE}export const viaWindow = window.$localize('bare member call');\n`,
  );
  const [message] = reported(result, RESTRICTED);
  assert.match(message.message, /@@area\.name/);
});

test('typescript: the member-expression tagged form without an id is reported', async () => {
  const result = await lintTypeScript(
    `${TS_PREAMBLE}export const viaGlobal = globalThis.$localize\`plain member text\`;\n`,
  );
  const [message] = reported(result, RESTRICTED);
  assert.match(message.message, /custom id/);
});

test('typescript: the member-expression tagged form with an empty id is reported', async () => {
  const result = await lintTypeScript(
    `${TS_PREAMBLE}export const viaSelf = self.$localize\`:@@:empty id\`;\n`,
  );
  reported(result, RESTRICTED);
});

test('typescript: the member-expression tagged form with @@area.name passes', async () => {
  const result = await lintTypeScript(
    `${TS_PREAMBLE}export const viaGlobal = globalThis.$localize\`:@@probe.member:Hello\`;\n`,
  );
  clean(result, RESTRICTED);
});
