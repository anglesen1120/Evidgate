// 09.1.1-T3.1 (plan §2 AC3 "a violating file fails lint", §8 T47 W1, FR-09-10): proves the
// boundary rules — no HttpClient/fetch outside core, no hand-written wire DTO, no
// feature→feature import — by linting inline snippets with the exported flat-config blocks
// (apps/web/lint/boundaries.cjs, including the built-in `no-restricted-globals` its HTTP
// block registers) and the typescript-eslint parser, exactly as eslint.config.js does.
// 09.1.1-T10 (AC7) adds the fourth rule here: `evidgate/no-bypass-security-trust`, which
// holds every DomSanitizer bypass except the one reviewed artifact-viewer bridge.
//
// This spec lives in @evidgate/cli, not in apps/web, on purpose: `ng test` compiles its
// specs with the Angular application builder for the browser, so a spec there cannot load
// ESLint and the CommonJS rule modules, which need Node's `require` (and `node:path` in the
// boundary rule). The Vitest `node` workspaces are the only test runner an existing
// scripts/verify.sh stage (`vitest`) executes in Node, and this package already hosts the
// repository-level checks over other workspaces (parity.test.ts, schemaParity.test.ts).
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

interface LintMessage {
  readonly ruleId: string | null;
  readonly severity: number;
  readonly message: string;
}

interface LinterInstance {
  verify(code: string, config: readonly unknown[], filename: string): LintMessage[];
}

interface LinterConstructor {
  new (options?: { cwd?: string }): LinterInstance;
}

interface BoundariesBlock {
  readonly files?: readonly string[];
  readonly rules?: Readonly<Record<string, unknown>>;
}

const requireFromHere = createRequire(import.meta.url);

// Loaded through Node's `require`, not through an ESM import: the rules are CommonJS (like
// eslint.config.js itself) and ESLint is a Node-only package.
const BOUNDARIES_MODULE = '../../../apps/web/lint/boundaries.cjs';
const { Linter } = requireFromHere('eslint') as { Linter: LinterConstructor };
const boundaries = requireFromHere(BOUNDARIES_MODULE) as readonly BoundariesBlock[];
const typescriptEslint = requireFromHere('typescript-eslint') as { parser: unknown };

const REPO_ROOT = fileURLToPath(new URL('../../../', import.meta.url));

// The parser is the only thing the typed blocks of eslint.config.js contribute to these
// rules, so a bare `Linter` needs it supplied explicitly.
const PARSER_BLOCK = {
  files: ['**/*.ts'],
  languageOptions: {
    ecmaVersion: 'latest',
    sourceType: 'module',
    parser: typescriptEslint.parser,
  },
};

const HTTP_OUTSIDE_CORE = 'evidgate/no-http-outside-core';
// The global half of the HTTP boundary is ESLint's own rule (see boundaries.cjs).
const HTTP_GLOBALS = 'no-restricted-globals';
const WIRE_DTO = 'evidgate/no-hand-written-wire-dto';
const FEATURE_BOUNDARY = 'evidgate/no-feature-cross-import';
const BYPASS_TRUST = 'evidgate/no-bypass-security-trust';

const FEATURE_FILE = 'apps/web/src/app/features/runs/x.ts';
const CORE_HTTP_FILE = 'apps/web/src/app/core/http/x.ts';
const CORE_DATA_FILE = 'apps/web/src/app/core/data/x.ts';
const CORE_FILE = 'apps/web/src/app/core/routes.ts';
const SHARED_FILE = 'apps/web/src/app/shared/x.ts';
const APP_CONFIG_FILE = 'apps/web/src/app/app.config.ts';

function lint(code: string, filename: string): readonly LintMessage[] {
  return new Linter({ cwd: REPO_ROOT }).verify(code, [PARSER_BLOCK, ...boundaries], filename);
}

function ruleIds(code: string, filename: string): readonly (string | null)[] {
  return lint(code, filename).map((message) => message.ruleId);
}

// The allow-list is a property of the bypass rule itself, and the exported blocks only match
// `apps/web/src/**/*.ts`, so the filenames a file must not hide behind — an absolute path from
// a checkout directory named like the bridge, a Windows path — cannot be expressed through
// them. ESLint resolves a filename against `cwd` before it matches `files`, so the filesystem
// root is the only `cwd` from which an out-of-tree absolute path matches a block at all.
const BYPASS_RULE_MODULE = '../../../apps/web/lint/rules/no-bypass-security-trust.cjs';
const bypassRule = requireFromHere(BYPASS_RULE_MODULE) as unknown;
const FILESYSTEM_ROOT = fileURLToPath(new URL('/', import.meta.url));
const RULE_ONLY_BLOCK = {
  files: ['**/*.ts'],
  languageOptions: { ecmaVersion: 'latest', sourceType: 'module', parser: typescriptEslint.parser },
  plugins: { evidgate: { rules: { 'no-bypass-security-trust': bypassRule } } },
  rules: { 'evidgate/no-bypass-security-trust': 'error' },
};

function ruleIdsAt(code: string, filename: string): readonly (string | null)[] {
  return new Linter({ cwd: FILESYSTEM_ROOT })
    .verify(code, [RULE_ONLY_BLOCK], filename)
    .map((message) => message.ruleId);
}

describe('apps/web lint boundaries (09.1.1 AC3, plan §8 T47 W1)', () => {
  it('registers the boundary rules for the application sources', () => {
    expect(boundaries.flatMap((block) => Object.keys(block.rules ?? {}))).toEqual([
      HTTP_OUTSIDE_CORE,
      HTTP_GLOBALS,
      WIRE_DTO,
      FEATURE_BOUNDARY,
      BYPASS_TRUST,
    ]);
    expect(new Set(boundaries.flatMap((block) => block.files ?? []))).toEqual(
      new Set(['apps/web/src/**/*.ts']),
    );
  });

  describe('no-http-outside-core', () => {
    const httpClientImport = [
      "import { HttpClient } from '@angular/common/http';",
      '',
      'export const client = HttpClient;',
      '',
    ].join('\n');

    it('rejects an HttpClient import in a feature', () => {
      expect(ruleIds(httpClientImport, FEATURE_FILE)).toEqual([HTTP_OUTSIDE_CORE]);
    });

    it('allows an HttpClient import in core/http and core/data', () => {
      expect(lint(httpClientImport, CORE_HTTP_FILE)).toEqual([]);
      expect(lint(httpClientImport, CORE_DATA_FILE)).toEqual([]);
    });

    it('allows only the listed @angular/common/http symbols outside core', () => {
      const allowed = [
        'import {',
        '  HttpErrorResponse,',
        '  provideHttpClient,',
        '  withInterceptors,',
        "} from '@angular/common/http';",
        '',
        'export const providers = [provideHttpClient(withInterceptors([])), HttpErrorResponse];',
        '',
      ].join('\n');
      expect(lint(allowed, APP_CONFIG_FILE)).toEqual([]);

      const banned = [
        "import { HttpBackend } from '@angular/common/http';",
        '',
        'export const backend = HttpBackend;',
        '',
      ].join('\n');
      expect(ruleIds(banned, FEATURE_FILE)).toEqual([HTTP_OUTSIDE_CORE]);
    });

    it('rejects a re-export, a star re-export and a dynamic import of @angular/common/http', () => {
      const reExport = ["export { HttpClient } from '@angular/common/http';", ''].join('\n');
      expect(ruleIds(reExport, FEATURE_FILE)).toEqual([HTTP_OUTSIDE_CORE]);

      const star = ["export * from '@angular/common/http';", ''].join('\n');
      expect(ruleIds(star, FEATURE_FILE)).toEqual([HTTP_OUTSIDE_CORE]);

      const dynamic = [
        "export const load = (): Promise<unknown> => import('@angular/common/http');",
        '',
      ].join('\n');
      expect(ruleIds(dynamic, FEATURE_FILE)).toEqual([HTTP_OUTSIDE_CORE]);
    });

    it('allows an allowed symbol through a re-export, and the whole module inside core', () => {
      const allowedReExport = [
        "export { provideHttpClient, withInterceptors } from '@angular/common/http';",
        '',
      ].join('\n');
      expect(lint(allowedReExport, APP_CONFIG_FILE)).toEqual([]);

      const typeReExport = [
        "export type { HttpErrorResponse } from '@angular/common/http';",
        '',
      ].join('\n');
      expect(lint(typeReExport, SHARED_FILE)).toEqual([]);

      const dynamic = [
        "export const load = (): Promise<unknown> => import('@angular/common/http');",
        '',
      ].join('\n');
      expect(lint(dynamic, CORE_HTTP_FILE)).toEqual([]);
      expect(lint(dynamic, CORE_DATA_FILE)).toEqual([]);
    });

    it('rejects an SDK operation, the default client and a namespace import outside core', () => {
      const operation = [
        "import { listRuns } from '@evidgate/sdk';",
        '',
        'export const load = listRuns;',
        '',
      ].join('\n');
      expect(ruleIds(operation, FEATURE_FILE)).toEqual([HTTP_OUTSIDE_CORE]);

      const defaultClient = [
        "import { client } from '@evidgate/sdk';",
        '',
        'export const api = client;',
        '',
      ].join('\n');
      expect(ruleIds(defaultClient, FEATURE_FILE)).toEqual([HTTP_OUTSIDE_CORE]);

      const namespace = [
        "import * as sdk from '@evidgate/sdk';",
        '',
        'export const api = sdk;',
        '',
      ].join('\n');
      expect(ruleIds(namespace, SHARED_FILE)).toEqual([HTTP_OUTSIDE_CORE]);

      const factories = [
        "import { createClient, createConfig } from '@evidgate/sdk';",
        '',
        'export const build = [createClient, createConfig];',
        '',
      ].join('\n');
      expect(ruleIds(factories, SHARED_FILE)).toEqual([HTTP_OUTSIDE_CORE, HTTP_OUTSIDE_CORE]);

      const mixed = [
        "import { type RunPage, listRuns } from '@evidgate/sdk';",
        '',
        'export const load = (): RunPage | undefined => listRuns;',
        '',
      ].join('\n');
      expect(ruleIds(mixed, FEATURE_FILE)).toEqual([HTTP_OUTSIDE_CORE]);

      const reExport = ["export { createConfig } from '@evidgate/sdk';", ''].join('\n');
      expect(ruleIds(reExport, SHARED_FILE)).toEqual([HTTP_OUTSIDE_CORE]);

      const mixedReExport = [
        "export { type RunPage, createConfig } from '@evidgate/sdk';",
        '',
      ].join('\n');
      expect(ruleIds(mixedReExport, SHARED_FILE)).toEqual([HTTP_OUTSIDE_CORE]);

      const star = ["export * from '@evidgate/sdk';", ''].join('\n');
      expect(ruleIds(star, SHARED_FILE)).toEqual([HTTP_OUTSIDE_CORE]);

      const dynamic = [
        "export const load = (): Promise<unknown> => import('@evidgate/sdk');",
        '',
      ].join('\n');
      expect(ruleIds(dynamic, FEATURE_FILE)).toEqual([HTTP_OUTSIDE_CORE]);
    });

    it('allows SDK types and the error-envelope guard outside core', () => {
      const typeImport = [
        "import type { RunPage } from '@evidgate/sdk';",
        '',
        'export type Page = RunPage;',
        '',
      ].join('\n');
      expect(lint(typeImport, FEATURE_FILE)).toEqual([]);

      const inlineType = [
        "import { type RunPage } from '@evidgate/sdk';",
        '',
        'export type Page = RunPage;',
        '',
      ].join('\n');
      expect(lint(inlineType, FEATURE_FILE)).toEqual([]);

      const guard = [
        "import { isErrorEnvelope } from '@evidgate/sdk';",
        '',
        'export const classify = (body: unknown): boolean => isErrorEnvelope(body);',
        '',
      ].join('\n');
      expect(lint(guard, SHARED_FILE)).toEqual([]);

      const typeReExport = ["export type { RunPage } from '@evidgate/sdk';", ''].join('\n');
      expect(lint(typeReExport, SHARED_FILE)).toEqual([]);

      const inlineTypeReExport = ["export { type RunPage } from '@evidgate/sdk';", ''].join('\n');
      expect(lint(inlineTypeReExport, SHARED_FILE)).toEqual([]);
    });

    it('allows every SDK value inside core/http and core/data, specs included', () => {
      const code = [
        "import { client, createConfig, listRuns } from '@evidgate/sdk';",
        '',
        'export const all = [client, createConfig, listRuns];',
        '',
      ].join('\n');
      expect(lint(code, CORE_HTTP_FILE)).toEqual([]);
      expect(lint(code, CORE_DATA_FILE)).toEqual([]);
      expect(lint(code, 'apps/web/src/app/core/http/error-mapper.spec.ts')).toEqual([]);
      expect(lint(code, 'apps/web/src/app/core/data/runs-data.service.spec.ts')).toEqual([]);
      expect(ruleIds(code, 'apps/web/src/app/features/runs/runs.store.spec.ts')).toEqual([
        HTTP_OUTSIDE_CORE,
        HTTP_OUTSIDE_CORE,
        HTTP_OUTSIDE_CORE,
      ]);
    });

    it('rejects fetch() outside core and allows it inside core/http', () => {
      const code = [
        'export function load(url: string): Promise<Response> {',
        '  return fetch(url);',
        '}',
        '',
      ].join('\n');
      expect(ruleIds(code, FEATURE_FILE)).toEqual([HTTP_GLOBALS]);
      expect(lint(code, CORE_HTTP_FILE)).toEqual([]);
    });

    it('rejects window.fetch and a new WebSocket outside core', () => {
      const member = [
        'export const load = (url: string): Promise<Response> => window.fetch(url);',
        '',
      ].join('\n');
      expect(ruleIds(member, FEATURE_FILE)).toEqual([HTTP_GLOBALS]);

      const socket = [
        "export const socket = new WebSocket('wss://evidence.test/v1/stream');",
        '',
      ].join('\n');
      expect(ruleIds(socket, SHARED_FILE)).toEqual([HTTP_GLOBALS]);
    });

    it('does not flag a local binding that shadows the global', () => {
      const code = [
        'const fetch = (url: string): string => url;',
        '',
        'export const read = fetch;',
        '',
      ].join('\n');
      expect(lint(code, FEATURE_FILE)).toEqual([]);
    });
  });

  describe('no-hand-written-wire-dto', () => {
    it('rejects an interface with a snake_case wire key', () => {
      const code = ['export interface RunRow {', '  run_id: string;', '}', ''].join('\n');
      expect(ruleIds(code, FEATURE_FILE)).toEqual([WIRE_DTO]);
    });

    it('rejects a payload-shaped type alias', () => {
      const code = ['export type RunDto = {', '  id: string;', '};', ''].join('\n');
      expect(ruleIds(code, FEATURE_FILE)).toEqual([WIRE_DTO]);
    });

    it('checks core as well: no wire type anywhere in the app', () => {
      const code = [
        'export interface RunCursor {',
        "  'next_cursor': string | null;",
        '}',
        '',
      ].join('\n');
      expect(ruleIds(code, CORE_DATA_FILE)).toEqual([WIRE_DTO]);
    });

    it('accepts a camelCase view model', () => {
      const viewModel = ['export interface RunRowViewModel {', '  runId: string;', '}', ''].join(
        '\n',
      );
      expect(lint(viewModel, FEATURE_FILE)).toEqual([]);

      const alias = ['export type RunRowView = {', '  runId: string;', '};', ''].join('\n');
      expect(lint(alias, FEATURE_FILE)).toEqual([]);
    });

    it('does not treat an underscore-prefixed private or phantom key as a wire key', () => {
      const code = [
        'export interface RunRowViewModel {',
        '  _private: string;',
        '  __brand: never;',
        '  runId: string;',
        '}',
        '',
      ].join('\n');
      expect(lint(code, FEATURE_FILE)).toEqual([]);
    });

    it('accepts an inline error-envelope value in a spec', () => {
      const code = [
        "import { isErrorEnvelope } from '@evidgate/sdk';",
        '',
        "export const body = { error: { code: 'forbidden', request_id: 'r-1' } };",
        '',
        'export const parsed = isErrorEnvelope(body);',
        '',
      ].join('\n');
      expect(lint(code, 'apps/web/src/app/core/http/error-mapper.spec.ts')).toEqual([]);
    });
  });

  describe('no-feature-cross-import', () => {
    it('rejects a sibling feature import, relative or aliased', () => {
      const relative = [
        "import { onboarding } from '../onboarding/store';",
        '',
        'export const store = onboarding;',
        '',
      ].join('\n');
      expect(ruleIds(relative, FEATURE_FILE)).toEqual([FEATURE_BOUNDARY]);

      const aliased = [
        "import { onboarding } from '@app/features/onboarding/store';",
        '',
        'export const store = onboarding;',
        '',
      ].join('\n');
      expect(ruleIds(aliased, FEATURE_FILE)).toEqual([FEATURE_BOUNDARY]);

      const barrel = [
        "import { onboardingStore } from '../onboarding';",
        '',
        'export const store = onboardingStore;',
        '',
      ].join('\n');
      expect(ruleIds(barrel, FEATURE_FILE)).toEqual([FEATURE_BOUNDARY]);
    });

    it('rejects a dynamic import of a sibling feature', () => {
      const code = [
        "export const load = (): Promise<unknown> => import('../onboarding/store');",
        '',
      ].join('\n');
      expect(ruleIds(code, FEATURE_FILE)).toEqual([FEATURE_BOUNDARY]);
    });

    it('allows core and its own feature area', () => {
      const code = [
        "import { ApiGateway } from '../../core/http/api-gateway';",
        "import { store } from './store';",
        '',
        'export const both = [ApiGateway, store];',
        '',
      ].join('\n');
      expect(lint(code, FEATURE_FILE)).toEqual([]);
    });

    it('rejects a core or shared dependency on a feature', () => {
      const code = [
        "import { runs } from '../features/runs/x';",
        '',
        'export const routes = runs;',
        '',
      ].join('\n');
      expect(ruleIds(code, CORE_FILE)).toEqual([FEATURE_BOUNDARY]);
      expect(ruleIds(code, SHARED_FILE)).toEqual([FEATURE_BOUNDARY]);
    });
  });

  describe('no-bypass-security-trust', () => {
    // The T10.1 done-when: a feature file hands sanitized output back to a sink.
    const bypassCall = [
      'export function bind(sanitizer: DomSanitizer, value: string): unknown {',
      '  return sanitizer.bypassSecurityTrustHtml(value);',
      '}',
      '',
    ].join('\n');

    it('rejects a bypassSecurityTrustHtml call in a feature file', () => {
      expect(ruleIds(bypassCall, FEATURE_FILE)).toEqual([BYPASS_TRUST]);
    });

    it('rejects each of the five DomSanitizer bypass methods', () => {
      for (const method of ['Html', 'Style', 'Script', 'Url', 'ResourceUrl']) {
        const code = [
          'export function bind(sanitizer: DomSanitizer, value: string): unknown {',
          `  return sanitizer.bypassSecurityTrust${method}(value);`,
          '}',
          '',
        ].join('\n');
        expect(ruleIds(code, FEATURE_FILE)).toEqual([BYPASS_TRUST]);
      }
    });

    it('rejects optional chaining and a string-computed member', () => {
      const optionalMember = [
        'export function bind(sanitizer: DomSanitizer, value: string): unknown {',
        '  return sanitizer?.bypassSecurityTrustUrl(value);',
        '}',
        '',
      ].join('\n');
      expect(ruleIds(optionalMember, FEATURE_FILE)).toEqual([BYPASS_TRUST]);

      const optionalCall = [
        'export function bind(sanitizer: DomSanitizer, value: string): unknown {',
        '  return sanitizer.bypassSecurityTrustScript?.(value);',
        '}',
        '',
      ].join('\n');
      expect(ruleIds(optionalCall, FEATURE_FILE)).toEqual([BYPASS_TRUST]);

      const computed = [
        'export function bind(sanitizer: DomSanitizer, value: string): unknown {',
        "  return sanitizer['bypassSecurityTrustResourceUrl'](value);",
        '}',
        '',
      ].join('\n');
      expect(ruleIds(computed, FEATURE_FILE)).toEqual([BYPASS_TRUST]);
    });

    it('rejects an aliased member reference that is never called', () => {
      const code = [
        'export function bind(sanitizer: DomSanitizer): (value: string) => unknown {',
        '  const bindHtml = sanitizer.bypassSecurityTrustHtml;',
        '  return bindHtml;',
        '}',
        '',
      ].join('\n');
      expect(ruleIds(code, FEATURE_FILE)).toEqual([BYPASS_TRUST]);
    });

    it('rejects a destructured bypass and its bare identifier call', () => {
      const code = [
        'export function bind(sanitizer: DomSanitizer, value: string): unknown {',
        '  const { bypassSecurityTrustStyle } = sanitizer;',
        '  return bypassSecurityTrustStyle(value);',
        '}',
        '',
      ].join('\n');
      expect(ruleIds(code, FEATURE_FILE)).toEqual([BYPASS_TRUST, BYPASS_TRUST]);
    });

    // Each of these builds the member name without a plain identifier or string property, and
    // each is reported exactly once: the constant that spells the name is the evidence, so a
    // key the rule cannot evaluate cannot hide the call.
    it('rejects a name spelled by a template literal, a variable or Reflect.get', () => {
      const templateKey = [
        'export function bind(sanitizer: DomSanitizer, value: string): unknown {',
        '  return sanitizer[`bypassSecurityTrustHtml`](value);',
        '}',
        '',
      ].join('\n');
      expect(ruleIds(templateKey, FEATURE_FILE)).toEqual([BYPASS_TRUST]);

      // A template WITH an expression is not folded; its inner literal is what the rule sees.
      const interpolatedKey = [
        'export function bind(sanitizer: DomSanitizer, value: string): unknown {',
        "  return sanitizer[`${'bypassSecurityTrustHtml'}`](value);",
        '}',
        '',
      ].join('\n');
      expect(ruleIds(interpolatedKey, FEATURE_FILE)).toEqual([BYPASS_TRUST]);

      const variableKey = [
        'export function bind(sanitizer: DomSanitizer, value: string): unknown {',
        "  const name = 'bypassSecurityTrustResourceUrl';",
        '  return sanitizer[name](value);',
        '}',
        '',
      ].join('\n');
      expect(ruleIds(variableKey, FEATURE_FILE)).toEqual([BYPASS_TRUST]);

      const reflected = [
        'export function read(sanitizer: DomSanitizer, value: string): unknown {',
        "  return Reflect.get(sanitizer, 'bypassSecurityTrustScript');",
        '}',
        '',
      ].join('\n');
      expect(ruleIds(reflected, FEATURE_FILE)).toEqual([BYPASS_TRUST]);
    });

    it('rejects a name split across string concatenation', () => {
      const inlineKey = [
        'export function bind(sanitizer: DomSanitizer, value: string): unknown {',
        "  return sanitizer['bypass' + 'SecurityTrustHtml'](value);",
        '}',
        '',
      ].join('\n');
      expect(ruleIds(inlineKey, FEATURE_FILE)).toEqual([BYPASS_TRUST]);

      const splitKey = [
        'export function bind(sanitizer: DomSanitizer, value: string): unknown {',
        "  const name = 'bypass' + 'SecurityTrustUrl';",
        '  return sanitizer[name](value);',
        '}',
        '',
      ].join('\n');
      expect(ruleIds(splitKey, FEATURE_FILE)).toEqual([BYPASS_TRUST]);
    });

    it('rejects a bypass method imported under an alias', () => {
      const aliased = [
        "import { bypassSecurityTrustHtml as bindHtml } from './sanitize';",
        '',
        'export const bind = bindHtml;',
        '',
      ].join('\n');
      expect(ruleIds(aliased, FEATURE_FILE)).toEqual([BYPASS_TRUST]);

      const plain = [
        "import { bypassSecurityTrustUrl } from './sanitize';",
        '',
        'export const bind = bypassSecurityTrustUrl;',
        '',
      ].join('\n');
      expect(ruleIds(plain, FEATURE_FILE)).toEqual([BYPASS_TRUST]);
    });

    // An inline template is source text, so a call inside it never reaches an AST visitor: the
    // property value is scanned by content instead.
    it('rejects a bypass call written inside an inline template', () => {
      const code = [
        '@Component({',
        "  selector: 'evidgate-viewer',",
        '  template: `<div [innerHTML]="sanitizer.bypassSecurityTrustHtml(html)"></div>`,',
        '})',
        'export class ViewerComponent {}',
        '',
      ].join('\n');
      expect(ruleIds(code, FEATURE_FILE)).toEqual([BYPASS_TRUST]);

      // A mention, a look-alike call and an unrelated property stay legal.
      const clean = [
        '@Component({',
        '  template: `<div [innerHTML]="html"></div>`,',
        "  styleUrl: './x.css',",
        '})',
        'export class CleanComponent {',
        "  note = 'bypassSecurityTrustHtml is not used here';",
        "  other = 'sanitizer.bypassSecurityTrustHtmlX(html)';",
        '}',
        '',
      ].join('\n');
      expect(lint(clean, FEATURE_FILE)).toEqual([]);
    });

    it('treats a constant template string as the constant it is', () => {
      const code = [
        'const viewer = { template: `bypassSecurityTrustHtml` };',
        'export const sink = viewer;',
        '',
      ].join('\n');
      expect(ruleIds(code, FEATURE_FILE)).toEqual([BYPASS_TRUST]);
    });

    // The rule is a lint-time report, not a claim about the call: the app-origin policy simply
    // has no policy for the value, so the browser refuses it at the sink.
    it('reports the DOM-sink refusal rather than a throw from the call', () => {
      const messages = lint(bypassCall, FEATURE_FILE);
      expect(messages.length).toBe(1);
      expect(messages[0]?.message).toContain('no `angular#unsafe-bypass` Trusted Types policy');
      expect(messages[0]?.message).toContain('DOM sink');
      expect(messages[0]?.message).not.toContain('throws');
    });

    it('allows the bypass inside the reviewed artifact-viewer bridge', () => {
      const bridgeFile = 'apps/web/src/app/features/runs/artifact-viewer-bridge/x.ts';
      expect(lint(bypassCall, bridgeFile)).toEqual([]);
      expect(lint(bypassCall, 'apps/web/src/app/shared/artifact-viewer-bridge/x.ts')).toEqual([]);
    });

    it('does not allow a name that merely contains the bridge words', () => {
      expect(
        ruleIds(bypassCall, 'apps/web/src/app/features/runs/artifact-viewer-bridge-notes.ts'),
      ).toEqual([BYPASS_TRUST]);
      expect(
        ruleIds(bypassCall, 'apps/web/src/app/features/runs/not-artifact-viewer-bridge/x.ts'),
      ).toEqual([BYPASS_TRUST]);
      expect(ruleIds(bypassCall, CORE_HTTP_FILE)).toEqual([BYPASS_TRUST]);
    });

    // The guard reads the segments BELOW the last `apps/web/src/app` marker, so a directory
    // that merely shares the name — the checkout's own parent, or a `..` traveller — cannot
    // switch the rule off for the whole tree.
    it('anchors the allow-list below apps/web, not at any bridge segment in the path', () => {
      expect(
        ruleIdsAt(
          bypassCall,
          '/checkout/artifact-viewer-bridge/apps/web/src/app/features/runs/x.ts',
        ),
      ).toEqual([BYPASS_TRUST]);
      expect(
        ruleIdsAt(bypassCall, 'artifact-viewer-bridge/apps/web/src/app/features/runs/x.ts'),
      ).toEqual([BYPASS_TRUST]);
      expect(
        ruleIdsAt(bypassCall, '/checkout/apps/web/src/app/artifact-viewer-bridge/../features/x.ts'),
      ).toEqual([BYPASS_TRUST]);
      expect(
        ruleIdsAt(bypassCall, 'apps/web/src/app/artifact-viewer-bridge/../features/x.ts'),
      ).toEqual([BYPASS_TRUST]);
      expect(
        ruleIdsAt(bypassCall, '/checkout/apps/web/src/app/features/my-artifact-viewer-bridge/x.ts'),
      ).toEqual([BYPASS_TRUST]);
      expect(ruleIdsAt(bypassCall, '/checkout/artifact-viewer-bridge/x.ts')).toEqual([
        BYPASS_TRUST,
      ]);
    });

    it('allows a Windows or absolute path inside the reviewed bridge directory', () => {
      expect(
        ruleIdsAt(
          bypassCall,
          'C:\\repo\\apps\\web\\src\\app\\features\\runs\\artifact-viewer-bridge\\x.ts',
        ),
      ).toEqual([]);
      expect(
        ruleIdsAt(bypassCall, '/checkout/apps/web/src/app/shared/artifact-viewer-bridge/x.ts'),
      ).toEqual([]);
      // `.`/`..` that resolve back inside the bridge keep the file inside it.
      expect(
        ruleIdsAt(bypassCall, 'apps/web/src/app/features/./../artifact-viewer-bridge/x.ts'),
      ).toEqual([]);
    });

    it('accepts sanitize() and an unrelated method name', () => {
      const sanitize = [
        'export function read(sanitizer: DomSanitizer, value: string): string | null {',
        '  return sanitizer.sanitize(SecurityContext.HTML, value);',
        '}',
        '',
      ].join('\n');
      expect(lint(sanitize, FEATURE_FILE)).toEqual([]);

      const unrelated = [
        'export function bind(sanitizer: DomSanitizer, value: string): unknown {',
        '  return sanitizer.bypassSecurityTrustFoo(value);',
        '}',
        '',
      ].join('\n');
      expect(lint(unrelated, FEATURE_FILE)).toEqual([]);
    });

    it('holds a spec file to the same rule', () => {
      expect(ruleIds(bypassCall, 'apps/web/src/app/x.spec.ts')).toEqual([BYPASS_TRUST]);
    });

    // The catch-all constants must not turn ordinary text into a finding.
    it('accepts an unrelated string, a comment and a look-alike property', () => {
      const code = [
        '// sanitizer.bypassSecurityTrustHtml(value) is the call this file exists to avoid.',
        "export const label = 'not a bypass';",
        "export const lookalike = 'bypassSecurityTrustHtmlX';",
        'export const keys = { bypassSecurityTrustHtmlX: 1 };',
        'export function read(sanitizer: DomSanitizer): number {',
        '  return sanitizer.bypassSecurityTrustHtmlX;',
        '}',
        '',
      ].join('\n');
      expect(lint(code, FEATURE_FILE)).toEqual([]);

      // A key whose constant spells a different name is not a bypass either.
      const otherKey = [
        'export function bind(sanitizer: DomSanitizer, value: string): unknown {',
        "  return sanitizer['bypassSecurityTrustHtml' + 'X'](value);",
        '}',
        '',
      ].join('\n');
      expect(lint(otherKey, FEATURE_FILE)).toEqual([]);
    });
  });
});
