// 09.1.1-T3.1 (plan §2 AC3 "a violating file fails lint", §8 T47 W1, FR-09-10): proves the
// boundary rules — no HttpClient/fetch outside core, no hand-written wire DTO, no
// feature→feature import — by linting inline snippets with the exported flat-config blocks
// (apps/web/lint/boundaries.cjs, including the built-in `no-restricted-globals` its HTTP
// block registers) and the typescript-eslint parser, exactly as eslint.config.js does.
// 09.1.1-T10 (AC7) adds the fourth rule here: `evidgate/no-bypass-security-trust`, which
// holds every DomSanitizer bypass except the one reviewed artifact-viewer bridge.
// 09.7.1-T1 (plan 09.7.1 §8 S3, T1.1, AC3) adds the fifth: `evidgate/no-secret-field-in-store`,
// which holds a Settings store to metadata-only field names and is registered for the Settings
// feature alone. 09.7.1-T3 (queue F16) extends its specs to the positions it gained: a member read
// and a bare read in the `.html` file a `templateUrl` names, a bare read in an inline template
// expression, and a one-time result type held in state.
//
// This spec lives in @evidgate/cli, not in apps/web, on purpose: `ng test` compiles its
// specs with the Angular application builder for the browser, so a spec there cannot load
// ESLint and the CommonJS rule modules, which need Node's `require` (and `node:path` in the
// boundary rule). The Vitest `node` workspaces are the only test runner an existing
// scripts/verify.sh stage (`vitest`) executes in Node, and this package already hosts the
// repository-level checks over other workspaces (parity.test.ts, schemaParity.test.ts).
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { join, relative } from 'node:path';
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
const SECRET_FIELD = 'evidgate/no-secret-field-in-store';

const FEATURE_FILE = 'apps/web/src/app/features/runs/x.ts';
const CORE_HTTP_FILE = 'apps/web/src/app/core/http/x.ts';
const CORE_DATA_FILE = 'apps/web/src/app/core/data/x.ts';
const CORE_FILE = 'apps/web/src/app/core/routes.ts';
const SHARED_FILE = 'apps/web/src/app/shared/x.ts';
const APP_CONFIG_FILE = 'apps/web/src/app/app.config.ts';

// 09.7.1-T1 (plan 09.7.1 §8 S3, T1.1): the Settings feature is the one folder where a store may
// hold metadata only, so a secret-shaped field name fails lint there and nowhere else.
const SETTINGS_SECRETS_FILE = 'apps/web/src/app/features/settings/secrets/secrets.store.ts';
const SECRET_FIXTURE_DIR = new URL(
  '../../../apps/web/lint/fixtures/no-secret-field-in-store/',
  import.meta.url,
);

/**
 * A fixture's source text. The two fixtures are `.ts.txt` files, not `.ts`: eslint.config.js's
 * block for every `.ts` under apps/web types them against apps/web/tsconfig.json, which includes
 * only `src`, so a deliberate violation named `.ts` would fail the lint job instead of this spec.
 */
function secretFixture(name: string): string {
  return readFileSync(new URL(name, SECRET_FIXTURE_DIR), 'utf8');
}

/**
 * The fixture directory as a filesystem path, and one fixture inside it as an absolute filename.
 * A `templateUrl` is resolved against the REAL directory of the file being linted (queue F16), so
 * the component fixture is linted under a path inside this directory and the `.html.txt` file it
 * names sits next to it.
 */
const SECRET_FIXTURE_PATH = fileURLToPath(SECRET_FIXTURE_DIR);

function secretFixturePath(name: string): string {
  return join(SECRET_FIXTURE_PATH, name);
}

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

// 09.7.1-T3 (queue F16): the secret rule is also applied on its own, from the filesystem root.
// A `templateUrl` is resolved against the REAL directory of the file being linted, and the
// registered Settings block only matches `apps/web/src/app/features/settings/**/*.ts`, so a
// fixture directory cannot be reached through it — an absolute filename can only match a `**/*.ts`
// block. The registration is pinned by the first spec below, which is what keeps this a test seam
// and not a second way to run the rule.
const SECRET_RULE_MODULE = '../../../apps/web/lint/rules/no-secret-field-in-store.cjs';
const secretRule = requireFromHere(SECRET_RULE_MODULE) as unknown;
const SECRET_RULE_ONLY_BLOCK = {
  files: ['**/*.ts'],
  languageOptions: { ecmaVersion: 'latest', sourceType: 'module', parser: typescriptEslint.parser },
  plugins: { evidgate: { rules: { 'no-secret-field-in-store': secretRule } } },
  rules: { 'evidgate/no-secret-field-in-store': 'error' },
};

function lintSecretAt(code: string, filename: string): readonly LintMessage[] {
  return new Linter({ cwd: FILESYSTEM_ROOT }).verify(code, [SECRET_RULE_ONLY_BLOCK], filename);
}

/** The `data` each message interpolated, read back out of its text: `<prefix>"<name>".` */
function quoted(prefix: string, messages: readonly LintMessage[]): readonly string[] {
  return messages.map((message) => {
    const start = message.message.indexOf(prefix);
    const from = start < 0 ? -1 : message.message.indexOf('"', start + prefix.length);
    const to = from < 0 ? -1 : message.message.indexOf('"', from + 1);
    return from < 0 || to < 0 ? '' : message.message.slice(from + 1, to);
  });
}

describe('apps/web lint boundaries (09.1.1 AC3, plan §8 T47 W1)', () => {
  it('registers the boundary rules for the application sources', () => {
    expect(boundaries.flatMap((block) => Object.keys(block.rules ?? {}))).toEqual([
      HTTP_OUTSIDE_CORE,
      HTTP_GLOBALS,
      WIRE_DTO,
      FEATURE_BOUNDARY,
      BYPASS_TRUST,
      SECRET_FIELD,
    ]);
    expect(new Set(boundaries.flatMap((block) => block.files ?? []))).toEqual(
      new Set(['apps/web/src/**/*.ts', 'apps/web/src/app/features/settings/**/*.ts']),
    );
    // The Settings-only rule is one additive block of its own, and its narrower glob is the
    // whole scoping mechanism: dropping it would report every feature's stores.
    expect(boundaries.filter((block) => block.rules?.[SECRET_FIELD] !== undefined)).toHaveLength(1);
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

  // 09.7.1-T1 (plan 09.7.1 §8 S3 "secrets never in logs/UI", T1.1, AC3): a Settings store holds
  // metadata only — `token_prefix`, `rotation_state`, `last_rotated_at` — and the value is
  // revealed exactly once by the shared reveal dialog, never stored, re-fetched or bound.
  describe('no-secret-field-in-store', () => {
    const failing = secretFixture('failing.ts.txt');
    const passing = secretFixture('passing.ts.txt');

    it('rejects a secret-shaped field in a state interface, a state literal and a store class', () => {
      // One finding per reported position: the interface member, the state literal key, the field,
      // the private field and the constructor parameter property, in source order.
      const expected = [SECRET_FIELD, SECRET_FIELD, SECRET_FIELD, SECRET_FIELD, SECRET_FIELD];
      expect(ruleIds(failing, SETTINGS_SECRETS_FILE)).toEqual(expected);
      for (const message of lint(failing, SETTINGS_SECRETS_FILE)) {
        expect(message.message).toContain(
          'a Settings store holds metadata only (plan 09.7.1 §8 S3): keep the value server-side or in the one-time reveal component',
        );
      }
    });

    it('accepts the metadata-only names of the same positions', () => {
      expect(lint(passing, SETTINGS_SECRETS_FILE)).toEqual([]);
    });

    // The scope is the block's `files` glob, not the rule: a store outside Settings keeps its
    // own naming, and the rule must not creep into other features.
    it('leaves a store outside the Settings feature alone', () => {
      expect(ruleIds(failing, 'apps/web/src/app/features/runs/runs.store.ts')).toEqual([]);
      expect(
        ruleIds(failing, 'apps/web/src/app/features/settings-adjacent/secrets.store.ts'),
      ).toEqual([]);
    });

    // A spec has to spell the forbidden name to assert its absence, and the one-time reveal
    // component is the single reviewed place allowed to hold a value in memory.
    it('exempts a spec file and the one-time reveal component', () => {
      expect(
        ruleIds(failing, 'apps/web/src/app/features/settings/secrets/secrets.store.spec.ts'),
      ).toEqual([]);
      expect(
        ruleIds(failing, 'apps/web/src/app/features/settings/secrets/reveal/reveal-dialog.ts'),
      ).toEqual([]);
      expect(
        ruleIds(
          failing,
          'apps/web/src/app/features/settings/secrets/signing-secret-reveal.component.ts',
        ),
      ).toEqual([]);
      // `reveal` must be a whole word of the basename: a longer word that merely contains it
      // (`revealed`, `unrevealed`, `secrets-revealed-store`) cannot switch the rule off.
      const allFive = [SECRET_FIELD, SECRET_FIELD, SECRET_FIELD, SECRET_FIELD, SECRET_FIELD];
      expect(
        ruleIds(failing, 'apps/web/src/app/features/settings/secrets/secrets-revealed-store.ts'),
      ).toEqual(allFive);
      expect(ruleIds(failing, 'apps/web/src/app/features/settings/secrets/unrevealed.ts')).toEqual(
        allFive,
      );
      expect(
        ruleIds(failing, 'apps/web/src/app/features/settings/secrets/revealed.store.ts'),
      ).toEqual(allFive);
      // Only an exact directory segment or a `reveal` word of the basename exempts: a look-alike
      // directory and a `..` that leaves the reveal one stay reportable.
      expect(
        ruleIds(failing, 'apps/web/src/app/features/settings/secrets/not-reveal/secrets.store.ts'),
      ).toEqual(allFive);
      expect(
        ruleIds(failing, 'apps/web/src/app/features/settings/secrets/reveal/../secrets.store.ts'),
      ).toEqual(allFive);
    });

    it('reports every secret-shaped row of the name table', () => {
      const objectKeys = [
        'token',
        'secret',
        'password',
        'passwd',
        'plaintext',
        'credential',
        'apikey',
        'API_KEY',
        'apiKey',
        'signing_secret',
        'signingSecret',
        'secretValue',
        'private_key',
        'privateKey',
        'accessToken',
        'refreshToken',
        'bearerToken',
        'clientSecret',
        'tokenValue',
        'secret_text',
        'passwordRaw',
        // A secret word in the trailing position names the value, whatever the leading words are.
        'ingestToken',
        'apiToken',
        'webhookSecret',
        'rawToken',
        'plainSecret',
        'secretKey',
      ];
      for (const name of objectKeys) {
        const code = [`export const state = withState({ ${name}: '' });`, ''].join('\n');
        expect(ruleIds(code, SETTINGS_SECRETS_FILE), name).toEqual([SECRET_FIELD]);
      }

      // The same table in the declaration position, where camelCase keeps the wire-DTO rule
      // quiet: `interface SecretsState` is what makes its members store state.
      const signatures = [
        'token',
        'secret',
        'password',
        'passwd',
        'plaintext',
        'credential',
        'apikey',
        'apiKey',
        'signingSecret',
        'secretValue',
        'privateKey',
        'accessToken',
        'refreshToken',
        'bearerToken',
        'clientSecret',
        'tokenValue',
        'secretRaw',
        'passwordText',
      ];
      for (const name of signatures) {
        const code = [
          'export interface SecretsState {',
          `  readonly ${name}: string;`,
          '}',
          '',
        ].join('\n');
        expect(ruleIds(code, SETTINGS_SECRETS_FILE), name).toEqual([SECRET_FIELD]);
      }
    });

    it('accepts every metadata row of the name table', () => {
      const signatures = [
        'tokenPrefix',
        'tokenName',
        'last4',
        'rotationState',
        'secretRef',
        'secretName',
        'ingestTokens',
        'tokenCount',
        'expiresAt',
        'hasSecret',
        'hasToken',
        'tokenExpiresAt',
        'lastRotatedAt',
        'ciBindings',
        'passwordHash',
        'tokens',
      ];
      for (const name of signatures) {
        const code = [
          'export interface SecretsState {',
          `  readonly ${name}: string;`,
          '}',
          '',
        ].join('\n');
        expect(lint(code, SETTINGS_SECRETS_FILE), name).toEqual([]);
      }

      const objectKeys = ['token_prefix', 'secret_reference', 'last_rotated_at', 'ingest_tokens'];
      for (const name of objectKeys) {
        const code = [`export const state = withState({ ${name}: '' });`, ''].join('\n');
        expect(lint(code, SETTINGS_SECRETS_FILE), name).toEqual([]);
      }
    });

    // No import: the rule matches the callee name, and the workspace has no `@ngrx/signals`
    // dependency (the app's own store seam is shaped like a signalStore without the package).
    it('reports a state literal of withState, patchState and signalStore at any depth', () => {
      const code = [
        'export const Store = signalStore(withState({ meta: { plaintext: "" } }));',
        '',
        'export function save(store: unknown): void {',
        '  patchState(store, { token: "" });',
        '}',
        '',
      ].join('\n');
      expect(ruleIds(code, SETTINGS_SECRETS_FILE)).toEqual([SECRET_FIELD, SECRET_FIELD]);
    });

    // A request body is what the API receives, not what the store holds, and a nested `withState`
    // is the literal its own call reports: the walk stops at both boundaries.
    it('walks only the state literal, not a payload inside a closure or a nested withState', () => {
      const code = [
        'export const Store = signalStore(',
        '  withState({ tokenPrefix: "" }),',
        '  withMethods((store) => {',
        '    return {',
        '      save(token: string): void {',
        "        post('/v1/settings/secrets', { token });",
        '      },',
        '    };',
        '  }),',
        '  withState({ secretRef: "" }),',
        ');',
        '',
      ].join('\n');
      expect(lint(code, SETTINGS_SECRETS_FILE)).toEqual([]);

      // The nested literal is still reported once, from its own `withState` call.
      const nested = [
        'export const Store = signalStore(withState({ meta: { token: "" } }));',
        '',
      ].join('\n');
      expect(ruleIds(nested, SETTINGS_SECRETS_FILE)).toEqual([SECRET_FIELD]);
    });

    it('reports a signalState literal', () => {
      const code = ['export const state = signalState({ password: "" });', ''].join('\n');
      expect(ruleIds(code, SETTINGS_SECRETS_FILE)).toEqual([SECRET_FIELD]);
    });

    // 09.7.1-T2 (plan 09.7.1-T2 Notes, from the 09.7.1-T1 delta review): a state factory or
    // updater hands the literal it RETURNS over as state, so the argument is walked too — a
    // concise body, a block body's top-level `return`, and a `signal({ ... })` inside the factory.
    // One finding per occurrence: the factory literal, the nested literal, the wrapped one and the
    // three middle-of-name keys are all reported once, and nothing twice.
    it('reports a secret-shaped field of a state factory or updater', () => {
      const extended = secretFixture('failing-extended.ts.txt');
      const expected = [
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
      ];
      expect(ruleIds(extended, SETTINGS_SECRETS_FILE)).toEqual(expected);
      for (const message of lint(extended, SETTINGS_SECRETS_FILE)) {
        expect(message.message).toContain(
          'a Settings store holds metadata only (plan 09.7.1 §8 S3)',
        );
      }
    });

    // The same extension must not turn a metadata factory, a neutral word or a payload into a
    // finding: `withState(() => ({...}))` writes the ordinary metadata names, a method's request
    // body and a nested arrow's object are still not state, and `tokenizer`, `secretary`,
    // `passwordless`/`passwordLess` and the pagination cursor fields stay legal.
    it('accepts a factory or updater that hands over metadata only', () => {
      expect(lint(secretFixture('passing-extended.ts.txt'), SETTINGS_SECRETS_FILE)).toEqual([]);
    });

    // A secret word behind a value word is the value itself, wherever it stands in the name:
    // position is not what decides it, the word behind it is. A secret word followed by anything
    // else is metadata (`rawTokenMaterial`, `nextTokenRefreshAt` — see the README blind spots).
    it('reports a secret word in the middle of a name', () => {
      const reported = [
        'rawTokenValue',
        'signingSecretText',
        'passwordRawText',
        'pageTokenText',
        // The trailing position was already reported before T2 and still is: `pageToken` is not a
        // neutral name.
        'pageToken',
      ];
      for (const name of reported) {
        const code = [`export const state = withState({ ${name}: '' });`, ''].join('\n');
        expect(ruleIds(code, SETTINGS_SECRETS_FILE), name).toEqual([SECRET_FIELD]);
      }

      const neutral = [
        'tokenizer',
        'secretary',
        'passwordless',
        'passwordLess',
        'hasTokenValue',
        'rawTokenMaterial',
        'nextTokenRefreshAt',
        'cursor',
        'next_cursor',
      ];
      for (const name of neutral) {
        const code = [`export const state = withState({ ${name}: '' });`, ''].join('\n');
        expect(lint(code, SETTINGS_SECRETS_FILE), name).toEqual([]);
      }
    });

    // 09.7.1-T2: the split is bounded. A name longer than 128 characters is examined in its first
    // and last 64, so the work per identifier stays constant; a secret word outside both windows
    // is not seen, which the README lists as a blind spot rather than implying it is covered.
    it('bounds the name it splits', () => {
      const head = `tokenValue_${'a'.repeat(140)}`;
      const tail = `${'a'.repeat(140)}_tokenValue`;
      const buried = `${'a'.repeat(70)}token${'b'.repeat(70)}`;
      expect(head.length).toBeGreaterThan(128);
      expect(tail.length).toBeGreaterThan(128);
      expect(buried.length).toBeGreaterThan(128);

      for (const name of [head, tail]) {
        const code = [`export const state = withState({ ${name}: '' });`, ''].join('\n');
        expect(ruleIds(code, SETTINGS_SECRETS_FILE), name).toEqual([SECRET_FIELD]);
      }

      const code = [`export const state = withState({ ${buried}: '' });`, ''].join('\n');
      expect(lint(code, SETTINGS_SECRETS_FILE), buried).toEqual([]);
    });

    it('reports a withState type argument literal', () => {
      const code = [
        'export const feature = withState<{ token: string; tokenPrefix: string }>({',
        "  tokenPrefix: '',",
        '});',
        '',
      ].join('\n');
      expect(ruleIds(code, SETTINGS_SECRETS_FILE)).toEqual([SECRET_FIELD]);
    });

    // Position 3: the members of a `/Store$/` class, getters included. The top-level signal and
    // the non-store class are here so the narrow scope is pinned, not implied.
    it('reports a store class field, accessor and getter, and nothing else', () => {
      const code = [
        "import { signal } from '@angular/core';",
        '',
        'export class SecretsStore {',
        "  readonly token = signal('');",
        "  accessor password = '';",
        '  get signingSecret(): string {',
        "    return '';",
        '  }',
        '}',
        '',
        "export const token = signal('');",
        '',
        'export class SecretsPanel {',
        "  readonly token = '';",
        '}',
        '',
      ].join('\n');
      expect(ruleIds(code, SETTINGS_SECRETS_FILE)).toEqual([
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
      ]);
    });

    // A private field and a constructor parameter property are fields of the instance too, and
    // they are where a value is easiest to hide from review.
    it('reports a private field and a constructor parameter property of a store class', () => {
      const code = [
        'export class SecretsStore {',
        "  #token = '';",
        '',
        '  #helper = 0;',
        '',
        '  constructor(private readonly password: string, scope: string) {',
        '    this.#helper = scope.length;',
        '  }',
        '}',
        '',
        'export class SecretsPanel {',
        "  #token = '';",
        '',
        '  constructor(private readonly password: string) {}',
        '}',
        '',
      ].join('\n');
      // The private field, then the parameter property: a plain `scope` parameter and a private
      // field of a non-store class are not fields a Settings template binds as state.
      expect(ruleIds(code, SETTINGS_SECRETS_FILE)).toEqual([SECRET_FIELD, SECRET_FIELD]);
    });

    it('does not report a declaration that is neither a state nor a store', () => {
      const code = [
        'export interface SecretRow {',
        '  token: string;',
        '}',
        '',
        'export type SecretList = { password: string };',
        '',
      ].join('\n');
      expect(lint(code, SETTINGS_SECRETS_FILE)).toEqual([]);
    });

    // Position 4: an inline template is source text, so the binding never reaches a visitor that
    // could see a property name — the template property's value is scanned by content instead.
    it('reports a secret-shaped member read in an inline template', () => {
      const templates = [
        '<p>{{ store.token() }}</p>',
        '<p>{{ row.secret }}</p>',
        '<input [value]="x.signing_secret" />',
        '<p>{{ store.plaintext }}</p>',
        '<p>{{ a.b.token }}</p>',
        '<p>{{ store?.token }}</p>',
        '<p>{{ rows[0]?.secret }}</p>',
      ];
      for (const template of templates) {
        const code = [
          '@Component({',
          "  selector: 'app-secrets',",
          `  template: \`${template}\`,`,
          '})',
          'export class SecretsPanel {}',
          '',
        ].join('\n');
        const messages = lint(code, SETTINGS_SECRETS_FILE);
        expect(
          messages.map((message) => message.ruleId),
          template,
        ).toEqual([SECRET_FIELD]);
        expect(messages[0]?.message).toContain(
          'keep the value server-side or in the one-time reveal component',
        );
      }
    });

    // The copy of a secret row mentions the word; a sentence period is not a member read, and a
    // metadata binding next to it is not a bare read of a value (queue F16 — the bare read of a
    // secret-shaped field is now reported, see the `{{ token }}` cases below).
    it('accepts prose around the word in an inline template', () => {
      const prose = [
        'Rotate the key. Token is shown once.',
        'Copy it now. Secret values are hidden.',
        'Format, e.g. token — see the column.',
      ];
      for (const sentence of prose) {
        const code = [
          '@Component({',
          "  selector: 'app-secrets',",
          `  template: \`<p>${sentence}</p><p>{{ tokenPrefix }}</p>\`,`,
          '})',
          'export class SecretsPanel {}',
          '',
        ].join('\n');
        expect(lint(code, SETTINGS_SECRETS_FILE), sentence).toEqual([]);
      }

      // A member read glued to its dot is still reported, and the `(` of a call, the `]` of an
      // index or the `?` of an optional chain is a member in front of the dot too.
      for (const read of [
        '{{ store.token }}',
        '{{ rows[0].secret }}',
        '{{ store.token() }}',
        '{{ store?.token }}',
      ]) {
        const code = [
          '@Component({',
          "  selector: 'app-secrets',",
          `  template: \`<p>${read}</p>\`,`,
          '})',
          'export class SecretsPanel {}',
          '',
        ].join('\n');
        expect(ruleIds(code, SETTINGS_SECRETS_FILE), read).toEqual([SECRET_FIELD]);
      }
    });

    it('accepts a metadata-only inline template', () => {
      const code = [
        '@Component({',
        "  selector: 'app-secrets',",
        '  template: `<p>{{ row.token_prefix }}</p>',
        '    <p>{{ store.rotationState() }} — {{ row.last4 }}</p>',
        '    <input [value]="x.secretRef" />',
        '    <p>Token rotated</p>`,',
        "  styleUrl: './x.css',",
        '})',
        'export class SecretsPanel {}',
        '',
      ].join('\n');
      expect(lint(code, SETTINGS_SECRETS_FILE)).toEqual([]);
    });

    // 09.7.1-T3 (queue F16): a `templateUrl` file used to be a documented blind spot. The rule
    // now resolves the constant URL against the linted file's own directory and reads the file —
    // here the fixture is linted under a path INSIDE the fixture directory, so the `.html.txt`
    // next to it is the file the URL names. Reported once, on the `templateUrl` property.
    it('reports a secret-shaped member read in a templateUrl file', () => {
      const messages = lintSecretAt(
        secretFixture('failing-template-url.ts.txt'),
        secretFixturePath('failing-template-url.ts'),
      );
      expect(messages.map((message) => message.ruleId)).toEqual([SECRET_FIELD]);
      expect(quoted('Do not bind ', messages)).toEqual(['token']);
      expect(messages[0]?.message).toContain(
        'keep the value server-side or in the one-time reveal component',
      );
    });

    // The same file position with a BARE read, and prose above it that mentions the word: the
    // interpolation is reported, the sentence is not.
    it('reports a bare read in a templateUrl file', () => {
      const messages = lintSecretAt(
        secretFixture('failing-template-url-bare.ts.txt'),
        secretFixturePath('failing-template-url-bare.ts'),
      );
      expect(messages.map((message) => message.ruleId)).toEqual([SECRET_FIELD]);
      expect(quoted('Do not bind ', messages)).toEqual(['token']);
    });

    // A binding VALUE in a `templateUrl` file, in a template that holds nothing else, so the
    // `[value]` read is the one whose name the message carries.
    it('reports a bare binding value in a templateUrl file', () => {
      const messages = lintSecretAt(
        secretFixture('failing-template-url-binding.ts.txt'),
        secretFixturePath('failing-template-url-binding.ts'),
      );
      expect(messages.map((message) => message.ruleId)).toEqual([SECRET_FIELD]);
      expect(quoted('Do not bind ', messages)).toEqual(['signingSecret']);
    });

    // A bare `Linter` hands the rule the filename it was given: an absolute one under the CLI, and
    // a relative one here. A relative filename is resolved against `process.cwd()`, so the same
    // fixture reports the same finding either way.
    it('resolves a templateUrl from an absolute and from a relative filename', () => {
      const code = secretFixture('failing-template-url.ts.txt');
      const absolute = secretFixturePath('failing-template-url.ts');
      expect(lintSecretAt(code, absolute).map((message) => message.ruleId)).toEqual([SECRET_FIELD]);
      expect(
        lintSecretAt(code, relative(process.cwd(), absolute)).map((message) => message.ruleId),
      ).toEqual([SECRET_FIELD]);
    });

    // A metadata-only file, a file that does not exist, a URL with a scheme, an absolute path and
    // a URL that is not a constant string: the rule reports nothing and never throws.
    it('accepts a templateUrl that is missing, remote, absolute or not constant', () => {
      expect(
        lintSecretAt(
          secretFixture('passing-template-url.ts.txt'),
          secretFixturePath('passing-template-url.ts'),
        ),
      ).toEqual([]);
    });

    // The read is bounded: a file over `MAX_TEMPLATE_CHARS` is not read at all, so a violation in
    // it is not reported and a huge file cannot slow the lint run down. The file just under the
    // bound reports, which is what proves the size is what skipped the oversized one.
    it('skips a templateUrl file over the read bound', () => {
      const directory = mkdtempSync(join(tmpdir(), 'evidgate-template-url-'));
      const component = (templateUrl: string): string =>
        [
          '@Component({',
          "  selector: 'app-secrets',",
          `  templateUrl: '${templateUrl}',`,
          '})',
          'export class SecretsPanel {}',
          '',
        ].join('\n');
      try {
        writeFileSync(
          join(directory, 'oversized.html'),
          `{{ token }}${'x'.repeat(300_000)}`,
          'utf8',
        );
        const oversizedFile = join(directory, 'oversized.ts');
        writeFileSync(oversizedFile, component('./oversized.html'), 'utf8');
        expect(lintSecretAt(readFileSync(oversizedFile, 'utf8'), oversizedFile)).toEqual([]);

        writeFileSync(join(directory, 'small.html'), '{{ token }}', 'utf8');
        const smallFile = join(directory, 'small.ts');
        writeFileSync(smallFile, component('./small.html'), 'utf8');
        expect(
          lintSecretAt(readFileSync(smallFile, 'utf8'), smallFile).map((message) => message.ruleId),
        ).toEqual([SECRET_FIELD]);
      } finally {
        rmSync(directory, { recursive: true, force: true });
      }
    });

    // Position 6 in an inline template: a bare interpolation next to a bare binding value, and
    // the binding value on its own.
    it('reports a bare interpolation and a bare binding value in an inline template', () => {
      const code = [
        '@Component({',
        "  selector: 'app-secrets',",
        `  template: '<p>{{ token }}</p><input [value]="signingSecret" />',`,
        '})',
        'export class SecretsPanel {}',
        '',
      ].join('\n');
      const messages = lint(code, SETTINGS_SECRETS_FILE);
      expect(messages.map((message) => message.ruleId)).toEqual([SECRET_FIELD]);
      expect(quoted('Do not bind ', messages)).toEqual(['token']);

      const bindingOnly = [
        '@Component({',
        "  selector: 'app-secrets',",
        `  template: '<input [value]="signingSecret" />',`,
        '})',
        'export class SecretsPanel {}',
        '',
      ].join('\n');
      expect(quoted('Do not bind ', lint(bindingOnly, SETTINGS_SECRETS_FILE))).toEqual([
        'signingSecret',
      ]);
    });

    // Every expression kind Angular evaluates, one component per kind: an interpolation, a
    // property binding on its own, an `@if` condition, the right-hand side of an `@let`, the
    // iterated expression of an `@for`, a structural directive and an `@switch` subject.
    it('reports a bare read in a block header and a structural directive', () => {
      const messages = lint(secretFixture('failing-template-bare.ts.txt'), SETTINGS_SECRETS_FILE);
      expect(messages.map((message) => message.ruleId)).toEqual([
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
      ]);
      expect(quoted('Do not bind ', messages)).toEqual([
        'token',
        'signingSecret',
        'password',
        'signingSecret',
        'pendingToken',
        'secretValue',
        'credential',
      ]);
    });

    // A member read of a metadata name, an `@for` alias, a template ref, a quoted string, an
    // object key, an `@let` declaration, a presence name and HTML text stay legal.
    it('accepts metadata, aliased and quoted template bindings', () => {
      expect(
        lintSecretAt(
          secretFixture('passing-templates.ts.txt'),
          secretFixturePath('passing-templates.ts'),
        ),
      ).toEqual([]);
    });

    // Position 7: a one-time result type in state, at every reported position — a `…State` member,
    // a plain annotated field, an accessor, a `signal`/`computed` type argument, a constructor
    // parameter property, a `…Store` member and a `withState` type argument.
    it('reports a secret-carrying result type held in state', () => {
      const messages = lint(secretFixture('failing-result-type.ts.txt'), SETTINGS_SECRETS_FILE);
      expect(messages.map((message) => message.ruleId)).toEqual([
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
        SECRET_FIELD,
      ]);
      expect(quoted('Do not type Settings state with ', messages)).toEqual([
        'IssuedInvite',
        'InviteIssuedView',
        'IssuedInvite',
        'RotationResult',
        'RotationResult',
        'IngestTokenCreatedResponse',
        'CiBindingCreatedResponse',
        'CiBindingCreatedResponse',
      ]);
      for (const message of messages) {
        expect(message.message).toContain('may only be a RETURN value, never state');
      }
    });

    // The sanctioned shape: a method that RETURNS one, a parameter, a local variable, the
    // declaration of the type itself, and a component class (which is not a store).
    it('accepts a secret-carrying result type in a return value, a parameter and a local', () => {
      expect(lint(secretFixture('passing-result-type.ts.txt'), SETTINGS_SECRETS_FILE)).toEqual([]);
    });

    // The two reviewed exemptions hold for the new positions as well: a spec has to spell the
    // name, and the reveal component is the one place allowed to hold a value.
    it('exempts a spec file and the reveal component from the new positions', () => {
      for (const fixture of [
        'failing-template-bare.ts.txt',
        'failing-result-type.ts.txt',
        'failing-template-url.ts.txt',
      ]) {
        expect(
          ruleIds(
            secretFixture(fixture),
            'apps/web/src/app/features/settings/secrets/secrets.store.spec.ts',
          ),
          fixture,
        ).toEqual([]);
        expect(
          ruleIds(
            secretFixture(fixture),
            'apps/web/src/app/features/settings/invites/reveal/invite-reveal.component.ts',
          ),
          fixture,
        ).toEqual([]);
      }
    });
  });
});
