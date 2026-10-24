// 09.1.1-T3.1 (plan §2 AC3, §8 T47, FR-09-10): the repository-specific rules of
// `apps/web`, exported as ready-to-spread ESLint flat-config blocks. eslint.config.js only
// requires and spreads this array, so the rule logic lives here and a later change to the
// root config has one small additive block to merge. The HTTP boundary is split: its
// import half is a local rule (no `no-restricted-imports` option can express an allow-list,
// and none has a notion of a type-only import) covering both the `@angular/common/http`
// names and the `@evidgate/sdk` values a file outside core may use; its
// `fetch`/XHR/WebSocket/EventSource half is ESLint's own `no-restricted-globals`.
//
// The blocks need no type information: they run next to the typed blocks of
// eslint.config.js (which own the parser and the project service) and can be given to a
// bare `Linter` with the typescript-eslint parser, which is how
// `packages/cli/src/webLintBoundaries.test.ts` proves AC3 "a violating file fails lint".
//
// These `.cjs` files are CommonJS lint tooling, not application code: no `files` glob of
// eslint.config.js matches `.cjs`, so ESLint itself does not lint them (prettier does).
'use strict';

const noBypassSecurityTrust = require('./rules/no-bypass-security-trust.cjs');
const noFeatureCrossImport = require('./rules/no-feature-cross-import.cjs');
const noHandWrittenWireDto = require('./rules/no-hand-written-wire-dto.cjs');
const noHttpOutsideCore = require('./rules/no-http-outside-core.cjs');

const plugin = {
  meta: { name: '@evidgate/web-lint' },
  rules: {
    'no-bypass-security-trust': noBypassSecurityTrust,
    'no-feature-cross-import': noFeatureCrossImport,
    'no-hand-written-wire-dto': noHandWrittenWireDto,
    'no-http-outside-core': noHttpOutsideCore,
  },
};

/** The one scope every block shares: the Angular application, specs included. */
const APPLICATION_FILES = ['apps/web/src/**/*.ts'];

/** Directories that own HTTP; every other file must go through them. */
const HTTP_OWNERS = [
  'apps/web/src/app/core/data/**/*.ts',
  'apps/web/src/app/core/http/**/*.ts',
  // 09.1.1-T12.1: the dev-only fixture backend provides `HttpBackend` itself (see the rule's
  // `EXEMPT_DIRECTORIES`), so the block must not apply to it either. Nothing here is built into a
  // production bundle: `angular.json`'s `fixtures` configuration is the only place it is imported.
  'apps/web/src/app/core/dev-fixtures/**/*.ts',
];

/** Added by ESLint's own globals rule after "Unexpected use of '<name>'." */
const HTTP_GLOBAL_MESSAGE = 'Call a data service (or the ApiGateway) instead: core owns HTTP.';

module.exports = [
  {
    name: 'evidgate/web-http-outside-core',
    files: APPLICATION_FILES,
    ignores: HTTP_OWNERS,
    plugins: { evidgate: plugin },
    // The import half is the local rule (an allow-list of names); the global half is
    // ESLint's `no-restricted-globals`. The three global-object names must be declared as
    // globals for its `checkGlobalObject` option to see `window.fetch`/`self.fetch`.
    languageOptions: {
      globals: { globalThis: 'readonly', self: 'readonly', window: 'readonly' },
    },
    rules: {
      'evidgate/no-http-outside-core': 'error',
      'no-restricted-globals': [
        'error',
        {
          globals: [
            { name: 'EventSource', message: HTTP_GLOBAL_MESSAGE },
            { name: 'WebSocket', message: HTTP_GLOBAL_MESSAGE },
            { name: 'XMLHttpRequest', message: HTTP_GLOBAL_MESSAGE },
            { name: 'fetch', message: HTTP_GLOBAL_MESSAGE },
          ],
          checkGlobalObject: true,
        },
      ],
    },
  },
  {
    name: 'evidgate/web-no-hand-written-wire-dto',
    files: APPLICATION_FILES,
    plugins: { evidgate: plugin },
    rules: { 'evidgate/no-hand-written-wire-dto': 'error' },
  },
  {
    name: 'evidgate/web-feature-boundary',
    files: APPLICATION_FILES,
    plugins: { evidgate: plugin },
    rules: { 'evidgate/no-feature-cross-import': 'error' },
  },
  // 09.1.1-T10 (AC7, ADR-0008 rule 4; 00 §5.6 W6, FR-09-15): the app origin runs under the
  // Trusted Types policy of apps/web/scripts/app-origin-csp.json, which has no
  // `angular#unsafe-bypass` — the browser refuses a bypassSecurityTrust* value at the DOM
  // sink, so the rule reports it at lint time. The one reviewed exception (the 06
  // artifact-viewer bridge, owned by 09.4.1-T5) is the rule's own allow-list, not a block.
  {
    name: 'evidgate/web-no-bypass-security-trust',
    files: APPLICATION_FILES,
    plugins: { evidgate: plugin },
    rules: { 'evidgate/no-bypass-security-trust': 'error' },
  },
];
